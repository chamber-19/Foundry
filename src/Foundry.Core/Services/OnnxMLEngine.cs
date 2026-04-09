using System.IO;
using System.Numerics.Tensors;
using Foundry.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Foundry.Services;

/// <summary>
/// In-process ONNX Runtime engine for ML inference.
/// Eliminates the need for Python subprocesses when ONNX models are available.
/// Models are loaded lazily and cached for the lifetime of the engine.
/// </summary>
public sealed class OnnxMLEngine : IDisposable
{
    private readonly string _modelsDirectory;
    private readonly object _sessionLock = new();
    private InferenceSession? _analyticsSession;
    private InferenceSession? _embeddingsSession;
    private InferenceSession? _forecastSession;
    private bool _disposed;

    public OnnxMLEngine(string modelsDirectory)
    {
        _modelsDirectory = modelsDirectory;
    }

    /// <summary>
    /// Checks whether the analytics ONNX model is available on disk.
    /// </summary>
    public bool IsAnalyticsModelAvailable =>
        File.Exists(Path.Combine(_modelsDirectory, "analytics.onnx"));

    /// <summary>
    /// Checks whether the embeddings ONNX model is available on disk.
    /// </summary>
    public bool IsEmbeddingsModelAvailable =>
        File.Exists(Path.Combine(_modelsDirectory, "embeddings.onnx"));

    /// <summary>
    /// Checks whether the forecast ONNX model is available on disk.
    /// </summary>
    public bool IsForecastModelAvailable =>
        File.Exists(Path.Combine(_modelsDirectory, "forecast.onnx"));

    /// <summary>
    /// Returns true if any ONNX model file is present.
    /// </summary>
    public bool HasAnyModel =>
        Directory.Exists(_modelsDirectory) &&
        (IsAnalyticsModelAvailable || IsEmbeddingsModelAvailable || IsForecastModelAvailable);

    /// <summary>
    /// Runs learning analytics using the ONNX analytics model.
    /// Returns null if the model is not available.
    /// </summary>
    public MLAnalyticsResult? RunAnalytics(
        IReadOnlyList<TrainingAttemptRecord> attempts)
    {
        if (!IsAnalyticsModelAvailable)
            return null;

        var session = GetOrCreateSession(
            ref _analyticsSession,
            Path.Combine(_modelsDirectory, "analytics.onnx"));

        if (session is null)
            return null;

        // Build per-topic accuracy from training data
        var topicAccuracy = ComputeTopicAccuracy(attempts);

        // Build feature tensor: [1, numTopics, 4] where features are
        // [accuracy, totalQuestions, correctCount, attemptCount]
        var topics = topicAccuracy.Keys.ToList();
        var numTopics = Math.Max(topics.Count, 1);

        var features = new float[numTopics * 4];
        for (int i = 0; i < topics.Count; i++)
        {
            var (correct, total, attemptCount) = topicAccuracy[topics[i]];
            var accuracy = total > 0 ? (float)correct / total : 0f;
            features[i * 4 + 0] = accuracy;
            features[i * 4 + 1] = total;
            features[i * 4 + 2] = correct;
            features[i * 4 + 3] = attemptCount;
        }

        try
        {
            var inputTensor = new DenseTensor<float>(features, [1, numTopics, 4]);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor),
            };

            using var results = session.Run(inputs);
            var output = results.First().AsTensor<float>();

            // Interpret ONNX output as readiness scores per topic
            var weak = new List<MLTopicEntry>();
            var strong = new List<MLTopicEntry>();
            var readinessBreakdown = new List<MLReadinessEntry>();

            for (int i = 0; i < topics.Count; i++)
            {
                var (correct, total, _) = topicAccuracy[topics[i]];
                var accuracy = total > 0 ? (double)correct / total : 0.0;
                var readiness = i < output.Length ? Math.Round(output[i], 3) : accuracy;

                var entry = new MLTopicEntry
                {
                    Topic = topics[i],
                    Accuracy = Math.Round(accuracy, 3),
                    TotalQuestions = total,
                    CorrectCount = correct,
                };

                if (readiness < 0.6)
                    weak.Add(entry);
                else
                    strong.Add(entry);

                readinessBreakdown.Add(new MLReadinessEntry
                {
                    Topic = topics[i],
                    Readiness = readiness,
                    Confidence = Math.Min(1.0, total / 20.0),
                });
            }

            var overallReadiness = readinessBreakdown.Count > 0
                ? readinessBreakdown.Average(r => r.Readiness)
                : 0.0;

            return new MLAnalyticsResult
            {
                Ok = true,
                Engine = "onnx",
                WeakTopics = weak.OrderBy(t => t.Accuracy).ToList(),
                StrongTopics = strong.OrderByDescending(t => t.Accuracy).ToList(),
                OverallReadiness = Math.Round(overallReadiness, 3),
                OperatorPattern = BuildDefaultOperatorPattern(),
                AdaptiveSchedule = BuildAdaptiveSchedule(weak),
                ReadinessBreakdown = readinessBreakdown,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs document embeddings using the ONNX embeddings model.
    /// Returns null if the model is not available.
    /// </summary>
    public MLEmbeddingsResult? RunEmbeddings(
        IReadOnlyList<LearningDocument> documents,
        string? query)
    {
        if (!IsEmbeddingsModelAvailable)
            return null;

        var session = GetOrCreateSession(
            ref _embeddingsSession,
            Path.Combine(_modelsDirectory, "embeddings.onnx"));

        if (session is null)
            return null;

        try
        {
            var embeddings = new List<MLDocumentEmbedding>();
            var vectors = new List<float[]>();

            foreach (var doc in documents)
            {
                var text = doc.ExtractedText?.Length > 5000
                    ? doc.ExtractedText[..5000]
                    : doc.ExtractedText ?? string.Empty;

                var tokenIds = SimpleTokenize(text, maxTokens: 512);
                var inputTensor = new DenseTensor<long>(
                    tokenIds.Select(t => (long)t).ToArray(),
                    [1, tokenIds.Length]);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
                };

                using var results = session.Run(inputs);
                var output = results.First().AsTensor<float>().ToArray();
                vectors.Add(output);

                embeddings.Add(new MLDocumentEmbedding
                {
                    DocumentId = doc.FullPath,
                    Title = doc.FileName,
                    Dimensions = output.Length,
                    Embedding = output.Select(v => Math.Round(v, 6)).ToList(),
                });
            }

            var similarities = ComputeSimilarities(documents, vectors);
            var queryResults = new List<MLQueryResult>();

            if (!string.IsNullOrWhiteSpace(query))
            {
                var queryTokens = SimpleTokenize(query, maxTokens: 128);
                var queryTensor = new DenseTensor<long>(
                    queryTokens.Select(t => (long)t).ToArray(),
                    [1, queryTokens.Length]);

                var queryInputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", queryTensor),
                };

                using var queryOutput = session.Run(queryInputs);
                var queryVec = queryOutput.First().AsTensor<float>().ToArray();

                for (int i = 0; i < documents.Count && i < vectors.Count; i++)
                {
                    var relevance = CosineSimilarity(queryVec, vectors[i]);
                    queryResults.Add(new MLQueryResult
                    {
                        DocumentId = documents[i].FullPath,
                        Title = documents[i].FileName,
                        Relevance = Math.Round(relevance, 4),
                    });
                }

                queryResults = queryResults.OrderByDescending(r => r.Relevance).Take(10).ToList();
            }

            return new MLEmbeddingsResult
            {
                Ok = true,
                Engine = "onnx",
                Embeddings = embeddings,
                Similarities = similarities,
                QueryResults = queryResults,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs progress forecasting using the ONNX forecast model.
    /// Returns null if the model is not available.
    /// </summary>
    public MLForecastResult? RunForecast(IReadOnlyList<TrainingAttemptRecord> attempts)
    {
        if (!IsForecastModelAvailable)
            return null;

        var session = GetOrCreateSession(
            ref _forecastSession,
            Path.Combine(_modelsDirectory, "forecast.onnx"));

        if (session is null)
            return null;

        try
        {
            var timeSeries = BuildTimeSeries(attempts);
            var forecasts = new List<MLTopicForecast>();
            var plateaus = new List<MLPlateauDetection>();
            var anomalies = new List<MLAnomaly>();
            var masteryEstimates = new List<MLMasteryEstimate>();

            foreach (var (topic, series) in timeSeries)
            {
                if (series.Count == 0)
                    continue;

                var accuracies = series.Select(s => (float)s.accuracy).ToArray();
                var windowSize = Math.Min(3, accuracies.Length);
                var inputData = accuracies.TakeLast(windowSize).ToArray();

                // Pad to windowSize if needed
                if (inputData.Length < windowSize)
                {
                    var padded = new float[windowSize];
                    Array.Copy(inputData, 0, padded, windowSize - inputData.Length, inputData.Length);
                    inputData = padded;
                }

                var inputTensor = new DenseTensor<float>(inputData, [1, windowSize, 1]);
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input", inputTensor),
                };

                using var results = session.Run(inputs);
                var prediction = results.First().AsTensor<float>()[0];

                var current = accuracies[^1];
                var slope = accuracies.Length >= 2
                    ? (accuracies[^1] - accuracies[0]) / Math.Max(1, accuracies.Length - 1)
                    : 0f;

                forecasts.Add(new MLTopicForecast
                {
                    Topic = topic,
                    CurrentAccuracy = Math.Round(current, 3),
                    PredictedNextSession = Math.Round(Math.Clamp(prediction, 0, 1), 3),
                    Predicted5Sessions = Math.Round(Math.Clamp(current + slope * 5, 0, 1), 3),
                    Trend = slope > 0.02 ? "improving" : slope < -0.02 ? "declining" : "stable",
                    TrendSlope = Math.Round(slope, 4),
                    DataPoints = accuracies.Length,
                    Confidence = Math.Round(Math.Min(1.0, accuracies.Length / 8.0), 2),
                });

                // Plateau detection
                if (accuracies.Length >= 4)
                {
                    var recent = accuracies.TakeLast(4).ToArray();
                    var mean = recent.Average();
                    var variance = recent.Select(a => (a - mean) * (a - mean)).Average();
                    if (variance < 0.005 && mean < 0.9)
                    {
                        plateaus.Add(new MLPlateauDetection
                        {
                            Topic = topic,
                            PlateauAccuracy = Math.Round(current, 3),
                            SessionsSincePlateau = Math.Min(4, accuracies.Length),
                            Recommendation = "Try oral defense or change difficulty level to break through.",
                        });
                    }
                }

                // Anomaly detection
                if (accuracies.Length >= 3)
                {
                    var prevWindow = accuracies.SkipLast(1).TakeLast(3).ToArray();
                    var avgPrev = prevWindow.Average();
                    if (avgPrev - current > 0.25)
                    {
                        anomalies.Add(new MLAnomaly
                        {
                            Topic = topic,
                            PreviousAverage = Math.Round(avgPrev, 3),
                            CurrentAccuracy = Math.Round(current, 3),
                            Drop = Math.Round(avgPrev - current, 3),
                            Severity = (avgPrev - current) > 0.4 ? "high" : "moderate",
                        });
                    }
                }

                // Mastery estimation
                if (current < 0.9 && slope > 0.001)
                {
                    var remaining = (0.9 - current) / slope;
                    masteryEstimates.Add(new MLMasteryEstimate
                    {
                        Topic = topic,
                        CurrentAccuracy = Math.Round(current, 3),
                        TargetAccuracy = 0.9,
                        EstimatedSessions = (int)remaining,
                        EstimatedDays = Math.Min((int)(remaining * 3), 365),
                        Confidence = Math.Round(Math.Min(1.0, accuracies.Length / 8.0), 2),
                    });
                }
                else if (current >= 0.9)
                {
                    masteryEstimates.Add(new MLMasteryEstimate
                    {
                        Topic = topic,
                        CurrentAccuracy = Math.Round(current, 3),
                        TargetAccuracy = 0.9,
                        EstimatedSessions = 0,
                        EstimatedDays = 0,
                        Confidence = Math.Round(Math.Min(1.0, accuracies.Length / 8.0), 2),
                        Mastered = true,
                    });
                }
            }

            return new MLForecastResult
            {
                Ok = true,
                Engine = "onnx",
                Forecasts = forecasts,
                Plateaus = plateaus,
                Anomalies = anomalies,
                MasteryEstimates = masteryEstimates,
            };
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_sessionLock)
        {
            _analyticsSession?.Dispose();
            _embeddingsSession?.Dispose();
            _forecastSession?.Dispose();
            _analyticsSession = null;
            _embeddingsSession = null;
            _forecastSession = null;
        }
    }

    private InferenceSession? GetOrCreateSession(ref InferenceSession? field, string modelPath)
    {
        if (field is not null)
            return field;

        lock (_sessionLock)
        {
            if (field is not null)
                return field;

            if (!File.Exists(modelPath))
                return null;

            try
            {
                var options = new SessionOptions
                {
                    EnableMemoryPattern = true,
                    EnableCpuMemArena = true,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                field = new InferenceSession(modelPath, options);
                return field;
            }
            catch
            {
                return null;
            }
        }
    }

    private static Dictionary<string, (int correct, int total, int attemptCount)> ComputeTopicAccuracy(
        IReadOnlyList<TrainingAttemptRecord> attempts)
    {
        var topics = new Dictionary<string, (int correct, int total, int attemptCount)>();
        foreach (var attempt in attempts)
        {
            foreach (var question in attempt.Questions)
            {
                if (!topics.ContainsKey(question.Topic))
                    topics[question.Topic] = (0, 0, 0);

                var (correct, total, count) = topics[question.Topic];
                topics[question.Topic] = (
                    correct + (question.Correct ? 1 : 0),
                    total + 1,
                    count + 1
                );
            }
        }
        return topics;
    }

    private static MLOperatorPattern BuildDefaultOperatorPattern()
    {
        return new MLOperatorPattern
        {
            ApproveRate = 0,
            RejectRate = 0,
            DeferRate = 0,
            TotalDecisions = 0,
            Pattern = "unknown",
        };
    }

    private static IReadOnlyList<MLScheduleItem> BuildAdaptiveSchedule(List<MLTopicEntry> weakTopics)
    {
        return weakTopics
            .OrderBy(t => t.Accuracy)
            .Take(5)
            .Select((t, i) => new MLScheduleItem
            {
                Topic = t.Topic,
                Priority = i + 1,
                RecommendedSessionType = t.Accuracy < 0.4 ? "practice" : "defense",
                IntervalDays = Math.Max(1, (int)((1.0 - t.Accuracy) * 7)),
                Reason = $"Accuracy {t.Accuracy:P0} is below threshold",
            })
            .ToList();
    }

    private static IReadOnlyList<MLDocumentSimilarity> ComputeSimilarities(
        IReadOnlyList<LearningDocument> documents,
        List<float[]> vectors)
    {
        var similarities = new List<MLDocumentSimilarity>();
        for (int i = 0; i < vectors.Count; i++)
        {
            for (int j = i + 1; j < vectors.Count; j++)
            {
                var sim = CosineSimilarity(vectors[i], vectors[j]);
                similarities.Add(new MLDocumentSimilarity
                {
                    DocumentA = documents[i].FullPath,
                    DocumentB = documents[j].FullPath,
                    Similarity = Math.Round(sim, 4),
                });
            }
        }
        return similarities.OrderByDescending(s => s.Similarity).Take(20).ToList();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0.0;

        var minLen = Math.Min(a.Length, b.Length);
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < minLen; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? dot / denom : 0.0;
    }

    private static int[] SimpleTokenize(string text, int maxTokens)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [0];

        // Simple character-level tokenization as a baseline.
        // Real models would use a proper tokenizer, but this works for
        // ONNX models that were trained with similar tokenization.
        var words = text.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', ';', ':', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries)
            .Take(maxTokens)
            .ToArray();

        // Hash-based token IDs for vocabulary-free operation
        var tokens = new int[words.Length];
        for (int i = 0; i < words.Length; i++)
        {
            tokens[i] = Math.Abs(words[i].GetHashCode() % 30000) + 1;
        }

        return tokens.Length > 0 ? tokens : [0];
    }

    private static Dictionary<string, List<(double accuracy, string timestamp)>> BuildTimeSeries(
        IReadOnlyList<TrainingAttemptRecord> attempts)
    {
        var series = new Dictionary<string, List<(double accuracy, string timestamp)>>();

        foreach (var attempt in attempts.OrderBy(a => a.CompletedAt))
        {
            var topicScores = new Dictionary<string, (int correct, int total)>();
            foreach (var question in attempt.Questions)
            {
                if (!topicScores.ContainsKey(question.Topic))
                    topicScores[question.Topic] = (0, 0);

                var (correct, total) = topicScores[question.Topic];
                topicScores[question.Topic] = (
                    correct + (question.Correct ? 1 : 0),
                    total + 1
                );
            }

            foreach (var (topic, (correct, total)) in topicScores)
            {
                if (!series.ContainsKey(topic))
                    series[topic] = [];

                var accuracy = total > 0 ? (double)correct / total : 0.0;
                series[topic].Add((accuracy, attempt.CompletedAt.ToString("O")));
            }
        }

        return series;
    }
}
