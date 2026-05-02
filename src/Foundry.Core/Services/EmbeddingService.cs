using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using OllamaSharp.Models;
using Polly;

namespace Foundry.Services;

/// <summary>
/// Generates text embeddings via Ollama's /api/embed endpoint.
/// Falls back gracefully (returns null) when Ollama is unavailable.
/// </summary>
public sealed class EmbeddingService
{
    public const string DefaultEmbeddingModel = "nomic-embed-text";

    private readonly OllamaApiClient _client;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly string _model;

    public EmbeddingService(
        OllamaApiClient client,
        string? model = null,
        ResiliencePipeline? resiliencePipeline = null,
        ILogger<EmbeddingService>? logger = null)
    {
        _client = client;
        _model = model ?? DefaultEmbeddingModel;
        _resiliencePipeline = resiliencePipeline ?? ResiliencePipeline.Empty;
        _logger = logger ?? NullLogger<EmbeddingService>.Instance;
    }

    /// <summary>
    /// Generates an embedding vector for the given text.
    /// Returns null if Ollama is unavailable or the model does not support embeddings.
    /// </summary>
    public async Task<float[]?> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var response = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var request = new EmbedRequest
                {
                    Model = _model,
                    Input = [text],
                };
                return await _client.EmbedAsync(request, ct);
            }, cancellationToken);

            if (response?.Embeddings is { Count: > 0 })
            {
                var embedding = response.Embeddings[0];
                return embedding.Select(d => (float)d).ToArray();
            }

            _logger.LogWarning("Ollama returned empty embeddings for model {Model}.", _model);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding generation failed for model {Model}, returning null.", _model);
            return null;
        }
    }

    /// <summary>
    /// Generates embedding vectors for multiple texts in a single request.
    /// Returns null if Ollama is unavailable.
    /// </summary>
    public async Task<float[][]?> GenerateBatchEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
        {
            return null;
        }

        try
        {
            var response = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var request = new EmbedRequest
                {
                    Model = _model,
                    Input = texts.ToList(),
                };
                return await _client.EmbedAsync(request, ct);
            }, cancellationToken);

            if (response?.Embeddings is { Count: > 0 })
            {
                return response.Embeddings
                    .Select(e => e.Select(d => (float)d).ToArray())
                    .ToArray();
            }

            _logger.LogWarning("Ollama returned empty batch embeddings for model {Model}.", _model);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch embedding generation failed for model {Model}, returning null.", _model);
            return null;
        }
    }

    /// <summary>
    /// The embedding model this service is configured to use.
    /// </summary>
    public string Model => _model;
}
