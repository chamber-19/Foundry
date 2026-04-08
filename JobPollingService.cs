using System.Net.Http;
using System.Text.Json;
using DailyDesk.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyDesk.Services;

/// <summary>
/// Polls the broker's job status endpoint on behalf of the WPF client.
/// Submits ML requests, receives a job ID, then polls GET /api/jobs/{jobId}
/// until the job reaches a terminal state (succeeded or failed).
/// </summary>
public sealed class JobPollingService
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private const int DefaultMaxAttempts = 150; // 5 minutes at 2-second intervals

    private readonly HttpClient _httpClient;
    private readonly ILogger<JobPollingService> _logger;

    public JobPollingService(
        HttpClient httpClient,
        ILogger<JobPollingService>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger ?? NullLogger<JobPollingService>.Instance;
    }

    /// <summary>
    /// Submits an ML request to the specified endpoint and returns the enqueued job ID.
    /// Returns null if the submission fails.
    /// </summary>
    public async Task<string?> SubmitJobAsync(
        string endpointPath,
        object? requestPayload = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpResponseMessage response;
            if (requestPayload is not null)
            {
                var json = JsonSerializer.Serialize(requestPayload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync(endpointPath, content, cancellationToken);
            }
            else
            {
                response = await _httpClient.PostAsync(endpointPath, null, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Job submission to {Endpoint} returned {Status}.", endpointPath, response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("jobId", out var jobIdElement))
            {
                return jobIdElement.GetString();
            }

            _logger.LogWarning("Job submission response did not contain a jobId.");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Job submission to {Endpoint} failed.", endpointPath);
            return null;
        }
    }

    /// <summary>
    /// Polls GET /api/jobs/{jobId} until the job reaches a terminal state.
    /// Invokes onStatusChange for each status transition.
    /// Returns the final <see cref="JobPollResult"/>.
    /// </summary>
    public async Task<JobPollResult> PollUntilCompleteAsync(
        string jobId,
        Action<JobPollStatus>? onStatusChange = null,
        TimeSpan? pollInterval = null,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        string? lastStatus = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await GetJobStatusAsync(jobId, cancellationToken);
            if (status is null)
            {
                return new JobPollResult
                {
                    JobId = jobId,
                    FinalStatus = OfficeJobStatus.Failed,
                    Error = "Job not found or broker unreachable.",
                };
            }

            if (status.Status != lastStatus)
            {
                lastStatus = status.Status;
                onStatusChange?.Invoke(status);
            }

            if (status.Status is OfficeJobStatus.Succeeded or OfficeJobStatus.Failed)
            {
                return new JobPollResult
                {
                    JobId = jobId,
                    FinalStatus = status.Status,
                    Error = status.Error,
                    CompletedAt = status.CompletedAt,
                };
            }

            await Task.Delay(interval, cancellationToken);
        }

        return new JobPollResult
        {
            JobId = jobId,
            FinalStatus = OfficeJobStatus.Failed,
            Error = $"Polling timed out after {maxAttempts} attempts ({maxAttempts * interval.TotalSeconds:F0}s elapsed).",
        };
    }

    /// <summary>
    /// Fetches the current status of a single job from the broker.
    /// Returns null if the job is not found or the broker is unreachable.
    /// </summary>
    public async Task<JobPollStatus?> GetJobStatusAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/api/jobs/{jobId}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<JobPollStatus>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to poll job {JobId}.", jobId);
            return null;
        }
    }

    /// <summary>
    /// Fetches the result JSON for a completed job.
    /// Returns null if the job has not succeeded or is unreachable.
    /// </summary>
    public async Task<string?> GetJobResultAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/api/jobs/{jobId}/result", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get result for job {JobId}.", jobId);
            return null;
        }
    }

    /// <summary>
    /// Convenience method: submits a job and polls until completion in one call.
    /// Updates the provided <see cref="JobActivityItem"/> through each status transition.
    /// </summary>
    public async Task<JobPollResult> SubmitAndPollAsync(
        string endpointPath,
        object? requestPayload,
        JobActivityItem? activityItem = null,
        TimeSpan? pollInterval = null,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        var jobId = await SubmitJobAsync(endpointPath, requestPayload, cancellationToken);
        if (jobId is null)
        {
            return new JobPollResult
            {
                JobId = string.Empty,
                FinalStatus = OfficeJobStatus.Failed,
                Error = "Job submission failed.",
            };
        }

        return await PollUntilCompleteAsync(
            jobId,
            onStatusChange: status =>
            {
                if (activityItem is not null)
                {
                    activityItem.Status = status.Status;
                    activityItem.Summary = status.Status switch
                    {
                        OfficeJobStatus.Queued => "Waiting in queue...",
                        OfficeJobStatus.Running => "Processing...",
                        OfficeJobStatus.Succeeded => "Completed.",
                        OfficeJobStatus.Failed => status.Error ?? "Job failed.",
                        _ => status.Status,
                    };
                }
            },
            pollInterval: pollInterval,
            maxAttempts: maxAttempts,
            cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Status snapshot returned from a single poll of /api/jobs/{jobId}.
/// </summary>
public sealed class JobPollStatus
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? RequestedBy { get; set; }
}

/// <summary>
/// Final result of a polling session.
/// </summary>
public sealed class JobPollResult
{
    public string JobId { get; set; } = string.Empty;
    public string FinalStatus { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public bool Succeeded =>
        FinalStatus.Equals(OfficeJobStatus.Succeeded, StringComparison.OrdinalIgnoreCase);
}
