namespace Foundry.Models;

public sealed class MLEmbeddingsResult
{
    public bool Ok { get; init; }
    public string Engine { get; init; } = "tfidf";
    public IReadOnlyList<MLDocumentEmbedding> Embeddings { get; init; } = Array.Empty<MLDocumentEmbedding>();
    public IReadOnlyList<MLDocumentSimilarity> Similarities { get; init; } = Array.Empty<MLDocumentSimilarity>();
    public IReadOnlyList<MLQueryResult> QueryResults { get; init; } = Array.Empty<MLQueryResult>();
    public string? PytorchError { get; init; }
}

public sealed class MLDocumentEmbedding
{
    public string DocumentId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int Dimensions { get; init; }
    public IReadOnlyList<double> Embedding { get; init; } = Array.Empty<double>();
}

public sealed class MLDocumentSimilarity
{
    public string DocumentA { get; init; } = string.Empty;
    public string DocumentB { get; init; } = string.Empty;
    public double Similarity { get; init; }
}

public sealed class MLQueryResult
{
    public string DocumentId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public double Relevance { get; init; }
}
