namespace Foundry.Models;

/// <summary>
/// Overall health report returned by GET /api/health.
/// </summary>
public sealed class FoundryHealthReport
{
    public string Overall { get; set; } = HealthStatus.Ok;
    public SubsystemHealth Ollama { get; set; } = new();
    public SubsystemHealth Python { get; set; } = new();
    public SubsystemHealth LiteDB { get; set; } = new();
    public SubsystemHealth JobWorker { get; set; } = new();
}

/// <summary>
/// Health status for an individual subsystem.
/// </summary>
public sealed class SubsystemHealth
{
    public string Status { get; set; } = HealthStatus.Ok;
    public string? Detail { get; set; }
}

/// <summary>
/// String constants for health status values.
/// </summary>
public static class HealthStatus
{
    public const string Ok = "ok";
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";
}
