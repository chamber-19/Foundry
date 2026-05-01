using Foundry.Models;
using Foundry.Services;
using OllamaSharp;
using Xunit;

namespace Foundry.Core.Tests;

/// <summary>
/// Unit tests for KnowledgeCoordinator covering document indexing and status retrieval.
/// EmbeddingService and VectorStoreService both fall back gracefully when their external
/// dependencies (Ollama, Qdrant) are unavailable, allowing tests to run in CI without
/// those services.
/// </summary>
[Collection("CoordinatorTests")]
public sealed class KnowledgeCoordinatorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "knowledgecoord-test-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Creates an EmbeddingService that always returns null (Ollama unavailable).
    /// </summary>
    private static EmbeddingService BuildUnavailableEmbeddingService()
    {
        var client = new OllamaApiClient(new Uri("http://localhost:11999")); // unreachable port
        return new EmbeddingService(client);
    }

    /// <summary>
    /// Creates a VectorStoreService pointing at a non-existent Qdrant instance (graceful fallback).
    /// </summary>
    private static VectorStoreService BuildUnavailableVectorStore() =>
        new VectorStoreService(host: "localhost", port: 16335); // non-default port, not running

    private static KnowledgeCoordinator BuildCoordinator(FoundryDatabase db)
    {
        return new KnowledgeCoordinator(
            BuildUnavailableEmbeddingService(),
            BuildUnavailableVectorStore(),
            new KnowledgeIndexStore(db));
    }

    private static LearningDocument MakeDocument(string path, string extractedText = "", string summary = "") =>
        new LearningDocument
        {
            RelativePath = path,
            Kind = "markdown",
            SourceRootLabel = "test",
            ExtractedText = extractedText,
            Summary = summary,
        };

    // -------------------------------------------------------------------------
    // RunKnowledgeIndexAsync — document filtering
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunKnowledgeIndexAsync_EmptyList_ReturnsZeroCounts()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = BuildCoordinator(db);

        var result = await coordinator.RunKnowledgeIndexAsync([]);

        Assert.Equal(0, result.TotalDocuments);
        Assert.Equal(0, result.Indexed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task RunKnowledgeIndexAsync_DocumentWithNoText_IsSkipped()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = BuildCoordinator(db);

        var documents = new[]
        {
            MakeDocument("doc/empty.md"),
        };

        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        Assert.Equal(1, result.TotalDocuments);
        Assert.Equal(0, result.Indexed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task RunKnowledgeIndexAsync_DocumentWithWhitespaceOnlyText_IsSkipped()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = BuildCoordinator(db);

        var documents = new[]
        {
            MakeDocument("doc/blank.md", extractedText: "   \n\t  "),
        };

        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        Assert.Equal(1, result.TotalDocuments);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task RunKnowledgeIndexAsync_DocumentWithOnlyEmptyExtractedText_UsesSummaryFallback()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = BuildCoordinator(db);

        // ExtractedText is empty — coordinator falls back to Summary as text content.
        // With unavailable embedding service the document will fail (not skip).
        var documents = new[]
        {
            MakeDocument("doc/summary-only.md", summary: "This is the document summary."),
        };

        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        Assert.Equal(1, result.TotalDocuments);
        Assert.Equal(0, result.Skipped); // Summary provides content so document is not skipped
        Assert.Equal(1, result.Failed);  // Embedding service unavailable → failed
    }

    [Fact]
    public async Task RunKnowledgeIndexAsync_AlreadyIndexedDocument_IsSkipped()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var indexStore = new KnowledgeIndexStore(db);

        // Pre-mark the document as indexed
        const string path = "doc/already-indexed.md";
        const string text = "Some content that was already indexed.";
        var hash = KnowledgeIndexStore.ComputeContentHash(text);
        indexStore.MarkIndexed(path, hash, "vec-existing-001");

        var coordinator = new KnowledgeCoordinator(
            BuildUnavailableEmbeddingService(),
            BuildUnavailableVectorStore(),
            indexStore);

        var documents = new[]
        {
            MakeDocument(path, extractedText: text),
        };

        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        Assert.Equal(1, result.TotalDocuments);
        Assert.Equal(0, result.Indexed);
        Assert.Equal(1, result.Skipped); // same hash — no re-indexing needed
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task RunKnowledgeIndexAsync_ChangedDocument_SkipsAlreadyIndexedThenAttempts()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var indexStore = new KnowledgeIndexStore(db);

        const string path = "doc/changed.md";
        indexStore.MarkIndexed(path, "old-hash", "vec-old");

        var coordinator = new KnowledgeCoordinator(
            BuildUnavailableEmbeddingService(),
            BuildUnavailableVectorStore(),
            indexStore);

        // Different content → different hash → NeedsIndexing = true
        var documents = new[]
        {
            MakeDocument(path, extractedText: "New content after document update."),
        };

        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        Assert.Equal(1, result.TotalDocuments);
        Assert.Equal(0, result.Skipped);
        // Embedding service unavailable → counted as failed
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task RunKnowledgeIndexAsync_EmbeddingUnavailable_CountsAsFailed()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = BuildCoordinator(db);

        var documents = new[]
        {
            MakeDocument("doc/needs-embed.md", extractedText: "Plenty of text content to embed."),
        };

        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        Assert.Equal(1, result.TotalDocuments);
        Assert.Equal(0, result.Indexed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task RunKnowledgeIndexAsync_MixedDocuments_CountsAreCorrect()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var indexStore = new KnowledgeIndexStore(db);

        // Pre-index one document
        const string alreadyIndexedPath = "doc/indexed.md";
        const string alreadyIndexedText = "Already indexed content.";
        indexStore.MarkIndexed(alreadyIndexedPath, KnowledgeIndexStore.ComputeContentHash(alreadyIndexedText), "vec-001");

        var coordinator = new KnowledgeCoordinator(
            BuildUnavailableEmbeddingService(),
            BuildUnavailableVectorStore(),
            indexStore);

        var documents = new[]
        {
            MakeDocument("doc/no-text.md"),                                      // skipped: no text
            MakeDocument(alreadyIndexedPath, extractedText: alreadyIndexedText), // skipped: already indexed
            MakeDocument("doc/new.md", extractedText: "New document content."),  // failed: embedding unavailable
        };

        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        Assert.Equal(3, result.TotalDocuments);
        Assert.Equal(0, result.Indexed);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task RunKnowledgeIndexAsync_SetsIndexedAtTimestamp()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = BuildCoordinator(db);

        var before = DateTimeOffset.Now.AddSeconds(-1);
        var result = await coordinator.RunKnowledgeIndexAsync([]);

        Assert.True(result.IndexedAt >= before);
    }

    [Fact]
    public async Task RunKnowledgeIndexAsync_TopicsIncludedInMetadata_DocumentHasTopics()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = BuildCoordinator(db);

        var document = new LearningDocument
        {
            RelativePath = "doc/topics.md",
            Kind = "markdown",
            SourceRootLabel = "test",
            ExtractedText = "Content about relay protection and grounding.",
            Topics = ["relay protection", "grounding", "fault analysis"],
        };

        // With unavailable embedding service the document will fail, but we confirm
        // the coordinator does not throw — topics do not affect document skipping.
        var result = await coordinator.RunKnowledgeIndexAsync([document]);

        Assert.Equal(1, result.TotalDocuments);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.Failed);
    }

    // -------------------------------------------------------------------------
    // GetKnowledgeIndexStatusAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetKnowledgeIndexStatusAsync_ZeroDocuments_ReturnsCorrectCounts()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = BuildCoordinator(db);

        var status = await coordinator.GetKnowledgeIndexStatusAsync(totalDocuments: 0);

        Assert.Equal(0, status.TotalDocuments);
        Assert.Equal(0, status.IndexedDocuments);
    }

    [Fact]
    public async Task GetKnowledgeIndexStatusAsync_ReflectsTotalDocumentsParameter()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = BuildCoordinator(db);

        var status = await coordinator.GetKnowledgeIndexStatusAsync(totalDocuments: 42);

        Assert.Equal(42, status.TotalDocuments);
    }

    [Fact]
    public async Task GetKnowledgeIndexStatusAsync_ReturnsIndexedCountFromStore()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var indexStore = new KnowledgeIndexStore(db);

        indexStore.MarkIndexed("doc/a.md", "hash-a", "vec-a");
        indexStore.MarkIndexed("doc/b.md", "hash-b", "vec-b");

        var coordinator = new KnowledgeCoordinator(
            BuildUnavailableEmbeddingService(),
            BuildUnavailableVectorStore(),
            indexStore);

        var status = await coordinator.GetKnowledgeIndexStatusAsync(totalDocuments: 5);

        Assert.Equal(5, status.TotalDocuments);
        Assert.Equal(2, status.IndexedDocuments);
    }

    [Fact]
    public async Task GetKnowledgeIndexStatusAsync_QdrantUnreachable_ReturnsUnreachableStatus()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = BuildCoordinator(db);

        var status = await coordinator.GetKnowledgeIndexStatusAsync(totalDocuments: 10);

        // VectorStoreService returns null when Qdrant is unreachable;
        // coordinator maps this to "unreachable" status and 0 vector store points.
        Assert.Equal("unreachable", status.VectorStoreStatus);
        Assert.Equal(0UL, status.VectorStorePoints);
    }

    [Fact]
    public async Task GetKnowledgeIndexStatusAsync_ReflectsIncrementalIndexing()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var indexStore = new KnowledgeIndexStore(db);
        var coordinator = new KnowledgeCoordinator(
            BuildUnavailableEmbeddingService(),
            BuildUnavailableVectorStore(),
            indexStore);

        var statusBefore = await coordinator.GetKnowledgeIndexStatusAsync(totalDocuments: 3);
        Assert.Equal(0, statusBefore.IndexedDocuments);

        indexStore.MarkIndexed("doc/x.md", "hx", "vx");

        var statusAfter = await coordinator.GetKnowledgeIndexStatusAsync(totalDocuments: 3);
        Assert.Equal(1, statusAfter.IndexedDocuments);
    }

    // -------------------------------------------------------------------------
    // Cancellation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunKnowledgeIndexAsync_PreCancelledToken_ThrowsOperationCancelled()
    {
        using var tmpDir = new TempDirectory();
        using var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = BuildCoordinator(db);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var document = MakeDocument("doc/test.md", extractedText: "Some content.");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.RunKnowledgeIndexAsync([document], cts.Token));
    }
}
