namespace DailyDesk.Models;

public sealed class MLForecastResult
{
    public bool Ok { get; init; }
    public string Engine { get; init; } = "linear";
    public IReadOnlyList<MLTopicForecast> Forecasts { get; init; } = Array.Empty<MLTopicForecast>();
    public IReadOnlyList<MLPlateauDetection> Plateaus { get; init; } = Array.Empty<MLPlateauDetection>();
    public IReadOnlyList<MLAnomaly> Anomalies { get; init; } = Array.Empty<MLAnomaly>();
    public IReadOnlyList<MLMasteryEstimate> MasteryEstimates { get; init; } = Array.Empty<MLMasteryEstimate>();
    public string? TensorflowError { get; init; }
}

public sealed class MLTopicForecast
{
    public string Topic { get; init; } = string.Empty;
    public double CurrentAccuracy { get; init; }
    public double PredictedNextSession { get; init; }
    public double Predicted5Sessions { get; init; }
    public string Trend { get; init; } = "stable";
    public double TrendSlope { get; init; }
    public int DataPoints { get; init; }
    public double Confidence { get; init; }
}

public sealed class MLPlateauDetection
{
    public string Topic { get; init; } = string.Empty;
    public double PlateauAccuracy { get; init; }
    public int SessionsSincePlateau { get; init; }
    public string Recommendation { get; init; } = string.Empty;
}

public sealed class MLAnomaly
{
    public string Topic { get; init; } = string.Empty;
    public double PreviousAverage { get; init; }
    public double CurrentAccuracy { get; init; }
    public double Drop { get; init; }
    public string Severity { get; init; } = "moderate";
}

public sealed class MLMasteryEstimate
{
    public string Topic { get; init; } = string.Empty;
    public double CurrentAccuracy { get; init; }
    public double TargetAccuracy { get; init; } = 0.9;
    public int EstimatedSessions { get; init; }
    public int EstimatedDays { get; init; }
    public double Confidence { get; init; }
    public bool? Mastered { get; init; }
}
