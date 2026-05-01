using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundry.Models;
using Foundry.Services;

namespace Foundry.Broker;

internal sealed class DiscordDeliveryWorker : BackgroundService
{
    private const int MaxDeliveryAttempts = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions WebhookJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly FoundryOrchestrator _orchestrator;
    private readonly HttpClient _http = new();
    private readonly ILogger<DiscordDeliveryWorker> _logger;

    public DiscordDeliveryWorker(
        FoundryOrchestrator orchestrator,
        ILogger<DiscordDeliveryWorker> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_orchestrator.DiscordWebhookUrl))
        {
            _logger.LogInformation("Discord delivery disabled: no webhook URL configured.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeliverPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discord delivery cycle failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DeliverPendingAsync(CancellationToken ct)
    {
        var pending = _orchestrator.Notifications.GetPending();
        foreach (var notification in pending)
        {
            if (ct.IsCancellationRequested)
                break;
            await DeliverOneAsync(notification, ct);
        }
    }

    private async Task DeliverOneAsync(FoundryNotification notification, CancellationToken ct)
    {
        var payload = DiscordWebhookFormatter.BuildPayload(notification);
        HttpResponseMessage response;
        try
        {
            response = await _http
                .PostAsJsonAsync(_orchestrator.DiscordWebhookUrl!, payload, WebhookJsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Discord POST failed (network); notification {Id} will retry.", notification.Id);
            BumpAttemptsOrFail(notification.Id);
            return;
        }

        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            _orchestrator.Notifications.MarkDelivered(notification.Id, "discord");
            return;
        }

        if (status is >= 400 and < 500)
        {
            _logger.LogError(
                "Discord POST returned {Status} for notification {Id}; marking failed.",
                status, notification.Id);
            BumpAttemptsOrFail(notification.Id);
        }
        else
        {
            _logger.LogWarning(
                "Discord POST returned {Status} for notification {Id}; will retry.",
                status, notification.Id);
            BumpAttemptsOrFail(notification.Id);
        }
    }

    private void BumpAttemptsOrFail(string id)
    {
        var updated = _orchestrator.Notifications.IncrementDeliveryAttempts(id);
        if ((updated?.DeliveryAttempts ?? MaxDeliveryAttempts) >= MaxDeliveryAttempts)
        {
            _orchestrator.Notifications.MarkDelivered(id, "discord:failed");
        }
    }
}
