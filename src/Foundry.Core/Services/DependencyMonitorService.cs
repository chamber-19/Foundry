using System.Text.Json;
using Foundry.Core.Agents;
using Foundry.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Services;

public sealed class DependencyMonitorService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly FoundrySettings _settings;
    private readonly IGitHubDependencyClient _gitHubClient;
    private readonly NotificationStore _notificationStore;
    private readonly AgentDispatcher _dispatcher;
    private readonly ILogger<DependencyMonitorService> _logger;

    public DependencyMonitorService(
        FoundrySettings settings,
        IGitHubDependencyClient gitHubClient,
        NotificationStore notificationStore,
        AgentDispatcher dispatcher,
        ILogger<DependencyMonitorService>? logger = null)
    {
        _settings = settings;
        _gitHubClient = gitHubClient;
        _notificationStore = notificationStore;
        _dispatcher = dispatcher;
        _logger = logger ?? NullLogger<DependencyMonitorService>.Instance;
    }

    public TimeSpan PollingInterval =>
        TimeSpan.FromMinutes(Math.Clamp(_settings.DependencyPollingIntervalMinutes, 1, 1440));

    public IReadOnlyList<string> ConfiguredRepositories => _settings.GitHubRepos
        .Where(repo => !string.IsNullOrWhiteSpace(repo))
        .Select(repo => repo.Trim().Trim('/'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public async Task<DependencyMonitorResult> PollAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var checkedRepos = 0;
        var pullRequestsSeen = 0;
        var alertsSeen = 0;
        var created = 0;
        var updated = 0;
        var changedNotifications = new List<FoundryNotification>();

        foreach (var repository in ConfiguredRepositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedRepos++;

            var pullRequests = await _gitHubClient
                .ListDependabotPullRequestsAsync(repository, cancellationToken)
                .ConfigureAwait(false);
            pullRequestsSeen += pullRequests.Count;

            foreach (var pullRequest in pullRequests)
            {
                var outcome = await ReviewAsync(
                    BuildPullRequestPayload(pullRequest),
                    "dependabot.pull_request",
                    cancellationToken).ConfigureAwait(false);

                var upsert = _notificationStore.Upsert(BuildPullRequestNotification(pullRequest, outcome));
                if (TrackChangedNotification(upsert, changedNotifications))
                {
                    created += upsert.Created ? 1 : 0;
                    updated += upsert.Updated ? 1 : 0;
                }
            }

            var alerts = await _gitHubClient
                .ListDependabotAlertsAsync(repository, cancellationToken)
                .ConfigureAwait(false);
            alertsSeen += alerts.Count;

            foreach (var alert in alerts)
            {
                var outcome = await ReviewAsync(
                    BuildAlertPayload(alert),
                    "dependabot.alert",
                    cancellationToken).ConfigureAwait(false);

                var upsert = _notificationStore.Upsert(BuildAlertNotification(alert, outcome));
                if (TrackChangedNotification(upsert, changedNotifications))
                {
                    created += upsert.Created ? 1 : 0;
                    updated += upsert.Updated ? 1 : 0;
                }
            }
        }

        _logger.LogInformation(
            "Dependency poll checked {RepositoryCount} repos, saw {PullRequestCount} PRs and {AlertCount} alerts, produced {NotificationCount} notifications.",
            checkedRepos,
            pullRequestsSeen,
            alertsSeen,
            changedNotifications.Count);

        return new DependencyMonitorResult
        {
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            RepositoriesChecked = checkedRepos,
            PullRequestsSeen = pullRequestsSeen,
            AlertsSeen = alertsSeen,
            NotificationsCreated = created,
            NotificationsUpdated = updated,
            Notifications = changedNotifications,
        };
    }

    private async Task<DependencyReviewOutcome> ReviewAsync(
        DependencyReviewPayload payload,
        string eventType,
        CancellationToken cancellationToken)
    {
        var normalized = DepReviewerAgent.NormalizePayload(payload);
        var handoff = new AgentHandoff
        {
            Source = "github",
            EventType = eventType,
            Payload = JsonSerializer.SerializeToElement(normalized, JsonOptions),
        };

        var result = await _dispatcher.DispatchAsync(handoff, cancellationToken).ConfigureAwait(false);
        if (result.Success && result.Data.HasValue)
        {
            try
            {
                var outcome = result.Data.Value.Deserialize<DependencyReviewOutcome>(JsonOptions);
                if (outcome is not null &&
                    DependencyNotificationCategory.IsValid(outcome.Category))
                {
                    return outcome;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Dependency review result JSON was invalid.");
            }
        }

        var fallback = DepReviewerAgent.BuildRuleBasedOutcome(normalized);
        return new DependencyReviewOutcome
        {
            Category = fallback.Category,
            Reason = result.Message ?? fallback.Reason,
            Summary = fallback.Summary,
            PackageName = fallback.PackageName,
            Ecosystem = fallback.Ecosystem,
            UpdateType = fallback.UpdateType,
            OllamaUsed = false,
        };
    }

    private static bool TrackChangedNotification(
        NotificationUpsertResult upsert,
        ICollection<FoundryNotification> changedNotifications)
    {
        if (!upsert.Created && !upsert.Updated)
        {
            return false;
        }

        changedNotifications.Add(upsert.Notification);
        return true;
    }

    private static DependencyReviewPayload BuildPullRequestPayload(DependencyPullRequest pullRequest) =>
        new()
        {
            Kind = "pull-request",
            Repository = pullRequest.Repository,
            Title = pullRequest.Title,
            Body = pullRequest.Body,
            Url = pullRequest.HtmlUrl,
        };

    private static DependencyReviewPayload BuildAlertPayload(DependencyAlert alert) =>
        new()
        {
            Kind = "alert",
            Repository = alert.Repository,
            PackageName = alert.PackageName,
            Ecosystem = alert.Ecosystem,
            Severity = alert.Severity,
            Title = alert.AdvisorySummary,
            Body = $"Vulnerable range: {alert.VulnerableRequirements}. Patched version: {alert.PatchedVersion}.",
            Url = alert.HtmlUrl,
        };

    private static FoundryNotification BuildPullRequestNotification(
        DependencyPullRequest pullRequest,
        DependencyReviewOutcome outcome)
    {
        var title = $"Dependabot PR: {pullRequest.Repository} #{pullRequest.Number}";
        var body = string.Join(Environment.NewLine, new[]
        {
            pullRequest.Title,
            outcome.Summary,
            outcome.Reason,
        }.Where(line => !string.IsNullOrWhiteSpace(line)));

        return new FoundryNotification
        {
            DedupeKey = $"github:dependabot-pr:{pullRequest.Repository}:{pullRequest.Number}",
            Category = outcome.Category,
            Severity = outcome.UpdateType,
            Title = title,
            Body = body,
            Source = "dependabot.pull_request",
            SourceUrl = pullRequest.HtmlUrl,
            Repository = pullRequest.Repository,
            PackageName = outcome.PackageName,
            Ecosystem = outcome.Ecosystem,
            EventUpdatedAt = pullRequest.UpdatedAt == default ? DateTimeOffset.UtcNow : pullRequest.UpdatedAt,
        };
    }

    private static FoundryNotification BuildAlertNotification(
        DependencyAlert alert,
        DependencyReviewOutcome outcome)
    {
        var packageName = string.IsNullOrWhiteSpace(alert.PackageName)
            ? "dependency"
            : alert.PackageName;
        var title = $"Dependabot alert: {alert.Repository} {packageName}";
        var body = string.Join(Environment.NewLine, new[]
        {
            alert.AdvisorySummary,
            outcome.Summary,
            outcome.Reason,
            string.IsNullOrWhiteSpace(alert.PatchedVersion)
                ? string.Empty
                : $"Patched version: {alert.PatchedVersion}",
        }.Where(line => !string.IsNullOrWhiteSpace(line)));

        return new FoundryNotification
        {
            DedupeKey = $"github:dependabot-alert:{alert.Repository}:{alert.AlertId}",
            Category = outcome.Category,
            Severity = alert.Severity,
            Title = title,
            Body = body,
            Source = "dependabot.alert",
            SourceUrl = alert.HtmlUrl,
            Repository = alert.Repository,
            PackageName = alert.PackageName,
            Ecosystem = alert.Ecosystem,
            EventUpdatedAt = alert.UpdatedAt == default ? DateTimeOffset.UtcNow : alert.UpdatedAt,
        };
    }
}
