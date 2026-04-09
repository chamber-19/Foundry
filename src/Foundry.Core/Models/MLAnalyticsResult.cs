namespace Foundry.Models;

public sealed class MLAnalyticsResult
{
    public bool Ok { get; init; }
    public string Engine { get; init; } = "heuristic";
    public IReadOnlyList<MLTopicEntry> WeakTopics { get; init; } = Array.Empty<MLTopicEntry>();
    public IReadOnlyList<MLTopicEntry> StrongTopics { get; init; } = Array.Empty<MLTopicEntry>();
    public double OverallReadiness { get; init; }
    public IReadOnlyList<MLTopicCluster> TopicClusters { get; init; } = Array.Empty<MLTopicCluster>();
    public IReadOnlyList<MLScheduleItem> AdaptiveSchedule { get; init; } = Array.Empty<MLScheduleItem>();
    public MLOperatorPattern OperatorPattern { get; init; } = new();
    public IReadOnlyList<MLReadinessEntry> ReadinessBreakdown { get; init; } = Array.Empty<MLReadinessEntry>();
    public string? SklearnError { get; init; }
    public IReadOnlyDictionary<string, MLForgettingCurve>? ForgettingCurves { get; init; }
    public IReadOnlyDictionary<string, MLBayesianEstimate>? BayesianEstimates { get; init; }
    public IReadOnlyDictionary<string, MLPlateauStatus>? PlateauStatus { get; init; }
}

public sealed class MLTopicEntry
{
    public string Topic { get; init; } = string.Empty;
    public double Accuracy { get; init; }
    public int TotalQuestions { get; init; }
    public int CorrectCount { get; init; }
}

public sealed class MLTopicCluster
{
    public int ClusterId { get; init; }
    public IReadOnlyList<string> Topics { get; init; } = Array.Empty<string>();
    public double AverageAccuracy { get; init; }
    public double? AverageStability { get; init; }
    public string Label { get; init; } = string.Empty;
}

public sealed class MLScheduleItem
{
    public string Topic { get; init; } = string.Empty;
    public int Priority { get; init; }
    public string RecommendedSessionType { get; init; } = "practice";
    public int IntervalDays { get; init; }
    public string Reason { get; init; } = string.Empty;
    public double? AverageStudyGapDays { get; init; }
    public bool? ForgettingCurveInterval { get; init; }
}

public sealed class MLOperatorPattern
{
    public double ApproveRate { get; init; }
    public double RejectRate { get; init; }
    public double DeferRate { get; init; }
    public int TotalDecisions { get; init; }
    public string Pattern { get; init; } = "balanced";
}

public sealed class MLReadinessEntry
{
    public string Topic { get; init; } = string.Empty;
    public double Readiness { get; init; }
    public double Confidence { get; init; }
    public double? Trend { get; init; }
    public bool? Improving { get; init; }
    public double? BayesianMean { get; init; }
    public double? CredibleIntervalLower { get; init; }
    public double? CredibleIntervalUpper { get; init; }
    public double? StabilityDays { get; init; }
    public bool? Plateaued { get; init; }
    public string? PlateauRecommendation { get; init; }
}

public sealed class MLForgettingCurve
{
    public double StabilityDays { get; init; }
    public IReadOnlyDictionary<string, double>? PredictedRetention { get; init; }
}

public sealed class MLBayesianEstimate
{
    public double Mean { get; init; }
    public double Lower90 { get; init; }
    public double Upper90 { get; init; }
    public int Samples { get; init; }
}

public sealed class MLPlateauStatus
{
    public bool Plateaued { get; init; }
    public double? Accuracy { get; init; }
    public bool? Regressing { get; init; }
    public string? Reason { get; init; }
    public string? Recommendation { get; init; }
}
