using Foundry.Services;

namespace Foundry.Broker;

/// <summary>
/// Background service that checks job schedules every minute and enqueues
/// due jobs via FoundryJobStore. Marks each schedule as run and computes the
/// next run time after enqueuing.
/// </summary>
public sealed class JobSchedulerWorker : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly FoundryOrchestrator _orchestrator;
    private readonly ILogger<JobSchedulerWorker> _logger;

    public JobSchedulerWorker(
        FoundryOrchestrator orchestrator,
        ILogger<JobSchedulerWorker> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobSchedulerWorker started. Checking schedules every {Interval}s.", CheckInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;
                var dueSchedules = _orchestrator.SchedulerStore.GetDueSchedules(now);

                foreach (var schedule in dueSchedules)
                {
                    try
                    {
                        _orchestrator.JobStore.Enqueue(
                            schedule.JobType,
                            requestedBy: $"scheduler:{schedule.Name}",
                            requestPayload: schedule.RequestPayload);

                        _orchestrator.SchedulerStore.MarkRun(schedule.Id, now);

                        _logger.LogInformation(
                            "Scheduler enqueued job type '{JobType}' for schedule '{ScheduleName}' ({ScheduleId}).",
                            schedule.JobType, schedule.Name, schedule.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to enqueue scheduled job for schedule '{ScheduleName}' ({ScheduleId}).",
                            schedule.Name, schedule.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JobSchedulerWorker encountered an error during schedule check.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("JobSchedulerWorker stopped.");
    }
}
