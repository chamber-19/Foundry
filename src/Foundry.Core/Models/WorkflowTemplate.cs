namespace Foundry.Models;

/// <summary>
/// Represents a reusable operator workflow template.
/// A workflow is an ordered list of steps (job types + parameters)
/// that can be executed as a sequence.
/// </summary>
public sealed class WorkflowTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Ordered list of steps to execute.
    /// </summary>
    public List<WorkflowStep> Steps { get; set; } = [];

    /// <summary>
    /// Whether to abort remaining steps on failure or continue.
    /// </summary>
    public string FailurePolicy { get; set; } = WorkflowFailurePolicy.Abort;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Whether this template is a built-in (system-defined) template.
    /// </summary>
    public bool BuiltIn { get; set; }
}

/// <summary>
/// A single step in a workflow template.
/// </summary>
public sealed class WorkflowStep
{
    public string JobType { get; set; } = string.Empty;
    public string? RequestPayload { get; set; }
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Constants for workflow failure policies.
/// </summary>
public static class WorkflowFailurePolicy
{
    public const string Abort = "abort";
    public const string Continue = "continue";
}
