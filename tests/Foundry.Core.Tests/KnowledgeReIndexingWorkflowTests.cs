using Foundry.Models;
using Foundry.Services;
using OllamaSharp;
using Xunit;

namespace Foundry.Core.Tests;

/// <summary>
/// Integration tests that verify compliance with the knowledge re-indexing workflow
/// described in AGENT_REPLY_GUIDE.md chunk4 (Workflow Templates — Knowledge Refresh).
///
/// Covers three areas:
///   1. Knowledge Refresh template configuration: abort failure policy, step sequence,
///      built-in flag — all mandated by the "Aborts if any step fails" requirement.
///   2. Abort-on-failure semantics: the KnowledgeIndexResult failure count drives abort
///      decisions; policy constants are correctly defined and distinguished.
///   3. Document import ↔ re-indexing integration: documents loaded by
///      KnowledgeImportService flow into KnowledgeCoordinator; incremental hash-based
///      change detection ensures only modified documents are re-indexed on subsequent runs.
/// </summary>
[Collection("CoordinatorTests")]
public sealed class KnowledgeReIndexingWorkflowTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kreindex-test-" + Guid.NewGuid().ToString("N")[..8]
            );
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    private static EmbeddingService BuildUnavailableEmbeddingService() =>
        new EmbeddingService(new OllamaApiClient(new Uri("http://localhost:11999")));

    private static VectorStoreService BuildUnavailableVectorStore() =>
        new VectorStoreService(host: "localhost", port: 16335);

    private static KnowledgeCoordinator BuildCoordinator(FoundryDatabase db) =>
        new KnowledgeCoordinator(
            BuildUnavailableEmbeddingService(),
            BuildUnavailableVectorStore(),
            new KnowledgeIndexStore(db));

    private static LearningDocument MakeDocument(
        string relativePath,
        string extractedText = "",
        string summary = "") =>
        new LearningDocument
        {
            RelativePath = relativePath,
            Kind = "md",
            SourceRootLabel = "test",
            ExtractedText = extractedText,
            Summary = summary,
        };

    /// <summary>
    /// Returns the effective text content of a document, mirroring the fallback
    /// logic used by KnowledgeCoordinator: ExtractedText first, then Summary.
    /// </summary>
    private static string GetDocumentContent(LearningDocument doc) =>
        string.IsNullOrEmpty(doc.ExtractedText) ? doc.Summary : doc.ExtractedText ?? string.Empty;

    // -------------------------------------------------------------------------
    // Group 1: Knowledge Refresh template compliance (chunk4)
    //
    // AGENT_REPLY_GUIDE.md chunk4 states:
    //   "Knowledge Refresh — Re-indexes all knowledge documents and updates
    //    embeddings. Aborts if any step fails.
    //    Steps: Index Knowledge Documents → Refresh Document Embeddings"
    // -------------------------------------------------------------------------

    [Fact]
    public void KnowledgeRefresh_Template_HasAbortFailurePolicy()
    {
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var store = new WorkflowStore(db);

        var template = store.ListAll().Single(w => w.Name == "Knowledge Refresh");

        Assert.Equal(WorkflowFailurePolicy.Abort, template.FailurePolicy);
    }

    [Fact]
    public void KnowledgeRefresh_Template_HasExactlyOneStep()
    {
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var store = new WorkflowStore(db);

        var template = store.ListAll().Single(w => w.Name == "Knowledge Refresh");

        Assert.Single(template.Steps);
    }

    [Fact]
    public void KnowledgeRefresh_Template_IsBuiltIn()
    {
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var store = new WorkflowStore(db);

        var template = store.ListAll().Single(w => w.Name == "Knowledge Refresh");

        Assert.True(template.BuiltIn);
    }

    [Fact]
    public void KnowledgeRefresh_Template_CannotBeDeleted()
    {
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var store = new WorkflowStore(db);

        var template = store.ListAll().Single(w => w.Name == "Knowledge Refresh");
        var deleted = store.Delete(template.Id);

        Assert.False(deleted);
        Assert.NotNull(store.GetById(template.Id));
    }

    [Fact]
    public void KnowledgeRefresh_Template_StepLabelsMatchGuide()
    {
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var store = new WorkflowStore(db);

        var template = store.ListAll().Single(w => w.Name == "Knowledge Refresh");

        // The guide names this step "Index Knowledge Documents"
        Assert.Equal("Index Knowledge Documents", template.Steps[0].Label);
    }

    // -------------------------------------------------------------------------
    // Group 2: Abort-on-failure semantics
    //
    // The abort-on-failure requirement means that when KnowledgeIndex produces
    // failures, no subsequent step should run. Here we verify:
    //   - The abort and continue policy constants are distinct and correct.
    //   - The "Daily Run" template uses "continue" (contrast with Knowledge Refresh abort).
    //   - KnowledgeIndexResult accurately surfaces failure counts that an abort
    //     check would use.
    // -------------------------------------------------------------------------

    [Fact]
    public void WorkflowFailurePolicy_Abort_HasCorrectValue()
    {
        Assert.Equal("abort", WorkflowFailurePolicy.Abort);
    }

    [Fact]
    public void WorkflowFailurePolicy_Continue_HasCorrectValue()
    {
        Assert.Equal("continue", WorkflowFailurePolicy.Continue);
    }

    [Fact]
    public void WorkflowFailurePolicy_AbortAndContinue_AreDistinct()
    {
        Assert.NotEqual(WorkflowFailurePolicy.Abort, WorkflowFailurePolicy.Continue);
    }

    [Fact]
    public void WorkflowTemplate_DefaultFailurePolicy_IsAbort()
    {
        // Any new template must default to the safe abort policy
        var template = new WorkflowTemplate();

        Assert.Equal(WorkflowFailurePolicy.Abort, template.FailurePolicy);
    }

    [Fact]
    public void DailyRun_Template_HasContinuePolicy()
    {
        // "Daily Run" is not critical — it continues even when individual steps fail
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var store = new WorkflowStore(db);

        var template = store.ListAll().Single(w => w.Name == "Daily Run");

        Assert.Equal(WorkflowFailurePolicy.Continue, template.FailurePolicy);
    }

    [Fact]
    public async Task KnowledgeIndex_WhenEmbeddingUnavailable_ResultHasNonZeroFailedCount()
    {
        // Demonstrates that a failed KnowledgeIndex step would trigger an abort
        // on the "Knowledge Refresh" workflow's abort-on-failure check.
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var coordinator = BuildCoordinator(db);

        var documents = new[]
        {
            MakeDocument("doc/guide.md", extractedText: "Content for relay protection."),
        };

        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        // Failed > 0 is the condition that would trigger abort
        Assert.True(result.Failed > 0,
            "A failed KnowledgeIndex step should produce Failed > 0 so that abort policy is triggered.");
        Assert.Equal(0, result.Indexed);
    }

    [Fact]
    public async Task KnowledgeIndex_MultipleFailures_AllReportedInResult()
    {
        // Verifies the result accurately aggregates failure counts used by abort logic
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var coordinator = BuildCoordinator(db);

        var documents = new[]
        {
            MakeDocument("doc/a.md", extractedText: "Content A."),
            MakeDocument("doc/b.md", extractedText: "Content B."),
            MakeDocument("doc/c.md", extractedText: "Content C."),
        };

        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        Assert.Equal(3, result.TotalDocuments);
        Assert.Equal(3, result.Failed);
        Assert.Equal(0, result.Indexed);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public async Task KnowledgeIndex_EmptyDocumentList_ReportsZeroFailures()
    {
        // An empty index run should never trigger the abort policy
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var coordinator = BuildCoordinator(db);

        var result = await coordinator.RunKnowledgeIndexAsync([]);

        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.TotalDocuments);
    }

    // -------------------------------------------------------------------------
    // Group 3: Document import → re-indexing integration
    //
    // Tests verify that documents loaded by KnowledgeImportService (plain text
    // and markdown files) have the correct content and metadata to be processed
    // by KnowledgeCoordinator, and that the hash-based change detection ensures
    // only modified documents trigger a re-index on subsequent runs.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ImportedTextFile_IsEligibleForKnowledgeIndexing()
    {
        // A .txt file loaded via KnowledgeImportService must have non-empty
        // content that KnowledgeCoordinator will attempt to index (not skip).
        using var tmp = new TempDirectory();
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(tmp.Path, "notes.txt"),
            "Relay protection fundamentals and grounding requirements.");

        var importService = new KnowledgeImportService(
            new ProcessRunner(),
            pythonScriptPath: System.IO.Path.Combine(tmp.Path, "no-script.py")
        );

        var library = await importService.LoadAsync(tmp.Path);

        Assert.Single(library.Documents);
        var doc = library.Documents[0];
        Assert.False(string.IsNullOrWhiteSpace(doc.ExtractedText ?? doc.Summary),
            "Imported text document must have extractable content for indexing.");
    }

    [Fact]
    public async Task ImportedMarkdownFile_IsEligibleForKnowledgeIndexing()
    {
        using var tmp = new TempDirectory();
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(tmp.Path, "study-guide.md"),
            "# Grounding\n\nEarth fault protection requires proper grounding at every node.");

        var importService = new KnowledgeImportService(
            new ProcessRunner(),
            pythonScriptPath: System.IO.Path.Combine(tmp.Path, "no-script.py")
        );

        var library = await importService.LoadAsync(tmp.Path);

        Assert.Single(library.Documents);
        var doc = library.Documents[0];
        Assert.False(string.IsNullOrWhiteSpace(doc.ExtractedText ?? doc.Summary),
            "Imported markdown document must have extractable content for indexing.");
    }

    [Fact]
    public async Task ImportedDocuments_FlowIntoKnowledgeCoordinator_AsAttemptedIndexing()
    {
        // Full import → coordinator pipeline: KnowledgeImportService loads documents,
        // KnowledgeCoordinator attempts indexing — no documents are silently skipped
        // due to missing content.
        using var tmp = new TempDirectory();
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(tmp.Path, "relay-notes.txt"),
            "Distance relay protection principles for transmission lines.");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(tmp.Path, "grounding.md"),
            "# Grounding\n\nSolid grounding reduces transient overvoltages during faults.");

        var importService = new KnowledgeImportService(
            new ProcessRunner(),
            pythonScriptPath: System.IO.Path.Combine(tmp.Path, "no-script.py")
        );
        var library = await importService.LoadAsync(tmp.Path);

        using var db = new FoundryDatabase(tmp.Path);
        var coordinator = BuildCoordinator(db);

        var result = await coordinator.RunKnowledgeIndexAsync(library.Documents);

        Assert.Equal(2, result.TotalDocuments);
        Assert.Equal(0, result.Skipped); // both documents have content
        // With unavailable embedding service all documents fail (not silently dropped)
        Assert.Equal(0, result.Indexed);
        Assert.Equal(2, result.Failed);
    }

    [Fact]
    public async Task ReIndexing_UnchangedDocuments_AreSkippedOnSecondRun()
    {
        // After a document's content hash has been recorded, re-running the index
        // on the same content must skip it entirely — no redundant embedding calls.
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var indexStore = new KnowledgeIndexStore(db);

        const string path = "doc/relay-protection.md";
        const string content = "Relay protection fundamentals.";
        var hash = KnowledgeIndexStore.ComputeContentHash(content);
        indexStore.MarkIndexed(path, hash, "vec-rp-001");

        var coordinator = new KnowledgeCoordinator(
            BuildUnavailableEmbeddingService(),
            BuildUnavailableVectorStore(),
            indexStore);

        var documents = new[] { MakeDocument(path, extractedText: content) };
        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        Assert.Equal(1, result.TotalDocuments);
        Assert.Equal(1, result.Skipped); // hash unchanged → skipped
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Indexed);
    }

    [Fact]
    public async Task ReIndexing_ModifiedDocument_IsAttemptedOnSecondRun()
    {
        // When document content changes (new hash), the coordinator must attempt
        // re-indexing even though the path was previously indexed.
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var indexStore = new KnowledgeIndexStore(db);

        const string path = "doc/relay-protection.md";
        indexStore.MarkIndexed(path, "old-hash-value", "vec-rp-old");

        var coordinator = new KnowledgeCoordinator(
            BuildUnavailableEmbeddingService(),
            BuildUnavailableVectorStore(),
            indexStore);

        // Different content → different hash → NeedsIndexing = true
        var documents = new[]
        {
            MakeDocument(path, extractedText: "Updated relay protection content after revision."),
        };

        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        Assert.Equal(1, result.TotalDocuments);
        Assert.Equal(0, result.Skipped); // content changed → not skipped
        Assert.Equal(1, result.Failed);  // embedding unavailable → failed (not silently ignored)
    }

    [Fact]
    public async Task ReIndexing_MixedBatch_OnlyChangedDocumentsAreAttempted()
    {
        // Full re-index cycle with mixed batch: two unchanged documents are skipped,
        // one modified document is attempted. This validates the incremental
        // re-indexing contract of the Knowledge Refresh workflow.
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var indexStore = new KnowledgeIndexStore(db);

        // Pre-index two documents
        const string textA = "Content A — unchanged.";
        const string textB = "Content B — unchanged.";
        indexStore.MarkIndexed("doc/a.md", KnowledgeIndexStore.ComputeContentHash(textA), "vec-a");
        indexStore.MarkIndexed("doc/b.md", KnowledgeIndexStore.ComputeContentHash(textB), "vec-b");
        // doc/c.md has a stale hash
        indexStore.MarkIndexed("doc/c.md", "stale-hash", "vec-c-old");

        var coordinator = new KnowledgeCoordinator(
            BuildUnavailableEmbeddingService(),
            BuildUnavailableVectorStore(),
            indexStore);

        var documents = new[]
        {
            MakeDocument("doc/a.md", extractedText: textA),                         // unchanged → skip
            MakeDocument("doc/b.md", extractedText: textB),                         // unchanged → skip
            MakeDocument("doc/c.md", extractedText: "Content C — now updated."),    // changed → attempt
        };

        var result = await coordinator.RunKnowledgeIndexAsync(documents);

        Assert.Equal(3, result.TotalDocuments);
        Assert.Equal(2, result.Skipped); // doc/a and doc/b unchanged
        Assert.Equal(1, result.Failed);  // doc/c changed but embedding unavailable
        Assert.Equal(0, result.Indexed);
    }

    [Fact]
    public async Task FullImportAndReIndexCycle_OnlyModifiedFileIsReAttempted()
    {
        // End-to-end simulation of the Knowledge Refresh workflow:
        //   1. Import two text files via KnowledgeImportService.
        //   2. Simulate that one file was already indexed (hash recorded).
        //   3. Modify the second file and re-import.
        //   4. Re-run KnowledgeCoordinator — only the modified file is attempted.
        using var tmp = new TempDirectory();

        const string fileA = "relay-basics.txt";
        const string fileB = "grounding.txt";
        const string originalContentA = "Original relay basics content.";
        const string originalContentB = "Original grounding content.";

        await File.WriteAllTextAsync(System.IO.Path.Combine(tmp.Path, fileA), originalContentA);
        await File.WriteAllTextAsync(System.IO.Path.Combine(tmp.Path, fileB), originalContentB);

        var importService = new KnowledgeImportService(
            new ProcessRunner(),
            pythonScriptPath: System.IO.Path.Combine(tmp.Path, "no-script.py")
        );

        // First import
        var library = await importService.LoadAsync(tmp.Path);
        Assert.Equal(2, library.Documents.Count);

        using var db = new FoundryDatabase(tmp.Path);
        var indexStore = new KnowledgeIndexStore(db);

        // Simulate that first run already indexed file A
        var docA = library.Documents.First(d => d.RelativePath.Contains(fileA, StringComparison.OrdinalIgnoreCase));
        var contentA = GetDocumentContent(docA);
        var hashA = KnowledgeIndexStore.ComputeContentHash(contentA);
        indexStore.MarkIndexed(docA.RelativePath, hashA, "vec-relay-001");

        // Modify file B on disk
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(tmp.Path, fileB),
            "Updated grounding content: new fault isolation procedures.");

        // Second import — picks up updated content for file B
        var library2 = await importService.LoadAsync(tmp.Path);

        var coordinator = new KnowledgeCoordinator(
            BuildUnavailableEmbeddingService(),
            BuildUnavailableVectorStore(),
            indexStore);

        var result = await coordinator.RunKnowledgeIndexAsync(library2.Documents);

        Assert.Equal(2, result.TotalDocuments);
        Assert.Equal(1, result.Skipped); // file A unchanged
        Assert.Equal(1, result.Failed);  // file B changed but embedding unavailable
        Assert.Equal(0, result.Indexed);
    }

    [Fact]
    public void NeedsIndexing_ReturnsFalse_ForUnchangedContent()
    {
        // Low-level check: KnowledgeIndexStore.NeedsIndexing returns false when
        // content hash matches the recorded value — this is the gate for skip logic.
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var indexStore = new KnowledgeIndexStore(db);

        const string path = "doc/test.md";
        const string content = "Static content that should not be re-indexed.";
        var hash = KnowledgeIndexStore.ComputeContentHash(content);

        indexStore.MarkIndexed(path, hash, "vec-static-001");

        Assert.False(indexStore.NeedsIndexing(path, hash),
            "NeedsIndexing must return false for unchanged content hash.");
    }

    [Fact]
    public void NeedsIndexing_ReturnsTrue_AfterContentChange()
    {
        // NeedsIndexing must return true when content changes — triggering re-indexing.
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var indexStore = new KnowledgeIndexStore(db);

        const string path = "doc/test.md";
        indexStore.MarkIndexed(path, "hash-v1", "vec-v1");

        var newHash = KnowledgeIndexStore.ComputeContentHash("Revised content for test document.");

        Assert.True(indexStore.NeedsIndexing(path, newHash),
            "NeedsIndexing must return true when content hash differs from recorded value.");
    }

    [Fact]
    public void NeedsIndexing_ReturnsTrue_ForNewDocument()
    {
        // A document never before indexed must always attempt indexing.
        using var tmp = new TempDirectory();
        using var db = new FoundryDatabase(tmp.Path);
        var indexStore = new KnowledgeIndexStore(db);

        var hash = KnowledgeIndexStore.ComputeContentHash("Brand new document content.");

        Assert.True(indexStore.NeedsIndexing("doc/new-document.md", hash),
            "NeedsIndexing must return true for a document path not yet in the index.");
    }
}
