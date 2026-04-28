using System.Text.Json;

namespace Foundry.Core.Agents;

/// <summary>
/// Carries the context and payload required to invoke a Foundry agent.
/// Handoffs are produced by event sources (GitHub webhooks, Discord slash
/// commands, scheduled triggers) and consumed by <see cref="IAgent"/>
/// implementations via <see cref="AgentDispatcher"/>.
/// </summary>
/// <remarks>
/// <c>AgentHandoff</c> is immutable by design — agents must not mutate the
/// handoff. All mutable agent state lives in injected services or in the
/// returned <see cref="AgentResult"/>.
/// </remarks>
public sealed class AgentHandoff
{
    /// <summary>
    /// Identifies the system that originated this handoff.
    /// Examples: <c>"github"</c>, <c>"discord"</c>, <c>"scheduler"</c>.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// The logical event type within the source.
    /// Examples: <c>"pull_request.opened"</c>, <c>"slash_command.review"</c>,
    /// <c>"cron.daily"</c>.
    /// </summary>
    public string EventType { get; init; } = string.Empty;

    /// <summary>
    /// Raw JSON payload from the originating event.
    /// Agents are responsible for deserializing the fields they need.
    /// </summary>
    public JsonElement Payload { get; init; }

    /// <summary>
    /// Opaque identifier used for end-to-end tracing across logs and
    /// telemetry sinks. Defaults to a new random value when not supplied.
    /// </summary>
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// When this handoff was created (UTC). Used for queue-depth telemetry.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
