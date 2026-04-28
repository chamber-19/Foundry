using Foundry.Models;

namespace Foundry.Services;

// Health checks, broker state snapshot, and internal initialization helpers.
public sealed partial class FoundryOrchestrator
{
    /// <summary>
    /// Returns a detailed health report for all Foundry subsystems (Ollama,
    /// Python, LiteDB, job worker).
    /// </summary>
    public async Task<FoundryHealthReport> GetDetailedHealthAsync(CancellationToken cancellationToken = default)
    {
        var report = new FoundryHealthReport();

        try
        {
            var reachable = await _modelProvider.PingAsync(cancellationToken);
            report.Ollama = reachable
                ? new SubsystemHealth { Status = HealthStatus.Ok }
                : new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = "Ollama did not respond to ping." };
        }
        catch (Exception ex)
        {
            report.Ollama = new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = ex.Message };
        }

        try
        {
            var version = await _processRunner.CheckPythonAsync(cancellationToken);
            report.Python = version is not null
                ? new SubsystemHealth { Status = HealthStatus.Ok, Detail = version }
                : new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = "Python 3 not found on PATH." };
        }
        catch (Exception ex)
        {
            report.Python = new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = ex.Message };
        }

        try
        {
            _foundryDatabase.Jobs.Count();
            report.LiteDB = new SubsystemHealth { Status = HealthStatus.Ok };
        }
        catch (Exception ex)
        {
            report.LiteDB = new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = ex.Message };
        }

        try
        {
            var runningJobs = _jobStore.ListByStatus(FoundryJobStatus.Running);
            var stuckCount = runningJobs.Count(j =>
                j.StartedAt.HasValue && (DateTimeOffset.Now - j.StartedAt.Value).TotalMinutes > 10);

            report.JobWorker = stuckCount > 0
                ? new SubsystemHealth
                {
                    Status = HealthStatus.Degraded,
                    Detail = $"{stuckCount} job(s) running for more than 10 minutes."
                }
                : new SubsystemHealth
                {
                    Status = HealthStatus.Ok,
                    Detail = $"{runningJobs.Count} job(s) currently running."
                };
        }
        catch (Exception ex)
        {
            report.JobWorker = new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = ex.Message };
        }

        var statuses = new[] { report.Ollama.Status, report.Python.Status, report.LiteDB.Status, report.JobWorker.Status };
        if (statuses.Any(s => s == HealthStatus.Unavailable))
            report.Overall = HealthStatus.Unavailable;
        else if (statuses.Any(s => s == HealthStatus.Degraded))
            report.Overall = HealthStatus.Degraded;
        else
            report.Overall = HealthStatus.Ok;

        return report;
    }

    /// <summary>
    /// Returns a lightweight health summary suitable for liveness probes.
    /// </summary>
    public async Task<object> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            return new
            {
                status = "ok",
                broker = _brokerMetadata.BaseUrl,
                provider = _modelProvider.ProviderId,
                providerReady = _installedModelCache.Count > 0,
                refreshedAt = _lastRefreshAt,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the full broker state snapshot including provider info, agent
    /// broker status, and dependency monitor settings.
    /// </summary>
    public async Task<FoundryBrokerState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            return BuildStateLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    private FoundryBrokerState BuildStateLocked()
    {
        return new FoundryBrokerState
        {
            Broker = new FoundryBrokerStatusSection
            {
                Host = _brokerMetadata.Host,
                Port = _brokerMetadata.Port,
                BaseUrl = _brokerMetadata.BaseUrl,
                LoopbackOnly = _brokerMetadata.LoopbackOnly,
                StartedAt = _brokerMetadata.StartedAt,
                LastRefreshAt = _lastRefreshAt,
            },
            Provider = new FoundryProviderSection
            {
                Ready = _installedModelCache.Count > 0,
                InstalledModelCount = _installedModelCache.Count,
                InstalledModels = _installedModelCache,
            },
            AgentBroker = new FoundryAgentBrokerSection
            {
                Enabled = true,
                Agents = _dispatcher.Agents.Select(agent => agent.Name).ToList(),
            },
            DependencyMonitor = new FoundryDependencyMonitorSection
            {
                Enabled = true,
                RepositoryCount = _dependencyMonitorService.ConfiguredRepositories.Count,
                Repositories = _dependencyMonitorService.ConfiguredRepositories,
                PollingIntervalMinutes = (int)_dependencyMonitorService.PollingInterval.TotalMinutes,
                PendingNotificationCount = _notificationStore.List(pendingOnly: true, limit: 100).Count,
            },
        };
    }

    // --- Private initialization helpers ---

    private async Task EnsureInitializedLockedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await RefreshContextLockedAsync(cancellationToken);
        _initialized = true;
    }

    private async Task RefreshContextLockedAsync(CancellationToken cancellationToken)
    {
        _installedModelCache = await LoadInstalledModelsSafeAsync(cancellationToken);
        _learningLibrary = await LoadLearningLibrarySafeAsync(cancellationToken);
        _lastRefreshAt = DateTimeOffset.Now;
    }

    private async Task<IReadOnlyList<string>> LoadInstalledModelsSafeAsync(CancellationToken cancellationToken)
    {
        return await RunWithTimeoutFallbackAsync(
            () => _modelProvider.GetInstalledModelsAsync(cancellationToken),
            InstalledModelsLoadTimeout,
            Array.Empty<string>());
    }

    private async Task<LearningLibrary> LoadLearningLibrarySafeAsync(CancellationToken cancellationToken)
    {
        return await RunWithTimeoutFallbackAsync(
            () => _knowledgeImportService.LoadAsync(_knowledgeLibraryPath, _additionalKnowledgePaths),
            LearningLibraryLoadTimeout,
            new LearningLibrary());
    }

    private static async Task<T> RunWithTimeoutFallbackAsync<T>(
        Func<Task<T>> action,
        TimeSpan timeout,
        T fallback)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await action();
        }
        catch
        {
            return fallback;
        }
    }

    private static string ResolveFoundryRootPath(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            if (dir.GetDirectories("Foundry").Length > 0 || dir.GetFiles("Foundry.sln").Length > 0)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return startDirectory;
    }
}
