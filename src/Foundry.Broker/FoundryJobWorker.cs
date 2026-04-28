using System.Text.Json;
using Foundry.Models;
using Foundry.Services;

namespace Foundry.Broker;

/// <summary>
/// Background worker that polls for queued jobs and executes them.
/// Runs one job at a time to match the existing _mlGate concurrency model.
/// </summary>
public sealed class FoundryJobWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultJobTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleJobThreshold = TimeSpan.FromMinutes(10);

    private readonly FoundryOrchestrator _orchestrator;
    private readonly ILogger<FoundryJobWorker> _logger;
    private readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    public FoundryJobWorker(
        FoundryOrchestrator orchestrator,
        ILogger<FoundryJobWorker> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recover any jobs left in Running state from a prior crash/restart
        var recoveredCount = _orchestrator.JobStore.RecoverStaleJobs(StaleJobThreshold);
        if (recoveredCount > 0)
        {
            _logger.LogWarning("Recovered {Count} stale job(s) from prior broker session.", recoveredCount);
        }

        _logger.LogInformation("FoundryJobWorker started. Polling for queued jobs every {Interval}s.", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = _orchestrator.JobStore.DequeueNext();
                if (job is not null)
                {
                    await ExecuteJobAsync(job, stoppingToken);
                }
                else
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FoundryJobWorker encountered an unexpected error.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        _logger.LogInformation("FoundryJobWorker stopped.");
    }

    private async Task ExecuteJobAsync(FoundryJob job, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Executing job {JobId} of type {JobType}.", job.Id, job.Type);
        using var timeoutCts = new CancellationTokenSource(DefaultJobTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);
        var ct = linkedCts.Token;

        try
        {
            var resultJson = job.Type switch
            {
                FoundryJobType.KnowledgeIndex => await ExecuteKnowledgeIndexAsync(ct),
                FoundryJobType.DailyRun => await ExecuteDailyRunAsync(ct),
                _ => throw new InvalidOperationException($"Unknown job type: {job.Type}"),
            };

            _orchestrator.JobStore.MarkSucceeded(job.Id, resultJson);
            _logger.LogInformation("Job {JobId} succeeded.", job.Id);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var message = $"Job timed out after {DefaultJobTimeout.TotalMinutes} minutes.";
            _orchestrator.JobStore.MarkFailed(job.Id, message);
            _logger.LogWarning("Job {JobId} timed out after {Timeout}.", job.Id, DefaultJobTimeout);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _orchestrator.JobStore.MarkFailed(job.Id, "Job cancelled due to broker shutdown.");
            _logger.LogWarning("Job {JobId} cancelled due to shutdown.", job.Id);
        }
        catch (Exception ex)
        {
            _orchestrator.JobStore.MarkFailed(job.Id, ex.Message);
            _logger.LogWarning(ex, "Job {JobId} failed.", job.Id);
        }
    }

    private async Task<string> ExecuteKnowledgeIndexAsync(CancellationToken ct)
    {
        var result = await _orchestrator.RunKnowledgeIndexAsync(ct);
        return JsonSerializer.Serialize(result, _jsonOptions);
    }

    private async Task<string> ExecuteDailyRunAsync(CancellationToken ct)
    {
        var result = await _orchestrator.RunDailyWorkflowAsync(ct);
        return JsonSerializer.Serialize(result, _jsonOptions);
    }
}
