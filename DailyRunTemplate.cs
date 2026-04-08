namespace DailyDesk.Models;

public sealed class DailyRunTemplate
{
    public string DateKey { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public string Objective { get; set; } = "No daily operator plan generated yet.";
    public string MorningPlan { get; set; } = string.Empty;
    public string StudyBlock { get; set; } = string.Empty;
    public string RepoBlock { get; set; } = string.Empty;
    public string MiddayCheckpoint { get; set; } = string.Empty;
    public string EndOfDayReview { get; set; } = string.Empty;
    public List<string> CarryoverQueue { get; set; } = [];
    public string GenerationSource { get; set; } = "not generated";
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;

    public string DisplaySummary =>
        $"{DateKey} | {GenerationSource} | {CarryoverQueue.Count} carryover items";
}
