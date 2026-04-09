namespace Foundry.Models;

public sealed class ReviewRecommendation
{
    public string Topic { get; init; } = string.Empty;
    public int Attempted { get; init; }
    public int Correct { get; init; }
    public DateTimeOffset LastPracticedAt { get; init; }
    public DateTimeOffset DueAt { get; init; }
    public string Priority { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;

    public double Accuracy => Attempted == 0 ? 0 : (double)Correct / Attempted;

    public bool IsDue => DueAt <= DateTimeOffset.Now;

    public string DisplaySummary =>
        $"{Topic} | {Priority} | due {DueAt:yyyy-MM-dd} | {Correct}/{Attempted} correct ({Accuracy:P0})";
}
