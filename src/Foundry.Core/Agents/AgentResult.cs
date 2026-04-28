using System.Text.Json;

namespace Foundry.Core.Agents;

/// <summary>
/// The outcome of a single agent invocation. Returned by
/// <see cref="IAgent.ExecuteAsync"/> and by <see cref="AgentDispatcher"/>.
/// </summary>
/// <remarks>
/// Agents MUST NOT throw for expected failure modes; they must return an
/// <c>AgentResult</c> with <see cref="Success"/> set to <c>false</c> and a
/// descriptive <see cref="Message"/>. Only truly unexpected exceptions (bugs,
/// infrastructure faults) should propagate as exceptions.
/// </remarks>
public sealed record AgentResult
{
    /// <summary>
    /// <c>true</c> when the agent completed its work without error;
    /// <c>false</c> on any expected failure (validation, Ollama unreachable,
    /// no matching agent, etc.).
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Human-readable summary of the outcome. Populated on both success and
    /// failure. Agents should provide enough detail for an operator to
    /// understand what happened without diving into logs.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Optional structured output from the agent.  Use
    /// <c>JsonSerializer.Deserialize&lt;T&gt;(Data.Value)</c> to extract
    /// agent-specific fields; callers must know the agent's output schema.
    /// <c>null</c> when the agent produced no structured output.
    /// </summary>
    public JsonElement? Data { get; init; }

    /// <summary>
    /// When the agent began executing (UTC). Combined with
    /// <see cref="CompletedAt"/> to derive elapsed time for telemetry.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the agent finished executing (UTC).
    /// </summary>
    public DateTimeOffset CompletedAt { get; init; }

    // -----------------------------------------------------------------------
    // Factory helpers — keeps call sites concise
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a failure result with the supplied message and no structured
    /// data. <see cref="StartedAt"/> and <see cref="CompletedAt"/> are both
    /// set to <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    /// <param name="message">Human-readable explanation of the failure.</param>
    public static AgentResult Fail(string message)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentResult { Success = false, Message = message, StartedAt = now, CompletedAt = now };
    }

    /// <summary>
    /// Creates a success result with an optional message and no structured
    /// data. <see cref="StartedAt"/> defaults to <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    /// <param name="message">Optional human-readable summary.</param>
    /// <param name="startedAt">
    /// When execution began. Pass the value captured before the agent ran so
    /// elapsed time reflects actual work, not just result construction.
    /// </param>
    public static AgentResult Ok(string? message = null, DateTimeOffset? startedAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentResult
        {
            Success = true,
            Message = message,
            StartedAt = startedAt ?? now,
            CompletedAt = now,
        };
    }
}
