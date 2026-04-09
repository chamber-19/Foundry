using Foundry.Models;

namespace Foundry.Services;

/// <summary>
/// Manages job schedule persistence via LiteDB.
/// Provides CRUD operations and schedule evaluation for the scheduler worker.
/// </summary>
public sealed class JobSchedulerStore
{
    private readonly FoundryDatabase _db;

    public JobSchedulerStore(FoundryDatabase db)
    {
        _db = db;
    }

    /// <summary>
    /// Creates a new schedule. Computes NextRunAt from the cron expression.
    /// </summary>
    public JobSchedule Create(JobSchedule schedule)
    {
        schedule.Id = Guid.NewGuid().ToString();
        schedule.CreatedAt = DateTimeOffset.Now;
        schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, DateTimeOffset.Now);
        _db.JobSchedules.Insert(schedule);
        return schedule;
    }

    /// <summary>
    /// Retrieves a schedule by ID.
    /// </summary>
    public JobSchedule? GetById(string id)
    {
        return _db.JobSchedules.FindOne(s => s.Id == id);
    }

    /// <summary>
    /// Lists all schedules, most recently created first.
    /// </summary>
    public IReadOnlyList<JobSchedule> ListAll()
    {
        return _db.JobSchedules.Query()
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Updates a schedule (name, enabled, cron expression, etc.).
    /// Recomputes NextRunAt if the cron expression changed.
    /// </summary>
    public JobSchedule? Update(string id, Action<JobSchedule> apply)
    {
        var schedule = _db.JobSchedules.FindOne(s => s.Id == id);
        if (schedule is null) return null;

        var oldCron = schedule.CronExpression;
        apply(schedule);

        if (schedule.CronExpression != oldCron)
        {
            schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, DateTimeOffset.Now);
        }

        _db.JobSchedules.Update(schedule);
        return schedule;
    }

    /// <summary>
    /// Deletes a schedule by ID. Returns true if found and deleted.
    /// </summary>
    public bool Delete(string id)
    {
        var schedule = _db.JobSchedules.FindOne(s => s.Id == id);
        if (schedule is null) return false;
        return _db.JobSchedules.Delete(schedule.Id);
    }

    /// <summary>
    /// Returns all enabled schedules whose NextRunAt is at or before the given time.
    /// </summary>
    public IReadOnlyList<JobSchedule> GetDueSchedules(DateTimeOffset asOf)
    {
        return _db.JobSchedules.Query()
            .Where(s => s.Enabled && s.NextRunAt != null && s.NextRunAt <= asOf)
            .ToList();
    }

    /// <summary>
    /// Marks a schedule as having just run and computes the next run time.
    /// </summary>
    public void MarkRun(string id, DateTimeOffset ranAt)
    {
        var schedule = _db.JobSchedules.FindOne(s => s.Id == id);
        if (schedule is null) return;

        schedule.LastRunAt = ranAt;
        schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, ranAt);
        _db.JobSchedules.Update(schedule);
    }

    /// <summary>
    /// Parses a cron expression or simple interval and computes the next run time after <paramref name="after"/>.
    /// Supports:
    /// - Simple intervals: "every 30m", "every 2h", "every 1d"
    /// - 5-part cron: "minute hour day month weekday" (e.g. "0 8 * * *" = daily at 8 AM)
    /// Returns null if the expression cannot be parsed.
    /// </summary>
    public static DateTimeOffset? ComputeNextRun(string cronExpression, DateTimeOffset after)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
            return null;

        var trimmed = cronExpression.Trim();

        // Simple interval: "every Nm", "every Nh", "every Nd"
        if (trimmed.StartsWith("every ", StringComparison.OrdinalIgnoreCase))
        {
            var intervalPart = trimmed["every ".Length..].Trim();
            if (TryParseInterval(intervalPart, out var interval))
            {
                return after.Add(interval);
            }
            return null;
        }

        // 5-part cron expression
        return ComputeNextCronRun(trimmed, after);
    }

    private static bool TryParseInterval(string value, out TimeSpan interval)
    {
        interval = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
            return false;

        var unit = value[^1];
        if (!int.TryParse(value[..^1], out var amount) || amount <= 0)
            return false;

        interval = unit switch
        {
            'm' or 'M' => TimeSpan.FromMinutes(amount),
            'h' or 'H' => TimeSpan.FromHours(amount),
            'd' or 'D' => TimeSpan.FromDays(amount),
            _ => TimeSpan.Zero,
        };

        return interval > TimeSpan.Zero;
    }

    /// <summary>
    /// Parses a 5-part cron expression and computes the next matching time after <paramref name="after"/>.
    /// Supports: numbers, '*' (any), and comma-separated values.
    /// Does not support ranges (-), step values (/), or complex expressions.
    /// </summary>
    private static DateTimeOffset? ComputeNextCronRun(string cron, DateTimeOffset after)
    {
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return null;

        if (!ParseCronField(parts[0], 0, 59, out var minutes)) return null;
        if (!ParseCronField(parts[1], 0, 23, out var hours)) return null;
        if (!ParseCronField(parts[2], 1, 31, out var daysOfMonth)) return null;
        if (!ParseCronField(parts[3], 1, 12, out var months)) return null;
        if (!ParseCronField(parts[4], 0, 6, out var daysOfWeek)) return null;

        // Start searching from the next minute after 'after'
        var candidate = new DateTimeOffset(
            after.Year, after.Month, after.Day,
            after.Hour, after.Minute, 0, after.Offset).AddMinutes(1);

        // Search up to 366 days (covers a full year cycle)
        var limit = candidate.AddDays(366);

        while (candidate < limit)
        {
            if (months.Contains(candidate.Month)
                && daysOfMonth.Contains(candidate.Day)
                && daysOfWeek.Contains((int)candidate.DayOfWeek)
                && hours.Contains(candidate.Hour)
                && minutes.Contains(candidate.Minute))
            {
                return candidate;
            }

            candidate = candidate.AddMinutes(1);

            // Skip ahead if month doesn't match
            if (!months.Contains(candidate.Month))
            {
                candidate = AdvanceToNextMonth(candidate, months);
                continue;
            }

            // Skip ahead if day doesn't match
            if (!daysOfMonth.Contains(candidate.Day) || !daysOfWeek.Contains((int)candidate.DayOfWeek))
            {
                if (candidate.Hour != 0 || candidate.Minute != 0)
                {
                    candidate = new DateTimeOffset(candidate.Date.AddDays(1), candidate.Offset);
                }
                continue;
            }

            // Skip ahead if hour doesn't match
            if (!hours.Contains(candidate.Hour))
            {
                candidate = new DateTimeOffset(
                    candidate.Year, candidate.Month, candidate.Day,
                    candidate.Hour, 0, 0, candidate.Offset).AddHours(1);
            }
        }

        return null;
    }

    private static DateTimeOffset AdvanceToNextMonth(DateTimeOffset current, HashSet<int> months)
    {
        var candidate = new DateTimeOffset(
            current.Year, current.Month, 1, 0, 0, 0, current.Offset).AddMonths(1);
        var limit = candidate.AddMonths(12);
        while (candidate < limit && !months.Contains(candidate.Month))
        {
            candidate = candidate.AddMonths(1);
        }
        return candidate;
    }

    private static bool ParseCronField(string field, int min, int max, out HashSet<int> values)
    {
        values = new HashSet<int>();

        if (field == "*")
        {
            for (int i = min; i <= max; i++)
                values.Add(i);
            return true;
        }

        foreach (var part in field.Split(','))
        {
            if (int.TryParse(part.Trim(), out var val) && val >= min && val <= max)
            {
                values.Add(val);
            }
            else
            {
                return false;
            }
        }

        return values.Count > 0;
    }
}
