namespace Foundry.Models;

/// <summary>
/// Represents a single semantic search result returned to the WPF client.
/// </summary>
public sealed class KnowledgeSearchResult
{
    public string DocumentId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Excerpt { get; set; } = string.Empty;
    public float Score { get; set; }

    /// <summary>
    /// Human-readable similarity label derived from the score.
    /// </summary>
    public string SimilarityLabel => Score switch
    {
        >= 0.9f => "Very High",
        >= 0.75f => "High",
        >= 0.5f => "Moderate",
        >= 0.3f => "Low",
        _ => "Weak",
    };

    public string DisplaySummary =>
        string.IsNullOrWhiteSpace(Title)
            ? $"[{Score:P0}] {DocumentId}"
            : $"[{Score:P0}] {Title}";
}
