using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using Polly;

namespace Foundry.Services;

public sealed class OllamaService : IModelProvider
{
    public const string OllamaProviderId = "ollama";
    public const string OllamaProviderLabel = "Ollama (local)";

    private readonly OllamaApiClient _client;
    private readonly ProcessRunner _processRunner;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly ILogger<OllamaService> _logger;
    private readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public string ProviderId => OllamaProviderId;
    public string ProviderLabel => OllamaProviderLabel;

    public OllamaService(string endpoint, ProcessRunner processRunner, ResiliencePipeline? resiliencePipeline = null, ILogger<OllamaService>? logger = null)
    {
        var uri = new Uri(endpoint.EndsWith("/") ? endpoint : $"{endpoint}/");
        var httpClient = new System.Net.Http.HttpClient
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(90),
        };
        _client = new OllamaApiClient(httpClient);
        _processRunner = processRunner;
        _resiliencePipeline = resiliencePipeline ?? ResiliencePipeline.Empty;
        _logger = logger ?? NullLogger<OllamaService>.Instance;
    }

    public async Task<IReadOnlyList<string>> GetInstalledModelsAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var modelsResponse = await _resiliencePipeline.ExecuteAsync(
                async ct => await _client.ListLocalModelsAsync(ct),
                cancellationToken
            );
            var models = modelsResponse
                .Select(m => m.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (models.Count > 0)
            {
                return models;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama API model listing failed, falling back to CLI.");
        }

        try
        {
            var output = await _processRunner.RunAsync("ollama", "list", null, cancellationToken);
            return output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama CLI model listing failed, returning empty list.");
            return Array.Empty<string>();
        }
    }

    public async Task<string> GenerateAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default
    )
    {
        var request = new ChatRequest
        {
            Model = model,
            Stream = false,
            Messages =
            [
                new Message(ChatRole.System, systemPrompt),
                new Message(ChatRole.User, userPrompt),
            ],
        };

        return await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            var responseStream = _client.ChatAsync(request, ct);
            ChatResponseStream? lastChunk = null;

            await foreach (var chunk in responseStream.WithCancellation(ct))
            {
                lastChunk = chunk;
            }

            return lastChunk?.Message?.Content?.Trim() ?? string.Empty;
        }, cancellationToken);
    }

    public async Task<T?> GenerateJsonAsync<T>(
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default
    )
    {
        var request = new ChatRequest
        {
            Model = model,
            Stream = false,
            Format = "json",
            Messages =
            [
                new Message(ChatRole.System, systemPrompt),
                new Message(ChatRole.User, userPrompt),
            ],
        };

        return await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            var responseStream = _client.ChatAsync(request, ct);
            ChatResponseStream? lastChunk = null;

            await foreach (var chunk in responseStream.WithCancellation(ct))
            {
                lastChunk = chunk;
            }

            var json = lastChunk?.Message?.Content?.Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }, cancellationToken);
    }

    /// <summary>
    /// Pings the Ollama API by listing models. Returns true if reachable.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.ListLocalModelsAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generates an embedding vector via the Ollama /api/embed endpoint.
    /// Returns null if Ollama is unavailable or the model does not support embeddings.
    /// </summary>
    public async Task<float[]?> GenerateEmbeddingAsync(
        string text,
        string? model = null,
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
                    Model = model ?? EmbeddingService.DefaultEmbeddingModel,
                    Input = [text],
                };
                return await _client.EmbedAsync(request, ct);
            }, cancellationToken);

            if (response?.Embeddings is { Count: > 0 })
            {
                return response.Embeddings[0].Select(d => (float)d).ToArray();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding generation failed, returning null.");
            return null;
        }
    }
}
