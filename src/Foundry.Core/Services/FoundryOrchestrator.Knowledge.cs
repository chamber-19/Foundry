using System.IO;
using Foundry.Models;

namespace Foundry.Services;

// Knowledge indexing and library import operations.
public sealed partial class FoundryOrchestrator
{
    /// <summary>
    /// Runs the knowledge index pipeline over all documents in the library.
    /// </summary>
    public async Task<KnowledgeIndexResult> RunKnowledgeIndexAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LearningDocument> documents;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            documents = _learningLibrary.Documents;
        }
        finally
        {
            _gate.Release();
        }

        return await _knowledgeCoordinator.RunKnowledgeIndexAsync(documents, cancellationToken);
    }

    /// <summary>
    /// Returns the current status of the knowledge index (document and
    /// vector-store counts).
    /// </summary>
    public async Task<KnowledgeIndexStatus> GetKnowledgeIndexStatusAsync(CancellationToken cancellationToken = default)
    {
        int totalDocuments;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            totalDocuments = _learningLibrary.Documents.Count;
        }
        finally
        {
            _gate.Release();
        }

        return await _knowledgeCoordinator.GetKnowledgeIndexStatusAsync(totalDocuments, cancellationToken);
    }

    /// <summary>
    /// Imports one or more files or directories into the knowledge library.
    /// Paths that do not exist or yield no documents are recorded as skipped.
    /// </summary>
    public async Task<FoundryLibraryImportResult> ImportLibraryFilesAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        var importedPaths = new List<string>();
        var skippedPaths = new List<string>();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    skippedPaths.Add(path);
                    continue;
                }

                try
                {
                    var library = await _knowledgeImportService.LoadAsync(path, cancellationToken: cancellationToken);
                    if (library.Documents.Count > 0)
                        importedPaths.Add(path);
                    else
                        skippedPaths.Add(path);
                }
                catch
                {
                    skippedPaths.Add(path);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return new FoundryLibraryImportResult
        {
            ImportedCount = importedPaths.Count,
            ImportedPaths = importedPaths,
            SkippedPaths = skippedPaths,
        };
    }
}

/// <summary>
/// Result of a knowledge indexing run.
/// </summary>
public sealed class KnowledgeIndexResult
{
    public int TotalDocuments { get; set; }
    public int Indexed { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
}

/// <summary>
/// Status snapshot of the knowledge index.
/// </summary>
public sealed class KnowledgeIndexStatus
{
    public int TotalDocuments { get; set; }
    public int IndexedDocuments { get; set; }
    public ulong VectorStorePoints { get; set; }
    public string VectorStoreStatus { get; set; } = string.Empty;
}
