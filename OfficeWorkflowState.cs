namespace DailyDesk.Models;

public sealed class OfficeWorkflowState
{
    public string ActiveRoute { get; set; } = "chief";
    public string LastAutoRoute { get; set; } = "chief";
    public string LastAutoRouteReason { get; set; } =
        "Chief is the default route until Office has enough context to steer elsewhere.";
    public string StudyFocus { get; set; } =
        "Protection, grounding, standards, drafting safety";
    public string PracticeDifficulty { get; set; } = "Mixed";
    public int PracticeQuestionCount { get; set; } = 6;
    public ResearchReport? LatestResearchReport { get; set; }
    public string LastScoredSessionMode { get; set; } = string.Empty;
    public string LastScoredSessionFocus { get; set; } = string.Empty;
    public string ReflectionContextSummary { get; set; } =
        "Score a practice or defense session before saving a reflection.";
    public DateTimeOffset? PracticeGeneratedAt { get; set; }
    public DateTimeOffset? PracticeScoredAt { get; set; }
    public DateTimeOffset? DefenseGeneratedAt { get; set; }
    public DateTimeOffset? DefenseScoredAt { get; set; }
    public DateTimeOffset? ReflectionSavedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
