namespace DailyDesk.Models;

public sealed class SuiteSnapshot
{
    public string RepoPath { get; init; } = string.Empty;
    public bool RepoAvailable { get; init; }
    public int ModifiedCount { get; init; }
    public int NewCount { get; init; }
    public string StatusSummary { get; init; } = string.Empty;
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> HotAreas { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RecentCommits { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NextSessionTasks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MonetizationMoves { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ProductPillars { get; init; } = Array.Empty<string>();
    public bool RuntimeStatusAvailable { get; init; }
    public string RuntimeDoctorState { get; init; } = "background";
    public string RuntimeDoctorSummary { get; init; } =
        "Suite Doctor has not produced a shared runtime snapshot yet.";
    public string RuntimeDoctorLeadDetail { get; init; } =
        "Manual doctor recommendations will appear here when the shared runtime snapshot finds drift.";
    public int ActionableIssueCount { get; init; }
    public IReadOnlyList<string> DeveloperToolGroups { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DeveloperTools { get; init; } = Array.Empty<string>();
    public string WorkshopSummary { get; init; } = string.Empty;
}
