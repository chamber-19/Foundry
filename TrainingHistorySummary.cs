namespace DailyDesk.Models;

public sealed class TrainingHistorySummary
{
    public int TotalAttempts { get; init; }
    public int TotalQuestions { get; init; }
    public int CorrectAnswers { get; init; }
    public IReadOnlyList<TopicMasterySummary> WeakTopics { get; init; } = Array.Empty<TopicMasterySummary>();
    public IReadOnlyList<TrainingAttemptRecord> RecentAttempts { get; init; } = Array.Empty<TrainingAttemptRecord>();
    public IReadOnlyList<ReviewRecommendation> ReviewRecommendations { get; init; } = Array.Empty<ReviewRecommendation>();
    public IReadOnlyList<OralDefenseAttemptRecord> RecentDefenseAttempts { get; init; } = Array.Empty<OralDefenseAttemptRecord>();
    public IReadOnlyList<SessionReflectionRecord> RecentReflections { get; init; } = Array.Empty<SessionReflectionRecord>();

    public string OverallSummary
    {
        get
        {
            if (TotalAttempts == 0 || TotalQuestions == 0)
            {
                return "No scored practice history yet.";
            }

            var accuracy = (double)CorrectAnswers / TotalQuestions;
            return $"{TotalAttempts} attempts, {CorrectAnswers}/{TotalQuestions} correct overall ({accuracy:P0}).";
        }
    }

    public string ReviewQueueSummary
    {
        get
        {
            if (ReviewRecommendations.Count == 0)
            {
                return "No review queue yet. Score a practice set to schedule follow-up work.";
            }

            var dueNow = ReviewRecommendations.Count(item => item.IsDue);
            var soon = ReviewRecommendations.Count(item => !item.IsDue && item.DueAt <= DateTimeOffset.Now.AddDays(2));
            return $"{dueNow} due now, {soon} due soon, {ReviewRecommendations.Count} tracked review targets.";
        }
    }

    public string DefenseSummary
    {
        get
        {
            if (RecentDefenseAttempts.Count == 0)
            {
                return "No scored oral-defense history yet.";
            }

            var totalScore = RecentDefenseAttempts.Sum(item => item.TotalScore);
            var maxScore = RecentDefenseAttempts.Sum(item => item.MaxScore);
            var ratio = maxScore == 0 ? 0 : (double)totalScore / maxScore;
            return $"{RecentDefenseAttempts.Count} recent defense attempts, {totalScore}/{maxScore} total ({ratio:P0}).";
        }
    }

    public string ReflectionSummary
    {
        get
        {
            if (RecentReflections.Count == 0)
            {
                return "No saved reflections yet.";
            }

            return $"Latest reflection: {RecentReflections[0].DisplaySummary}";
        }
    }
}
