using Foundry.Services;

namespace Foundry.Models;

public sealed class FoundryBrokerState
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public FoundryBrokerStatusSection Broker { get; init; } = new();
    public FoundryProviderSection Provider { get; init; } = new();
    public FoundryMLSection ML { get; init; } = new();
}

public sealed class FoundryBrokerStatusSection
{
    public string Status { get; init; } = "ok";
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public bool LoopbackOnly { get; init; } = true;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset LastRefreshAt { get; init; } = DateTimeOffset.Now;
}

public sealed class FoundryProviderSection
{
    public string ActiveProviderId { get; init; } = OllamaService.OllamaProviderId;
    public string ActiveProviderLabel { get; init; } = OllamaService.OllamaProviderLabel;
    public string PrimaryProviderLabel { get; init; } = OllamaService.OllamaProviderLabel;
    public string ConfiguredProviderId { get; init; } = OllamaService.OllamaProviderId;
    public bool Ready { get; init; }
    public int InstalledModelCount { get; init; }
    public IReadOnlyList<string> InstalledModels { get; init; } = Array.Empty<string>();
}

public sealed class FoundryMLSection
{
    public bool Enabled { get; init; }
    public string Summary { get; init; } = "ML pipeline removed. See follow-up cleanup PRs.";
}

public sealed class FoundryLibraryImportResult
{
    public int ImportedCount { get; init; }
    public IReadOnlyList<string> ImportedPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SkippedPaths { get; init; } = Array.Empty<string>();
}
