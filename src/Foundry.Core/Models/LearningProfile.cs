namespace Foundry.Models;

public sealed class LearningProfile
{
    public string Summary { get; init; } = "No learning profile yet.";
    public string CurrentNeed { get; init; } =
        "Add a few knowledge files and score a practice test to personalize the desk.";
    public IReadOnlyList<string> ActiveTopics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CoachingRules { get; init; } = Array.Empty<string>();
}
