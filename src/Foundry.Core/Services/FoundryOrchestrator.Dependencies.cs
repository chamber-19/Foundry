using Foundry.Models;

namespace Foundry.Services;

public sealed partial class FoundryOrchestrator
{
    public Task<DependencyMonitorResult> PollDependenciesAsync(CancellationToken cancellationToken = default) =>
        _dependencyMonitorService.PollAsync(cancellationToken);

    public IReadOnlyList<FoundryNotification> ListNotifications(bool pendingOnly = false, int limit = 50) =>
        _notificationStore.List(pendingOnly, limit);

    public FoundryNotification? MarkNotificationDelivered(string id, string? deliveredTo = null) =>
        _notificationStore.MarkDelivered(id, deliveredTo);

    public async Task<object> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var installed = await _modelProvider.GetInstalledModelsAsync(cancellationToken);
        return new
        {
            provider = _modelProvider.ProviderId,
            chatModel = _settings.OllamaChatModel,
            embeddingModel = _settings.OllamaEmbeddingModel,
            installedModels = installed,
        };
    }
}
