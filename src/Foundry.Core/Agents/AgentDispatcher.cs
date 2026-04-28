using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Core.Agents;

/// <summary>
/// Routes an <see cref="AgentHandoff"/> to the first registered
/// <see cref="IAgent"/> whose <see cref="IAgent.CanHandle"/> returns
/// <c>true</c>.
/// </summary>
/// <remarks>
/// Agents are evaluated in registration order. The first match wins; no
/// subsequent agents are invoked for the same handoff. If no agent matches,
/// a failure <see cref="AgentResult"/> is returned — an exception is never
/// thrown for the no-match case.
/// </remarks>
public sealed class AgentDispatcher
{
    private readonly IReadOnlyList<IAgent> _agents;
    private readonly ILogger<AgentDispatcher> _logger;

    /// <summary>
    /// Initializes a new <see cref="AgentDispatcher"/> with the supplied
    /// agents evaluated in enumeration order.
    /// </summary>
    /// <param name="agents">
    /// The agents to consider for dispatch. Pass an empty collection to
    /// create a dispatcher that always returns a no-match failure.
    /// </param>
    /// <param name="logger">Optional logger. Defaults to a no-op logger.</param>
    public AgentDispatcher(IEnumerable<IAgent> agents, ILogger<AgentDispatcher>? logger = null)
    {
        _agents = agents.ToList().AsReadOnly();
        _logger = logger ?? NullLogger<AgentDispatcher>.Instance;
    }

    /// <summary>
    /// Gets the agents registered with this dispatcher (in evaluation order).
    /// </summary>
    public IReadOnlyList<IAgent> Agents => _agents;

    /// <summary>
    /// Dispatches <paramref name="handoff"/> to the first agent that can
    /// handle it.
    /// </summary>
    /// <param name="handoff">The handoff to route.</param>
    /// <param name="ct">Cancellation token propagated to the matched agent.</param>
    /// <returns>
    /// The <see cref="AgentResult"/> from the matched agent, or a failure
    /// result when no agent matches.
    /// </returns>
    public async Task<AgentResult> DispatchAsync(AgentHandoff handoff, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        foreach (var agent in _agents)
        {
            if (!agent.CanHandle(handoff))
                continue;

            _logger.LogInformation(
                "Dispatching handoff {CorrelationId} (source={Source} event={EventType}) to agent {Agent} v{Version}",
                handoff.CorrelationId, handoff.Source, handoff.EventType, agent.Name, agent.Version);

            try
            {
                return await agent.ExecuteAsync(handoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Agent {Agent} cancelled for handoff {CorrelationId}",
                    agent.Name, handoff.CorrelationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Agent {Agent} threw an unexpected exception for handoff {CorrelationId}",
                    agent.Name, handoff.CorrelationId);
                return new AgentResult
                {
                    Success = false,
                    Message = $"Agent '{agent.Name}' faulted: {ex.Message}",
                    StartedAt = startedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                };
            }
        }

        _logger.LogWarning(
            "No agent matched handoff {CorrelationId} (source={Source} event={EventType}). Registered agents: {Count}",
            handoff.CorrelationId, handoff.Source, handoff.EventType, _agents.Count);

        return new AgentResult
        {
            Success = false,
            Message = $"No registered agent can handle source='{handoff.Source}' event='{handoff.EventType}'. Needs human review.",
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }
}
