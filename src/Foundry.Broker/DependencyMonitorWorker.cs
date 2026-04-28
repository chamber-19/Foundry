using Foundry.Services;

namespace Foundry.Broker;

internal sealed class DependencyMonitorWorker : BackgroundService
{
    private readonly FoundryOrchestrator _orchestrator;
    private readonly ILogger<DependencyMonitorWorker> _logger;

    public DependencyMonitorWorker(
        FoundryOrchestrator orchestrator,
        ILogger<DependencyMonitorWorker> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _orchestrator.PollDependenciesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dependency monitor poll failed.");
            }

            await Task.Delay(_orchestrator.DependencyPollingInterval, stoppingToken);
        }
    }
}
