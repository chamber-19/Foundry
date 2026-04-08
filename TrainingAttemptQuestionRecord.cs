namespace DailyDesk.Models;

public sealed class TrainingAttemptQuestionRecord
{
    public string Topic { get; init; } = string.Empty;
    public string Difficulty { get; init; } = string.Empty;
    public bool Correct { get; init; }
}
