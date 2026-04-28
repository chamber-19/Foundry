using Foundry.Core.Agents;
using Xunit;

namespace Foundry.Core.Tests;

/// <summary>
/// Unit tests that pin the agent dispatch contract. These tests use
/// <see cref="AgentDispatcher"/> directly, which encapsulates the same
/// routing logic exposed by <see cref="Foundry.Services.FoundryOrchestrator.DispatchAsync"/>.
/// </summary>
public sealed class OrchestratorDispatchTests
{
    // -------------------------------------------------------------------------
    // Fake agent helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// A controllable <see cref="IAgent"/> that records whether it was invoked.
    /// </summary>
    private sealed class FakeAgent : IAgent
    {
        private readonly bool _canHandle;
        private readonly AgentResult _result;

        public bool WasInvoked { get; private set; }

        public string Name { get; }
        public string Version => "1.0.0";

        public FakeAgent(string name, bool canHandle, AgentResult? result = null)
        {
            Name = name;
            _canHandle = canHandle;
            _result = result ?? AgentResult.Ok($"Handled by {name}");
        }

        public bool CanHandle(AgentHandoff handoff) => _canHandle;

        public Task<AgentResult> ExecuteAsync(AgentHandoff handoff, CancellationToken ct)
        {
            WasInvoked = true;
            return Task.FromResult(_result);
        }
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Orchestrator_Dispatches_To_Matching_Agent()
    {
        // Arrange — two agents registered; only the second can handle the handoff
        var handoff = new AgentHandoff { Source = "test", EventType = "test.event" };
        var nonMatchingAgent = new FakeAgent("non-matching", canHandle: false);
        var matchingAgent = new FakeAgent("matching", canHandle: true);

        var dispatcher = new AgentDispatcher([nonMatchingAgent, matchingAgent]);

        // Act
        var result = await dispatcher.DispatchAsync(handoff);

        // Assert — the matching agent ran, the non-matching one did not
        Assert.True(result.Success);
        Assert.True(matchingAgent.WasInvoked);
        Assert.False(nonMatchingAgent.WasInvoked);
        Assert.Contains("matching", result.Message);
    }

    [Fact]
    public async Task Orchestrator_Returns_Failure_When_No_Agent_Handles()
    {
        // Arrange — two agents, neither claims the handoff
        var handoff = new AgentHandoff { Source = "test", EventType = "unknown.event" };
        var agent1 = new FakeAgent("agent-a", canHandle: false);
        var agent2 = new FakeAgent("agent-b", canHandle: false);

        var dispatcher = new AgentDispatcher([agent1, agent2]);

        // Act
        var result = await dispatcher.DispatchAsync(handoff);

        // Assert — failure result returned (not an exception), no agent was invoked
        Assert.False(result.Success);
        Assert.False(agent1.WasInvoked);
        Assert.False(agent2.WasInvoked);
        Assert.NotNull(result.Message);
        Assert.Contains("No registered agent", result.Message);
    }

    [Fact]
    public async Task Orchestrator_Dispatches_To_First_Matching_Agent_In_Registration_Order()
    {
        // Arrange — both agents claim the handoff; first registered wins
        var handoff = new AgentHandoff { Source = "test", EventType = "overlap.event" };
        var firstAgent = new FakeAgent("first", canHandle: true);
        var secondAgent = new FakeAgent("second", canHandle: true);

        var dispatcher = new AgentDispatcher([firstAgent, secondAgent]);

        // Act
        var result = await dispatcher.DispatchAsync(handoff);

        // Assert — first-registered agent wins; second is never called
        Assert.True(result.Success);
        Assert.True(firstAgent.WasInvoked);
        Assert.False(secondAgent.WasInvoked);
    }

    [Fact]
    public async Task Orchestrator_Returns_Failure_When_No_Agents_Registered()
    {
        // Arrange — empty dispatcher (no agents at all)
        var handoff = new AgentHandoff { Source = "github", EventType = "pull_request.opened" };
        var dispatcher = new AgentDispatcher([]);

        // Act
        var result = await dispatcher.DispatchAsync(handoff);

        // Assert — graceful failure, not an exception
        Assert.False(result.Success);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task Orchestrator_Result_Contains_Timing_Info()
    {
        // Arrange
        var handoff = new AgentHandoff { Source = "scheduler", EventType = "cron.daily" };
        var agent = new FakeAgent("timed-agent", canHandle: true);
        var dispatcher = new AgentDispatcher([agent]);

        // Act
        var result = await dispatcher.DispatchAsync(handoff);

        // Assert — timing fields are populated (contract for future telemetry)
        Assert.True(result.StartedAt > DateTimeOffset.MinValue);
        Assert.True(result.CompletedAt >= result.StartedAt);
    }
}
