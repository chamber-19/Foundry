namespace Foundry.Services;

public interface IModelProvider
{
    string ProviderId { get; }
    string ProviderLabel { get; }

    Task<IReadOnlyList<string>> GetInstalledModelsAsync(
        CancellationToken cancellationToken = default
    );

    Task<string> GenerateAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default
    );

    Task<T?> GenerateJsonAsync<T>(
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Checks whether the model provider backend is reachable.
    /// Returns true if the provider responds to a lightweight ping.
    /// </summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an embedding vector for the given text using the specified model.
    /// Returns null if the provider does not support embeddings or is unavailable.
    /// </summary>
    Task<float[]?> GenerateEmbeddingAsync(
        string text,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<float[]?>(null);
    }
}
