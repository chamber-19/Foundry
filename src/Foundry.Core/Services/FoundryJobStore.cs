using Foundry.Models;

namespace Foundry.Services;

/// <summary>
/// Manages job lifecycle persistence via LiteDB.
/// Thread-safe for concurrent reads; LiteDB serializes writes.
/// </summary>
public sealed class FoundryJobStore
{
    private readonly FoundryDatabase _db;

    // Process-scoped counter that gives each new job a monotonically increasing sequence
    // number used as a sort key in DequeueNext() for deterministic FIFO ordering.
    // Initialized on first store construction from the maximum SequenceNumber found in the
    // database so that new jobs enqueued after a process restart are always ordered after
    // any unprocessed jobs that remain from the previous run.
    private static int _nextSequenceNumber;

    public FoundryJobStore(FoundryDatabase db)
    {
        _db = db;
        InitializeSequenceCounter();
    }

    // Atomically advances _nextSequenceNumber to at least the maximum SequenceNumber already
    // stored in the database.  Safe to call from multiple concurrent constructors.
    private void InitializeSequenceCounter()
    {
        var maxInDb = _db.Jobs.Query()
            .Select(j => j.SequenceNumber)
            .ToList()
            .DefaultIfEmpty(0)
            .Max();

        // CAS loop: update only if the current static value is less than what the DB holds.
        int current;
        do
        {
            current = _nextSequenceNumber;
            if (maxInDb <= current) break;
        }
        while (Interlocked.CompareExchange(ref _nextSequenceNumber, maxInDb, current) != current);
    }

    /// <summary>
    /// Creates a new job record in queued state.
    /// </summary>
    public FoundryJob Enqueue(string type, string? requestedBy = null, string? requestPayload = null)
    {
        var job = new FoundryJob
        {
            Id = Guid.NewGuid().ToString(),
            Type = type,
            Status = FoundryJobStatus.Queued,
            CreatedAt = DateTimeOffset.Now,
            SequenceNumber = Interlocked.Increment(ref _nextSequenceNumber),
            RequestedBy = requestedBy,
            RequestPayload = requestPayload,
        };

        _db.Jobs.Insert(job);
        return job;
    }

    /// <summary>
    /// Retrieves a job by its ID.
    /// </summary>
    public FoundryJob? GetById(string jobId)
    {
        return _db.Jobs.FindOne(j => j.Id == jobId);
    }

    /// <summary>
    /// Lists recent jobs, most recent first.
    /// </summary>
    public IReadOnlyList<FoundryJob> ListRecent(int count = 50)
    {
        return _db.Jobs.Query()
            .OrderByDescending(j => j.CreatedAt)
            .Limit(count)
            .ToList();
    }

    /// <summary>
    /// Dequeues the next queued job (oldest first, FIFO).
    /// Returns null if no queued jobs exist.
    /// </summary>
    public FoundryJob? DequeueNext()
    {
        // Fetch all queued jobs and select the one with the lowest composite sort key.
        // The sort key is projected once per item (not re-evaluated per comparison).
        //
        // Ordering rules:
        //   - Legacy records (SequenceNumber == 0, predating this field) receive sort key 0
        //     so they are always dequeued before any new record; ties among legacy records
        //     are broken by CreatedAt.
        //   - New records (SequenceNumber > 0) are ordered by their sequence number, which is
        //     a monotonically increasing counter assigned at enqueue time, guaranteeing
        //     stable FIFO ordering even for jobs enqueued within the same clock tick.
        var job = _db.Jobs.Query()
            .Where(j => j.Status == FoundryJobStatus.Queued)
            .ToList()
            .Select(j => new
            {
                Job = j,
                PrimaryKey = j.SequenceNumber > 0 ? (long)j.SequenceNumber : 0L,
                SecondaryKey = j.CreatedAt.UtcTicks,
            })
            .OrderBy(x => x.PrimaryKey)
            .ThenBy(x => x.SecondaryKey)
            .FirstOrDefault()
            ?.Job;

        if (job is null) return null;

        job.Status = FoundryJobStatus.Running;
        job.StartedAt = DateTimeOffset.Now;
        _db.Jobs.Update(job);
        return job;
    }

    /// <summary>
    /// Marks a job as succeeded with a JSON result.
    /// </summary>
    public void MarkSucceeded(string jobId, string? resultJson)
    {
        var job = _db.Jobs.FindOne(j => j.Id == jobId);
        if (job is null) return;

        job.Status = FoundryJobStatus.Succeeded;
        job.CompletedAt = DateTimeOffset.Now;
        job.ResultJson = resultJson;
        _db.Jobs.Update(job);
    }

    /// <summary>
    /// Marks a job as failed with an error message.
    /// </summary>
    public void MarkFailed(string jobId, string error)
    {
        var job = _db.Jobs.FindOne(j => j.Id == jobId);
        if (job is null) return;

        job.Status = FoundryJobStatus.Failed;
        job.CompletedAt = DateTimeOffset.Now;
        job.Error = error;
        _db.Jobs.Update(job);
    }

    /// <summary>
    /// Recovers jobs that were left in Running status (e.g., after a broker crash/restart).
    /// Any Running job with StartedAt older than the staleThreshold is marked as Failed.
    /// </summary>
    public int RecoverStaleJobs(TimeSpan staleThreshold)
    {
        var cutoff = DateTimeOffset.Now - staleThreshold;
        var staleJobs = _db.Jobs.Query()
            .Where(j => j.Status == FoundryJobStatus.Running && j.StartedAt != null && j.StartedAt < cutoff)
            .ToList();

        foreach (var job in staleJobs)
        {
            job.Status = FoundryJobStatus.Failed;
            job.CompletedAt = DateTimeOffset.Now;
            job.Error = $"Recovered after broker restart. Job was running since {job.StartedAt:O} and exceeded stale threshold of {staleThreshold.TotalMinutes} minutes.";
            _db.Jobs.Update(job);
        }

        return staleJobs.Count;
    }

    /// <summary>
    /// Deletes a completed job by ID. Only Succeeded or Failed jobs can be deleted.
    /// Returns true if the job was found and deleted, false otherwise.
    /// </summary>
    public bool DeleteById(string jobId)
    {
        var job = _db.Jobs.FindOne(j => j.Id == jobId);
        if (job is null) return false;
        if (job.Status is not (FoundryJobStatus.Succeeded or FoundryJobStatus.Failed))
            return false;

        return _db.Jobs.Delete(job.Id);
    }

    /// <summary>
    /// Deletes completed jobs older than the specified cutoff.
    /// Only removes jobs in Succeeded or Failed status.
    /// Returns the number of jobs deleted.
    /// </summary>
    public int DeleteOlderThan(DateTimeOffset cutoff)
    {
        var oldJobs = _db.Jobs.Query()
            .Where(j =>
                (j.Status == FoundryJobStatus.Succeeded || j.Status == FoundryJobStatus.Failed)
                && j.CreatedAt < cutoff)
            .ToList();

        foreach (var job in oldJobs)
        {
            _db.Jobs.Delete(job.Id);
        }

        return oldJobs.Count;
    }

    /// <summary>
    /// Lists jobs filtered by status, most recent first.
    /// </summary>
    public IReadOnlyList<FoundryJob> ListByStatus(string status, int count = 50)
    {
        return _db.Jobs.Query()
            .Where(j => j.Status == status)
            .OrderByDescending(j => j.CreatedAt)
            .Limit(count)
            .ToList();
    }

    /// <summary>
    /// Returns the total number of jobs in the store.
    /// </summary>
    public int GetTotalCount()
    {
        return _db.Jobs.Count();
    }

    /// <summary>
    /// Returns the count of jobs in a specific status.
    /// </summary>
    public int GetCountByStatus(string status)
    {
        return _db.Jobs.Count(j => j.Status == status);
    }

    /// <summary>
    /// Returns the average duration (in seconds) for succeeded jobs,
    /// or null if there are no succeeded jobs with timing data.
    /// </summary>
    public double? GetAverageDuration()
    {
        var succeeded = _db.Jobs.Query()
            .Where(j => j.Status == FoundryJobStatus.Succeeded
                        && j.StartedAt != null
                        && j.CompletedAt != null)
            .ToList();

        if (succeeded.Count == 0) return null;

        var total = succeeded.Sum(j => (j.CompletedAt!.Value - j.StartedAt!.Value).TotalSeconds);
        return total / succeeded.Count;
    }

    /// <summary>
    /// Returns the number of completed (succeeded or failed) jobs
    /// since the specified cutoff time.
    /// </summary>
    public int GetCompletedSince(DateTimeOffset cutoff)
    {
        return _db.Jobs.Count(j =>
            (j.Status == FoundryJobStatus.Succeeded || j.Status == FoundryJobStatus.Failed)
            && j.CompletedAt != null
            && j.CompletedAt >= cutoff);
    }

    /// <summary>
    /// Returns a structured metrics snapshot for the job system.
    /// </summary>
    public FoundryJobMetrics GetMetrics()
    {
        var now = DateTimeOffset.Now;
        return new FoundryJobMetrics
        {
            TotalJobs = GetTotalCount(),
            QueuedCount = GetCountByStatus(FoundryJobStatus.Queued),
            RunningCount = GetCountByStatus(FoundryJobStatus.Running),
            SucceededCount = GetCountByStatus(FoundryJobStatus.Succeeded),
            FailedCount = GetCountByStatus(FoundryJobStatus.Failed),
            AverageDurationSeconds = GetAverageDuration(),
            CompletedLastHour = GetCompletedSince(now.AddHours(-1)),
            CompletedLastDay = GetCompletedSince(now.AddDays(-1)),
        };
    }
}
