namespace Foundry.Models;

public sealed class TrainingAttemptRecord
{
    public string Title { get; init; } = string.Empty;
    public string Focus { get; init; } = string.Empty;
    public string Difficulty { get; init; } = string.Empty;
    public string GenerationSource { get; init; } = string.Empty;
    public DateTimeOffset CompletedAt { get; init; }
    public int QuestionCount { get; init; }
    public int CorrectCount { get; init; }
    public IReadOnlyList<TrainingAttemptQuestionRecord> Questions { get; init; } = Array.Empty<TrainingAttemptQuestionRecord>();

    public double Accuracy => QuestionCount == 0 ? 0 : (double)CorrectCount / QuestionCount;

    public string DisplaySummary =>
        $"{CompletedAt:yyyy-MM-dd HH:mm} | {CorrectCount}/{QuestionCount} correct | {Difficulty} | {Focus}";
}
