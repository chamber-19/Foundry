using Foundry.Core.Agents;
using Foundry.Models;
using Foundry.Services;
using System.Text.Json;
using Xunit;

namespace Foundry.Core.Tests;

public sealed class DependencyMonitorTests
{
    [Fact]
    public void DepReviewer_Blocks_Toolkit_Updates()
    {
        var payload = DepReviewerAgent.NormalizePayload(new DependencyReviewPayload
        {
            Kind = "pull-request",
            Repository = "chamber-19/launcher",
            Title = "Bump @chamber-19/desktop-toolkit from 0.1.0 to 0.2.0",
        });

        var outcome = DepReviewerAgent.BuildRuleBasedOutcome(payload);

        Assert.Equal(DependencyNotificationCategory.Blocked, outcome.Category);
        Assert.Equal("@chamber-19/desktop-toolkit", outcome.PackageName);
    }

    [Fact]
    public void DepReviewer_Classifies_Major_Update_As_Risky()
    {
        var payload = DepReviewerAgent.NormalizePayload(new DependencyReviewPayload
        {
            Kind = "pull-request",
            Repository = "chamber-19/Foundry",
            Title = "Bump OllamaSharp from 5.4.25 to 6.0.0",
        });

        var outcome = DepReviewerAgent.BuildRuleBasedOutcome(payload);

        Assert.Equal("major", payload.UpdateType);
        Assert.Equal(DependencyNotificationCategory.Risky, outcome.Category);
    }

    [Fact]
    public void DepReviewer_Maps_High_Alert_To_Risky()
    {
        var outcome = DepReviewerAgent.BuildRuleBasedOutcome(new DependencyReviewPayload
        {
            Kind = "alert",
            Repository = "chamber-19/Foundry",
            PackageName = "Example.Package",
            Ecosystem = "nuget",
            Severity = "high",
        });

        Assert.Equal(DependencyNotificationCategory.Risky, outcome.Category);
    }

    [Fact]
    public void DepReviewer_StripList_Minor_Is_Blocked()
    {
        var stripList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.ML.OnnxRuntime" };
        var outcome = DepReviewerAgent.BuildRuleBasedOutcome(new DependencyReviewPayload
        {
            Kind = "pull-request",
            Repository = "chamber-19/Foundry",
            PackageName = "Microsoft.ML.OnnxRuntime",
            Ecosystem = "nuget",
            UpdateType = "minor",
        }, stripList);

        Assert.Equal(DependencyNotificationCategory.Blocked, outcome.Category);
    }

    [Fact]
    public void DepReviewer_StripList_Major_Is_Blocked()
    {
        var stripList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.ML.OnnxRuntime" };
        var outcome = DepReviewerAgent.BuildRuleBasedOutcome(new DependencyReviewPayload
        {
            Kind = "pull-request",
            Repository = "chamber-19/Foundry",
            PackageName = "Microsoft.ML.OnnxRuntime",
            Ecosystem = "nuget",
            UpdateType = "major",
        }, stripList);

        Assert.Equal(DependencyNotificationCategory.Blocked, outcome.Category);
    }

    [Fact]
    public void DepReviewer_NonStripList_Package_Is_Not_Blocked()
    {
        var stripList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.ML.OnnxRuntime" };
        var outcome = DepReviewerAgent.BuildRuleBasedOutcome(new DependencyReviewPayload
        {
            Kind = "pull-request",
            Repository = "chamber-19/Foundry",
            PackageName = "Microsoft.AspNetCore.Mvc.Testing",
            Ecosystem = "nuget",
            UpdateType = "minor",
        }, stripList);

        Assert.NotEqual(DependencyNotificationCategory.Blocked, outcome.Category);
    }

    [Fact]
    public void DepReviewer_FirstParty_Actions_Major_Is_Info()
    {
        var outcome = DepReviewerAgent.BuildRuleBasedOutcome(new DependencyReviewPayload
        {
            Kind = "pull-request",
            Repository = "chamber-19/Foundry",
            PackageName = "actions/checkout",
            Ecosystem = "github_actions",
            UpdateType = "major",
        });

        Assert.Equal(DependencyNotificationCategory.Info, outcome.Category);
    }

    [Fact]
    public void DepReviewer_ThirdParty_Actions_Major_Is_NeedsReview()
    {
        var outcome = DepReviewerAgent.BuildRuleBasedOutcome(new DependencyReviewPayload
        {
            Kind = "pull-request",
            Repository = "chamber-19/desktop-toolkit",
            PackageName = "softprops/action-gh-release",
            Ecosystem = "github_actions",
            UpdateType = "major",
        });

        Assert.Equal(DependencyNotificationCategory.NeedsReview, outcome.Category);
    }

    [Fact]
    public void DepReviewer_Pip_Major_Is_Risky()
    {
        var outcome = DepReviewerAgent.BuildRuleBasedOutcome(new DependencyReviewPayload
        {
            Kind = "pull-request",
            Repository = "chamber-19/transmittal-builder",
            PackageName = "some-library",
            Ecosystem = "pip",
            UpdateType = "major",
        });

        Assert.Equal(DependencyNotificationCategory.Risky, outcome.Category);
    }

    [Fact]
    public void NotificationStore_Dedupes_And_Redelivers_Updates()
    {
        using var scope = new TempDatabaseScope();
        var store = new NotificationStore(scope.Database);
        var first = BuildNotification("title", DateTimeOffset.UtcNow);

        var created = store.Upsert(first);
        var duplicate = store.Upsert(BuildNotification("title", first.EventUpdatedAt));
        store.MarkDelivered(created.Notification.Id, "discord");
        var updated = store.Upsert(BuildNotification("updated title", first.EventUpdatedAt.AddMinutes(1)));

        Assert.True(created.Created);
        Assert.False(duplicate.Created);
        Assert.False(duplicate.Updated);
        Assert.True(updated.Updated);
        Assert.Null(updated.Notification.DeliveredAt);
        Assert.Single(store.List(pendingOnly: true));
    }

    [Fact]
    public async Task DependencyMonitor_Polls_Dependabot_Prs_And_Dedupes()
    {
        using var scope = new TempDatabaseScope();
        var store = new NotificationStore(scope.Database);
        var client = new FakeGitHubDependencyClient(
            [new DependencyPullRequest
            {
                Repository = "chamber-19/Foundry",
                Number = 42,
                Title = "Bump OllamaSharp from 5.4.25 to 5.4.26",
                HtmlUrl = "https://example.test/pr/42",
                UpdatedAt = DateTimeOffset.UtcNow,
                Author = "dependabot[bot]",
                State = "open",
            }],
            []);
        var dispatcher = new AgentDispatcher([new DepReviewerAgent()]);
        var service = new DependencyMonitorService(
            new FoundrySettings { GitHubRepos = ["chamber-19/Foundry"] },
            client,
            store,
            dispatcher);

        var first = await service.PollAsync();
        var second = await service.PollAsync();

        Assert.Equal(1, first.PullRequestsSeen);
        Assert.Equal(1, first.NotificationsCreated);
        Assert.Equal(0, second.NotificationsCreated);
        Assert.Equal(0, second.NotificationsUpdated);
        Assert.Single(store.List(pendingOnly: true));
    }

    [Fact]
    public async Task DepReviewer_Falls_Back_When_Ollama_Is_Unavailable()
    {
        var payload = new DependencyReviewPayload
        {
            Kind = "pull-request",
            Repository = "chamber-19/Foundry",
            Title = "Bump OllamaSharp from 5.4.25 to 6.0.0",
        };
        var agent = new DepReviewerAgent(new ThrowingModelProvider(), "local-model");
        var handoff = new AgentHandoff
        {
            Source = "github",
            EventType = "dependabot.pull_request",
            Payload = JsonSerializer.SerializeToElement(payload),
        };

        var result = await agent.ExecuteAsync(handoff, CancellationToken.None);
        var outcome = result.Data!.Value.Deserialize<DependencyReviewOutcome>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.True(result.Success);
        Assert.NotNull(outcome);
        Assert.False(outcome.OllamaUsed);
        Assert.Equal(DependencyNotificationCategory.Risky, outcome.Category);
    }

    [Fact]
    public async Task DepReviewer_Retries_On_Schema_Failure_And_Uses_Second_Result()
    {
        const string expectedSummary = "Patch update for OllamaSharp looks safe; CI green.";
        var fakeProvider = new NullThenSuccessModelProvider(expectedSummary);
        var agent = new DepReviewerAgent(fakeProvider, "local-model");
        var payload = new DependencyReviewPayload
        {
            Kind = "pull-request",
            Repository = "chamber-19/Foundry",
            PackageName = "OllamaSharp",
            Ecosystem = "nuget",
            UpdateType = "patch",
        };
        var handoff = new AgentHandoff
        {
            Source = "github",
            EventType = "dependabot.pull_request",
            Payload = JsonSerializer.SerializeToElement(payload),
        };

        var result = await agent.ExecuteAsync(handoff, CancellationToken.None);
        var outcome = result.Data!.Value.Deserialize<DependencyReviewOutcome>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.True(result.Success);
        Assert.NotNull(outcome);
        Assert.True(outcome.OllamaUsed);
        Assert.Equal(expectedSummary, outcome.Summary);
        Assert.Equal(2, fakeProvider.CallCount);
    }

    [Fact]
    public async Task DependencyMonitor_Rejects_Invalid_Agent_Category()
    {
        using var scope = new TempDatabaseScope();
        var store = new NotificationStore(scope.Database);
        var client = new FakeGitHubDependencyClient(
            [new DependencyPullRequest
            {
                Repository = "chamber-19/Foundry",
                Number = 77,
                Title = "Bump OllamaSharp from 5.4.25 to 6.0.0",
                HtmlUrl = "https://example.test/pr/77",
                UpdatedAt = DateTimeOffset.UtcNow,
                Author = "dependabot[bot]",
                State = "open",
            }],
            []);
        var service = new DependencyMonitorService(
            new FoundrySettings { GitHubRepos = ["chamber-19/Foundry"] },
            client,
            store,
            new AgentDispatcher([new InvalidCategoryAgent()]));

        await service.PollAsync();

        var notification = Assert.Single(store.List(pendingOnly: true));
        Assert.Equal(DependencyNotificationCategory.Risky, notification.Category);
    }

    private static FoundryNotification BuildNotification(string title, DateTimeOffset eventUpdatedAt) =>
        new()
        {
            DedupeKey = "github:dependabot-pr:chamber-19/Foundry:42",
            Category = DependencyNotificationCategory.Info,
            Severity = "patch",
            Title = title,
            Body = "body",
            Source = "dependabot.pull_request",
            SourceUrl = "https://example.test/pr/42",
            Repository = "chamber-19/Foundry",
            EventUpdatedAt = eventUpdatedAt,
        };

    private sealed class FakeGitHubDependencyClient : IGitHubDependencyClient
    {
        private readonly IReadOnlyList<DependencyPullRequest> _pullRequests;
        private readonly IReadOnlyList<DependencyAlert> _alerts;

        public FakeGitHubDependencyClient(
            IReadOnlyList<DependencyPullRequest> pullRequests,
            IReadOnlyList<DependencyAlert> alerts)
        {
            _pullRequests = pullRequests;
            _alerts = alerts;
        }

        public Task<IReadOnlyList<DependencyPullRequest>> ListDependabotPullRequestsAsync(
            string repository,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_pullRequests);

        public Task<IReadOnlyList<DependencyAlert>> ListDependabotAlertsAsync(
            string repository,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_alerts);
    }

    private sealed class InvalidCategoryAgent : IAgent
    {
        public string Name => "invalid-category";
        public string Version => "1.0.0";
        public bool CanHandle(AgentHandoff handoff) => true;

        public Task<AgentResult> ExecuteAsync(AgentHandoff handoff, CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new AgentResult
            {
                Success = true,
                Message = "invalid category",
                Data = JsonSerializer.SerializeToElement(new DependencyReviewOutcome
                {
                    Category = "not-valid",
                    Reason = "bad test data",
                }),
                StartedAt = now,
                CompletedAt = now,
            });
        }
    }

    private sealed class ThrowingModelProvider : IModelProvider
    {
        public string ProviderId => "test";
        public string ProviderLabel => "Test";

        public Task<IReadOnlyList<string>> GetInstalledModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string> GenerateAsync(
            string model,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("model unavailable");

        public Task<T?> GenerateJsonAsync<T>(
            string model,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("model unavailable");

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class NullThenSuccessModelProvider : IModelProvider
    {
        private readonly string _summary;
        private int _callCount;

        public NullThenSuccessModelProvider(string summary) => _summary = summary;

        public int CallCount => _callCount;

        public string ProviderId => "test";
        public string ProviderLabel => "Test";

        public Task<IReadOnlyList<string>> GetInstalledModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string> GenerateAsync(
            string model,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<T?> GenerateJsonAsync<T>(
            string model,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
                return Task.FromResult<T?>(default);

            var json = $"{{\"summary\":\"{_summary}\"}}";
            var result = JsonSerializer.Deserialize<T>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return Task.FromResult(result);
        }

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class TempDatabaseScope : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "foundry-tests", Guid.NewGuid().ToString("N"));

        public TempDatabaseScope()
        {
            Database = new FoundryDatabase(_path);
        }

        public FoundryDatabase Database { get; }

        public void Dispose()
        {
            Database.Dispose();
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
