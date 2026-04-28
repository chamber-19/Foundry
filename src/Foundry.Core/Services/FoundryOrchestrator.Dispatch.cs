using Foundry.Core.Agents;

namespace Foundry.Services;

// Agent handoff dispatch — routes incoming AgentHandoff to registered IAgent
// implementations via AgentDispatcher.
public sealed partial class FoundryOrchestrator
{
    /// <summary>
    /// Dispatches <paramref name="handoff"/> to the first registered
    /// <see cref="IAgent"/> whose <see cref="IAgent.CanHandle"/> returns
    /// <c>true</c>.
    /// </summary>
    /// <remarks>
    /// If no agent matches, a failure <see cref="AgentResult"/> is returned —
    /// an exception is never thrown for the no-match case (fail-open pattern).
    /// Agents are evaluated in registration order; the first match wins.
    /// </remarks>
    /// <param name="handoff">The handoff to route.</param>
    /// <param name="ct">
    /// Cancellation token propagated to the matched agent.
    /// </param>
    public Task<AgentResult> DispatchAsync(AgentHandoff handoff, CancellationToken ct = default)
        => _dispatcher.DispatchAsync(handoff, ct);
}
