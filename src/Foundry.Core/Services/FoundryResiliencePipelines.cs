using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Foundry.Services;

/// <summary>
/// Factory for named Polly v8 resilience pipelines used across Foundry services.
/// Each pipeline matches the retry/circuit-breaker/timeout strategy defined in Docs/ARCHITECTURE.md.
/// </summary>
public static class FoundryResiliencePipelines
{
    /// <summary>
    /// Ollama HTTP calls: 3x retry with exponential backoff + circuit breaker.
    /// Retry delays: ~2s, ~4s, ~8s. Circuit breaker opens after 5 failures for 30s.
    /// </summary>
    public static ResiliencePipeline BuildOllamaPipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.8,
                SamplingDuration = TimeSpan.FromSeconds(60),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
            })
            .AddTimeout(TimeSpan.FromSeconds(90))
            .Build();
    }

    /// <summary>
    /// Web research (DuckDuckGo + page fetch): 2x retry + 25s timeout.
    /// Retry delays: ~1s, ~2s.
    /// </summary>
    public static ResiliencePipeline BuildWebResearchPipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
            })
            .AddTimeout(TimeSpan.FromSeconds(25))
            .Build();
    }

    /// <summary>
    /// Python subprocess calls: 1x retry + 90s timeout.
    /// Handles transient process spawn failures.
    /// </summary>
    public static ResiliencePipeline BuildPythonSubprocessPipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder()
                    .Handle<InvalidOperationException>()
                    .Handle<System.ComponentModel.Win32Exception>(),
            })
            .AddTimeout(TimeSpan.FromSeconds(90))
            .Build();
    }
}
