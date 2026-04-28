namespace Foundry.Core.Agents;

/// <summary>
/// Contract for a Foundry agent. An agent receives an <see cref="AgentHandoff"/>,
/// performs deterministic and/or LLM-assisted work, and returns an
/// <see cref="AgentResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// Agents MUST be stateless across invocations — all state lives in the
/// handoff or in injected services. This constraint allows the broker to
/// invoke the same agent instance concurrently for different handoffs.
/// </para>
/// <para>
/// Design contract for concrete implementations:
/// <list type="number">
///   <item>Run deterministic pre-checks first (no LLM).</item>
///   <item>Use the LLM for <em>structured extraction</em> against a JSON
///     schema; never for open-ended judgment.</item>
///   <item>Apply a rule engine to LLM output to produce a verdict.</item>
///   <item>If Ollama is unreachable, fail open to
///     <c>AgentResult.Fail("needs human review")</c>.</item>
/// </list>
/// </para>
/// </remarks>
public interface IAgent
{
    /// <summary>
    /// Stable lowercase identifier used for routing and telemetry.
    /// Must be unique across all registered agents.
    /// Examples: <c>"dep-reviewer"</c>, <c>"toolkit-bumper"</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Semantic version of this agent's behavior contract (e.g. <c>"1.0.0"</c>).
    /// Increment the major version when the output schema changes in a
    /// breaking way so callers can detect incompatible updates.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Returns <c>true</c> when this agent is able to handle the given
    /// <paramref name="handoff"/>. Used by <see cref="AgentDispatcher"/> to
    /// route handoffs to the first matching agent.
    /// </summary>
    /// <remarks>
    /// MUST be cheap and side-effect-free — it is called on every registered
    /// agent for every incoming handoff.
    /// </remarks>
    /// <param name="handoff">The handoff to evaluate.</param>
    bool CanHandle(AgentHandoff handoff);

    /// <summary>
    /// Execute the agent against the given <paramref name="handoff"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>MUST honor <paramref name="ct"/>.</item>
    ///   <item>MUST NOT throw for expected failure modes — return an
    ///     <see cref="AgentResult"/> with <c>Success = false</c>
    ///     instead.</item>
    ///   <item>Only called after <see cref="CanHandle"/> returns
    ///     <c>true</c>.</item>
    /// </list>
    /// </remarks>
    /// <param name="handoff">The handoff to process.</param>
    /// <param name="ct">Cancellation token; respect promptly.</param>
    /// <returns>
    /// An <see cref="AgentResult"/> describing the outcome. Never
    /// <c>null</c>.
    /// </returns>
    Task<AgentResult> ExecuteAsync(AgentHandoff handoff, CancellationToken ct);
}
