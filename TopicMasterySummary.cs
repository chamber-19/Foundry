namespace DailyDesk.Models;

public sealed class TopicMasterySummary
{
    public string Topic { get; init; } = string.Empty;
    public int Attempted { get; init; }
    public int Correct { get; init; }

    public double Accuracy => Attempted == 0 ? 0 : (double)Correct / Attempted;

    public string DisplaySummary =>
        $"{Topic}: {Correct}/{Attempted} correct ({Accuracy:P0})";
}
