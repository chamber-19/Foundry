using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;

namespace DailyDesk.Services;

/// <summary>
/// Builds a Semantic Kernel <see cref="Kernel"/> configured for the local Ollama endpoint.
/// The kernel uses Ollama's OpenAI-compatible chat completion API so that SK agents
/// can leverage tool-calling, function chaining, and multi-turn memory.
/// </summary>
public sealed class OfficeKernelFactory
{
    private readonly string _ollamaEndpoint;
    private readonly ILoggerFactory _loggerFactory;

    public OfficeKernelFactory(string ollamaEndpoint, ILoggerFactory? loggerFactory = null)
    {
        _ollamaEndpoint = string.IsNullOrWhiteSpace(ollamaEndpoint)
            ? "http://localhost:11434"
            : ollamaEndpoint.TrimEnd('/');
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// Creates a new <see cref="Kernel"/> wired to the Ollama chat-completion backend
    /// for the given model.  The returned kernel is ready for agent use.
    /// </summary>
    public Kernel CreateKernel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("Model ID is required.", nameof(modelId));
        }

        var builder = Kernel.CreateBuilder();

        // Ollama exposes an OpenAI-compatible /v1/chat/completions endpoint.
        // We point the OpenAI connector at that local endpoint with a dummy API key.
#pragma warning disable SKEXP0070 // Experimental Ollama connector
        builder.AddOpenAIChatCompletion(
            modelId: modelId,
            apiKey: "ollama",                            // Ollama ignores the key
            endpoint: new Uri($"{_ollamaEndpoint}/v1")); // OpenAI-compat path
#pragma warning restore SKEXP0070

        builder.Services.AddSingleton(_loggerFactory);

        return builder.Build();
    }
}
