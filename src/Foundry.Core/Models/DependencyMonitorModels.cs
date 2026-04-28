namespace Foundry.Models;

public static class DependencyNotificationCategory
{
    public const string Info = "info";
    public const string NeedsReview = "needs-review";
    public const string Risky = "risky";
    public const string Blocked = "blocked";

    public static bool IsValid(string? value) =>
        value is Info or NeedsReview or Risky or Blocked;
}

public sealed class FoundryNotification
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DedupeKey { get; set; } = string.Empty;
    public string Category { get; set; } = DependencyNotificationCategory.Info;
    public string Severity { get; set; } = "info";
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public DateTimeOffset EventUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? DeliveredTo { get; set; }
}

public sealed class DependencyMonitorResult
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
    public int RepositoriesChecked { get; init; }
    public int PullRequestsSeen { get; init; }
    public int AlertsSeen { get; init; }
    public int NotificationsCreated { get; init; }
    public int NotificationsUpdated { get; init; }
    public IReadOnlyList<FoundryNotification> Notifications { get; init; } = Array.Empty<FoundryNotification>();
}

public sealed class DependencyPullRequest
{
    public string Repository { get; init; } = string.Empty;
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class DependencyAlert
{
    public string Repository { get; init; } = string.Empty;
    public string AlertId { get; init; } = string.Empty;
    public string PackageName { get; init; } = string.Empty;
    public string Ecosystem { get; init; } = string.Empty;
    public string Severity { get; init; } = "unknown";
    public string State { get; init; } = string.Empty;
    public string AdvisorySummary { get; init; } = string.Empty;
    public string VulnerableRequirements { get; init; } = string.Empty;
    public string PatchedVersion { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class DependencyReviewPayload
{
    public string Kind { get; init; } = string.Empty;
    public string Repository { get; init; } = string.Empty;
    public string PackageName { get; init; } = string.Empty;
    public string Ecosystem { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string TargetVersion { get; init; } = string.Empty;
    public string UpdateType { get; init; } = "unknown";
    public string Severity { get; init; } = "unknown";
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public sealed class DependencyReviewOutcome
{
    public string Category { get; init; } = DependencyNotificationCategory.NeedsReview;
    public string Reason { get; init; } = "Needs human review.";
    public string Summary { get; init; } = string.Empty;
    public string PackageName { get; init; } = string.Empty;
    public string Ecosystem { get; init; } = string.Empty;
    public string UpdateType { get; init; } = "unknown";
    public bool OllamaUsed { get; init; }
}

public sealed class NotificationUpsertResult
{
    public required FoundryNotification Notification { get; init; }
    public bool Created { get; init; }
    public bool Updated { get; init; }
}
