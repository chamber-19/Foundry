using System.Text.Json;
using System.Text.RegularExpressions;
using Foundry.Models;
using Foundry.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Core.Agents;

public sealed partial class DepReviewerAgent : IAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IModelProvider? _modelProvider;
    private readonly string _ollamaModel;
    private readonly ILogger<DepReviewerAgent> _logger;

    public DepReviewerAgent(
        IModelProvider? modelProvider = null,
        string? ollamaModel = null,
        ILogger<DepReviewerAgent>? logger = null)
    {
        _modelProvider = modelProvider;
        _ollamaModel = ollamaModel ?? string.Empty;
        _logger = logger ?? NullLogger<DepReviewerAgent>.Instance;
    }

    public string Name => "dep-reviewer";
    public string Version => "1.0.0";

    public bool CanHandle(AgentHandoff handoff) =>
        string.Equals(handoff.Source, "github", StringComparison.OrdinalIgnoreCase) &&
        handoff.EventType is "dependabot.pull_request" or "dependabot.alert";

    public async Task<AgentResult> ExecuteAsync(AgentHandoff handoff, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var payload = DeserializePayload(handoff.Payload);
        if (payload is null)
        {
            return AgentResult.Fail("Dependency review payload was invalid.");
        }

        payload = NormalizePayload(payload);
        var deterministic = BuildRuleBasedOutcome(payload);
        var summary = deterministic.Summary;
        var ollamaUsed = false;

        if (_modelProvider is not null && !string.IsNullOrWhiteSpace(_ollamaModel))
        {
            try
            {
                var ollamaSummary = await _modelProvider.GenerateJsonAsync<OllamaDependencySummary>(
                    _ollamaModel,
                    "Summarize dependency changes for an operator. Return only JSON with a concise summary field. Do not decide risk.",
                    JsonSerializer.Serialize(payload, JsonOptions),
                    ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(ollamaSummary?.Summary))
                {
                    summary = ollamaSummary.Summary.Trim();
                    ollamaUsed = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Ollama dependency summary failed; using deterministic summary.");
            }
        }

        var outcome = new DependencyReviewOutcome
        {
            Category = deterministic.Category,
            Reason = deterministic.Reason,
            Summary = summary,
            PackageName = payload.PackageName,
            Ecosystem = payload.Ecosystem,
            UpdateType = payload.UpdateType,
            OllamaUsed = ollamaUsed,
        };

        return new AgentResult
        {
            Success = true,
            Message = $"{outcome.Category}: {outcome.Reason}",
            Data = JsonSerializer.SerializeToElement(outcome, JsonOptions),
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    public static DependencyReviewPayload NormalizePayload(DependencyReviewPayload payload)
    {
        var packageName = payload.PackageName.Trim();
        var currentVersion = payload.CurrentVersion.Trim();
        var targetVersion = payload.TargetVersion.Trim();

        if (string.Equals(payload.Kind, "pull-request", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(packageName) ||
             string.IsNullOrWhiteSpace(currentVersion) ||
             string.IsNullOrWhiteSpace(targetVersion)))
        {
            var extracted = ExtractDependabotBump(payload.Title);
            packageName = string.IsNullOrWhiteSpace(packageName) ? extracted.PackageName : packageName;
            currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? extracted.CurrentVersion : currentVersion;
            targetVersion = string.IsNullOrWhiteSpace(targetVersion) ? extracted.TargetVersion : targetVersion;
        }

        var updateType = payload.UpdateType.Trim();
        if (string.IsNullOrWhiteSpace(updateType) || updateType == "unknown")
        {
            updateType = ClassifySemVerUpdate(currentVersion, targetVersion);
        }

        return new DependencyReviewPayload
        {
            Kind = payload.Kind,
            Repository = payload.Repository,
            PackageName = packageName,
            Ecosystem = payload.Ecosystem.Trim(),
            CurrentVersion = currentVersion,
            TargetVersion = targetVersion,
            UpdateType = updateType,
            Severity = payload.Severity.Trim(),
            Title = payload.Title,
            Body = payload.Body,
            Url = payload.Url,
        };
    }

    public static DependencyReviewOutcome BuildRuleBasedOutcome(DependencyReviewPayload payload)
    {
        var packageName = payload.PackageName.Trim();
        if (IsToolkitPackage(packageName))
        {
            return new DependencyReviewOutcome
            {
                Category = DependencyNotificationCategory.Blocked,
                Reason = "desktop-toolkit updates must be paired and reviewed deliberately.",
                Summary = BuildSummary(payload),
                PackageName = packageName,
                Ecosystem = payload.Ecosystem,
                UpdateType = payload.UpdateType,
                OllamaUsed = false,
            };
        }

        if (string.Equals(payload.Kind, "alert", StringComparison.OrdinalIgnoreCase))
        {
            return SeverityOutcome(payload);
        }

        return payload.UpdateType.ToLowerInvariant() switch
        {
            "major" => new DependencyReviewOutcome
            {
                Category = DependencyNotificationCategory.Risky,
                Reason = "major dependency update needs focused review.",
                Summary = BuildSummary(payload),
                PackageName = packageName,
                Ecosystem = payload.Ecosystem,
                UpdateType = payload.UpdateType,
            },
            "minor" => new DependencyReviewOutcome
            {
                Category = DependencyNotificationCategory.NeedsReview,
                Reason = "minor dependency update should be reviewed before merge.",
                Summary = BuildSummary(payload),
                PackageName = packageName,
                Ecosystem = payload.Ecosystem,
                UpdateType = payload.UpdateType,
            },
            "patch" => new DependencyReviewOutcome
            {
                Category = DependencyNotificationCategory.Info,
                Reason = "patch dependency update.",
                Summary = BuildSummary(payload),
                PackageName = packageName,
                Ecosystem = payload.Ecosystem,
                UpdateType = payload.UpdateType,
            },
            _ => new DependencyReviewOutcome
            {
                Category = DependencyNotificationCategory.NeedsReview,
                Reason = "dependency update type was not confidently classified.",
                Summary = BuildSummary(payload),
                PackageName = packageName,
                Ecosystem = payload.Ecosystem,
                UpdateType = payload.UpdateType,
            },
        };
    }

    public static string ClassifySemVerUpdate(string currentVersion, string targetVersion)
    {
        if (!TryParseSemVer(currentVersion, out var current) ||
            !TryParseSemVer(targetVersion, out var target))
        {
            return "unknown";
        }

        if (target.Major > current.Major)
        {
            return "major";
        }

        if (target.Major == current.Major && target.Minor > current.Minor)
        {
            return "minor";
        }

        if (target.Major == current.Major &&
            target.Minor == current.Minor &&
            target.Patch > current.Patch)
        {
            return "patch";
        }

        return "unknown";
    }

    private static DependencyReviewPayload? DeserializePayload(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<DependencyReviewPayload>(JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static DependencyReviewOutcome SeverityOutcome(DependencyReviewPayload payload)
    {
        var severity = payload.Severity.ToLowerInvariant();
        var category = severity switch
        {
            "critical" or "high" => DependencyNotificationCategory.Risky,
            "moderate" or "medium" => DependencyNotificationCategory.NeedsReview,
            "low" => DependencyNotificationCategory.Info,
            _ => DependencyNotificationCategory.NeedsReview,
        };

        var reason = severity switch
        {
            "critical" or "high" => "security alert severity is high enough to require prompt review.",
            "moderate" or "medium" => "security alert needs review.",
            "low" => "low severity security alert.",
            _ => "security alert severity was unknown.",
        };

        return new DependencyReviewOutcome
        {
            Category = category,
            Reason = reason,
            Summary = BuildSummary(payload),
            PackageName = payload.PackageName,
            Ecosystem = payload.Ecosystem,
            UpdateType = payload.UpdateType,
        };
    }

    private static bool IsToolkitPackage(string packageName) =>
        string.Equals(packageName, "@chamber-19/desktop-toolkit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(packageName, "desktop-toolkit", StringComparison.OrdinalIgnoreCase);

    private static string BuildSummary(DependencyReviewPayload payload)
    {
        var packageName = string.IsNullOrWhiteSpace(payload.PackageName)
            ? "dependency"
            : payload.PackageName;

        if (string.Equals(payload.Kind, "alert", StringComparison.OrdinalIgnoreCase))
        {
            return $"{payload.Repository}: {payload.Severity} alert for {packageName}.";
        }

        var fromTo = !string.IsNullOrWhiteSpace(payload.CurrentVersion) &&
                     !string.IsNullOrWhiteSpace(payload.TargetVersion)
            ? $" from {payload.CurrentVersion} to {payload.TargetVersion}"
            : string.Empty;

        return $"{payload.Repository}: {payload.UpdateType} update for {packageName}{fromTo}.";
    }

    private static ExtractedBump ExtractDependabotBump(string title)
    {
        var match = DependabotBumpRegex().Match(title);
        if (!match.Success)
        {
            return new ExtractedBump();
        }

        return new ExtractedBump
        {
            PackageName = match.Groups["package"].Value.Trim(),
            CurrentVersion = match.Groups["from"].Value.Trim().TrimEnd('.'),
            TargetVersion = match.Groups["to"].Value.Trim().TrimEnd('.'),
        };
    }

    private static bool TryParseSemVer(string version, out SemVer semVer)
    {
        semVer = default;
        var match = SemVerRegex().Match(version.Trim());
        if (!match.Success)
        {
            return false;
        }

        semVer = new SemVer(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value));
        return true;
    }

    [GeneratedRegex(@"^Bump\s+(?<package>.+?)\s+from\s+(?<from>\S+)\s+to\s+(?<to>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DependabotBumpRegex();

    [GeneratedRegex(@"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SemVerRegex();

    private readonly record struct SemVer(int Major, int Minor, int Patch);

    private sealed class ExtractedBump
    {
        public string PackageName { get; init; } = string.Empty;
        public string CurrentVersion { get; init; } = string.Empty;
        public string TargetVersion { get; init; } = string.Empty;
    }

    private sealed class OllamaDependencySummary
    {
        public string Summary { get; init; } = string.Empty;
    }
}
