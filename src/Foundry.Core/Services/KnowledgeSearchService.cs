using Foundry.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Services;

/// <summary>
/// Provides semantic search over the knowledge library using Qdrant vector search.
/// Falls back to keyword-based text search when Qdrant is unavailable.
/// </summary>
public sealed class KnowledgeSearchService
{
    private readonly EmbeddingService _embeddingService;
    private readonly VectorStoreService _vectorStoreService;
    private readonly ILogger<KnowledgeSearchService> _logger;

    public KnowledgeSearchService(
        EmbeddingService embeddingService,
        VectorStoreService vectorStoreService,
        ILogger<KnowledgeSearchService>? logger = null)
    {
        _embeddingService = embeddingService;
        _vectorStoreService = vectorStoreService;
        _logger = logger ?? NullLogger<KnowledgeSearchService>.Instance;
    }

    /// <summary>
    /// Performs semantic search over the knowledge index.
    /// Falls back to text-based search over the provided library when Qdrant is unreachable.
    /// Provide <paramref name="fallbackLibrary"/> if text-based fallback is desired when
    /// embedding generation or Qdrant is unavailable.
    /// </summary>
    public async Task<KnowledgeSearchResponse> SearchAsync(
        string query,
        LearningLibrary? fallbackLibrary = null,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new KnowledgeSearchResponse
            {
                Query = query ?? string.Empty,
                SearchMode = "none",
            };
        }

        // Attempt semantic search first
        var embedding = await _embeddingService.GenerateEmbeddingAsync(query, cancellationToken);
        if (embedding is not null)
        {
            var vectorResults = await _vectorStoreService.SearchAsync(embedding, topK, cancellationToken);
            if (vectorResults.Count > 0)
            {
                var results = vectorResults.Select(vr => new KnowledgeSearchResult
                {
                    DocumentId = vr.DocumentId,
                    Title = vr.Metadata.GetValueOrDefault("title", string.Empty),
                    Excerpt = vr.Metadata.GetValueOrDefault("excerpt", string.Empty),
                    Score = vr.Score,
                }).ToList();

                return new KnowledgeSearchResponse
                {
                    Query = query,
                    SearchMode = "semantic",
                    Results = results,
                    TotalResults = results.Count,
                };
            }

            _logger.LogInformation("Semantic search returned no results for query; falling back to text search.");
        }
        else
        {
            _logger.LogInformation("Embedding generation unavailable; falling back to text search.");
        }

        // Fallback: text-based search over the learning library
        return FallbackTextSearch(query, fallbackLibrary, topK);
    }

    /// <summary>
    /// Simple keyword-based search used when Qdrant is unavailable.
    /// </summary>
    public static KnowledgeSearchResponse FallbackTextSearch(
        string query,
        LearningLibrary? library,
        int topK = 5)
    {
        if (library is null || library.Documents.Count == 0)
        {
            return new KnowledgeSearchResponse
            {
                Query = query,
                SearchMode = "text",
            };
        }

        var queryTokens = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();

        if (queryTokens.Count == 0)
        {
            return new KnowledgeSearchResponse
            {
                Query = query,
                SearchMode = "text",
            };
        }

        var scored = library.Documents
            .Select(doc =>
            {
                var searchable = $"{doc.FileName} {doc.PromptSummary} {string.Join(" ", doc.Topics)}"
                    .ToLowerInvariant();
                var matchCount = queryTokens.Count(token => searchable.Contains(token));
                var score = queryTokens.Count > 0 ? (float)matchCount / queryTokens.Count : 0f;
                return new { Document = doc, Score = score, MatchCount = matchCount };
            })
            .Where(item => item.MatchCount > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.MatchCount)
            .Take(topK)
            .ToList();

        var results = scored.Select(item => new KnowledgeSearchResult
        {
            DocumentId = item.Document.RelativePath,
            Title = item.Document.FileName,
            Excerpt = Truncate(item.Document.PromptSummary, 300),
            Score = item.Score,
        }).ToList();

        return new KnowledgeSearchResponse
        {
            Query = query,
            SearchMode = "text",
            Results = results,
            TotalResults = results.Count,
        };
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text ?? string.Empty;
        }

        return text[..maxLength] + "…";
    }
}

/// <summary>
/// Response from a knowledge search operation.
/// </summary>
public sealed class KnowledgeSearchResponse
{
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// "semantic" when Qdrant was used, "text" for fallback keyword search, "none" for empty queries.
    /// </summary>
    public string SearchMode { get; set; } = "none";

    public List<KnowledgeSearchResult> Results { get; set; } = [];
    public int TotalResults { get; set; }
}
