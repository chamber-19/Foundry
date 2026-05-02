using Foundry.Services;

namespace Foundry.Broker;

/// <summary>
/// Background service that periodically deletes completed jobs older than the
/// configured retention period. Runs once per day.
/// </summary>
public sealed class JobRetentionWorker : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);

    private readonly FoundryOrchestrator _orchestrator;
    private readonly ILogger<JobRetentionWorker> _logger;

    public JobRetentionWorker(
        FoundryOrchestrator orchestrator,
        ILogger<JobRetentionWorker> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobRetentionWorker started. Will clean up completed jobs older than {Days} day(s) every 24 hours.",
            _orchestrator.JobRetentionDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTimeOffset.Now.AddDays(-_orchestrator.JobRetentionDays);
                var deleted = _orchestrator.JobStore.DeleteOlderThan(cutoff);
                if (deleted > 0)
                {
                    _logger.LogInformation("JobRetentionWorker deleted {Count} completed job(s) older than {Cutoff:O}.",
                        deleted, cutoff);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JobRetentionWorker encountered an error during cleanup.");
            }

            try
            {
                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("JobRetentionWorker stopped.");
    }
}
