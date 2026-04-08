using DailyDesk.Models;

namespace DailyDesk.Services;

/// <summary>
/// Domain coordinator for knowledge operations: document indexing and context status retrieval.
/// Extracted from OfficeBrokerOrchestrator per TECHNICAL-DEBT.md.
/// </summary>
public sealed class KnowledgeCoordinator
{
    private readonly EmbeddingService _embeddingService;
    private readonly VectorStoreService _vectorStoreService;
    private readonly KnowledgeIndexStore _knowledgeIndexStore;

    public KnowledgeCoordinator(
        EmbeddingService embeddingService,
        VectorStoreService vectorStoreService,
        KnowledgeIndexStore knowledgeIndexStore)
    {
        _embeddingService = embeddingService;
        _vectorStoreService = vectorStoreService;
        _knowledgeIndexStore = knowledgeIndexStore;
    }

    /// <summary>
    /// Indexes all provided knowledge documents by generating embeddings and storing them
    /// in the vector store. Skips documents that have no extractable text content or have
    /// not changed since last indexing (content hash unchanged).
    /// </summary>
    public async Task<KnowledgeIndexResult> RunKnowledgeIndexAsync(
        IReadOnlyList<LearningDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var indexed = 0;
        var skipped = 0;
        var failed = 0;
        var total = documents.Count;

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var textContent = string.IsNullOrEmpty(document.ExtractedText)
                ? document.Summary
                : document.ExtractedText;
            if (string.IsNullOrWhiteSpace(textContent))
            {
                skipped++;
                continue;
            }

            var contentHash = KnowledgeIndexStore.ComputeContentHash(textContent);
            if (!_knowledgeIndexStore.NeedsIndexing(document.RelativePath, contentHash))
            {
                skipped++;
                continue;
            }

            var embedding = await _embeddingService.GenerateEmbeddingAsync(textContent, cancellationToken);
            if (embedding is null)
            {
                failed++;
                continue;
            }

            var vectorId = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(document.RelativePath)))[..32];

            var metadata = new Dictionary<string, string>
            {
                ["path"] = document.RelativePath,
                ["kind"] = document.Kind,
                ["source"] = document.SourceRootLabel,
            };
            if (document.Topics.Count > 0)
            {
                metadata["topics"] = string.Join(", ", document.Topics.Take(5));
            }

            var upserted = await _vectorStoreService.UpsertAsync(vectorId, embedding, metadata, cancellationToken);
            if (upserted)
            {
                _knowledgeIndexStore.MarkIndexed(document.RelativePath, contentHash, vectorId);
                indexed++;
            }
            else
            {
                failed++;
            }
        }

        return new KnowledgeIndexResult
        {
            TotalDocuments = total,
            Indexed = indexed,
            Skipped = skipped,
            Failed = failed,
            IndexedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// Returns the current knowledge index status including indexed count and vector store info.
    /// </summary>
    public async Task<KnowledgeIndexStatus> GetKnowledgeIndexStatusAsync(
        int totalDocuments,
        CancellationToken cancellationToken = default)
    {
        var indexedCount = _knowledgeIndexStore.GetIndexedCount();
        var collectionInfo = await _vectorStoreService.GetCollectionInfoAsync(cancellationToken);

        return new KnowledgeIndexStatus
        {
            TotalDocuments = totalDocuments,
            IndexedDocuments = indexedCount,
            VectorStorePoints = collectionInfo?.PointsCount ?? 0,
            VectorStoreStatus = collectionInfo?.Status ?? "unreachable",
        };
    }
}
