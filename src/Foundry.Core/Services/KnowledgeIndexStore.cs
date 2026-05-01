using System.Security.Cryptography;
using System.Text;
using LiteDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Services;

/// <summary>
/// Tracks which knowledge documents have been indexed (embedded + stored in vector DB).
/// Uses LiteDB to persist indexed document hashes so re-indexing skips unchanged documents.
/// </summary>
public sealed class KnowledgeIndexStore
{
    private const string CollectionName = "knowledge_index";

    private readonly ILiteCollection<IndexedDocumentRecord> _collection;
    private readonly ILogger<KnowledgeIndexStore> _logger;

    public KnowledgeIndexStore(FoundryDatabase database, ILogger<KnowledgeIndexStore>? logger = null)
    {
        _collection = database.KnowledgeIndex;
        _logger = logger ?? NullLogger<KnowledgeIndexStore>.Instance;
    }

    /// <summary>
    /// Checks whether a document needs re-indexing by comparing its content hash.
    /// Returns true if the document is new or has changed since last indexing.
    /// </summary>
    public bool NeedsIndexing(string documentPath, string contentHash)
    {
        var record = _collection.FindOne(x => x.DocumentPath == documentPath);
        return record is null || record.ContentHash != contentHash;
    }

    /// <summary>
    /// Marks a document as indexed with the given content hash and vector ID.
    /// </summary>
    public void MarkIndexed(string documentPath, string contentHash, string vectorId)
    {
        var existing = _collection.FindOne(x => x.DocumentPath == documentPath);
        if (existing is not null)
        {
            existing.ContentHash = contentHash;
            existing.VectorId = vectorId;
            existing.IndexedAt = DateTimeOffset.Now;
            _collection.Update(existing);
        }
        else
        {
            _collection.Insert(new IndexedDocumentRecord
            {
                DocumentPath = documentPath,
                ContentHash = contentHash,
                VectorId = vectorId,
                IndexedAt = DateTimeOffset.Now,
            });
        }
    }

    /// <summary>
    /// Removes a document from the index tracking.
    /// </summary>
    public bool RemoveDocument(string documentPath)
    {
        var record = _collection.FindOne(x => x.DocumentPath == documentPath);
        if (record is null)
        {
            return false;
        }

        return _collection.Delete(record.Id);
    }

    /// <summary>
    /// Gets the vector ID for an indexed document, or null if not indexed.
    /// </summary>
    public string? GetVectorId(string documentPath)
    {
        var record = _collection.FindOne(x => x.DocumentPath == documentPath);
        return record?.VectorId;
    }

    /// <summary>
    /// Returns the total number of indexed documents.
    /// </summary>
    public int GetIndexedCount() => _collection.Count();

    /// <summary>
    /// Returns all indexed document records.
    /// </summary>
    public IReadOnlyList<IndexedDocumentRecord> GetAllIndexed() =>
        _collection.FindAll().ToList();

    /// <summary>
    /// Computes a SHA-256 hash of the given content for change detection.
    /// </summary>
    public static string ComputeContentHash(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hashBytes);
    }
}

/// <summary>
/// Record of an indexed knowledge document in LiteDB.
/// </summary>
public sealed class IndexedDocumentRecord
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public string DocumentPath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string VectorId { get; set; } = string.Empty;
    public DateTimeOffset IndexedAt { get; set; }
}
