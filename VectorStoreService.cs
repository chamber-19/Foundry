using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DailyDesk.Services;

/// <summary>
/// Wraps the Qdrant vector database client for storing and searching document embeddings.
/// Falls back gracefully (returns empty results) when Qdrant is unreachable.
/// </summary>
public sealed class VectorStoreService
{
    public const string DefaultCollectionName = "office-knowledge";
    private const ulong DefaultVectorSize = 768; // nomic-embed-text default dimension

    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private readonly ulong _vectorSize;
    private readonly ILogger<VectorStoreService> _logger;
    private bool _collectionEnsured;

    public VectorStoreService(
        string host = "localhost",
        int port = 6334,
        string? collectionName = null,
        ulong vectorSize = DefaultVectorSize,
        ILogger<VectorStoreService>? logger = null)
    {
        _client = new QdrantClient(host, port);
        _collectionName = collectionName ?? DefaultCollectionName;
        _vectorSize = vectorSize;
        _logger = logger ?? NullLogger<VectorStoreService>.Instance;
    }

    /// <summary>
    /// Constructor for testing or advanced configuration with a pre-built client.
    /// </summary>
    public VectorStoreService(
        QdrantClient client,
        string? collectionName = null,
        ulong vectorSize = DefaultVectorSize,
        ILogger<VectorStoreService>? logger = null)
    {
        _client = client;
        _collectionName = collectionName ?? DefaultCollectionName;
        _vectorSize = vectorSize;
        _logger = logger ?? NullLogger<VectorStoreService>.Instance;
    }

    /// <summary>
    /// Upserts a document embedding into Qdrant with metadata.
    /// Returns true on success, false if Qdrant is unreachable.
    /// </summary>
    public async Task<bool> UpsertAsync(
        string docId,
        float[] vector,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureCollectionAsync(cancellationToken);

            var point = new PointStruct
            {
                Id = new PointId { Uuid = docId },
                Vectors = vector,
            };

            if (metadata is not null)
            {
                foreach (var (key, value) in metadata)
                {
                    point.Payload[key] = value;
                }
            }

            await _client.UpsertAsync(_collectionName, [point], cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qdrant upsert failed for document {DocId}.", docId);
            return false;
        }
    }

    /// <summary>
    /// Searches for the topK most similar documents to the query vector.
    /// Returns empty list if Qdrant is unreachable.
    /// </summary>
    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] queryVector,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureCollectionAsync(cancellationToken);

            var results = await _client.SearchAsync(
                _collectionName,
                queryVector,
                limit: (ulong)topK,
                cancellationToken: cancellationToken);

            return results
                .Select(point => new VectorSearchResult
                {
                    DocumentId = point.Id?.HasUuid == true ? point.Id.Uuid : point.Id?.Num.ToString() ?? string.Empty,
                    Score = point.Score,
                    Metadata = point.Payload
                        ?.ToDictionary(
                            kvp => kvp.Key,
                            kvp => kvp.Value?.StringValue ?? string.Empty)
                        ?? new Dictionary<string, string>(),
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qdrant search failed, returning empty results.");
            return Array.Empty<VectorSearchResult>();
        }
    }

    /// <summary>
    /// Deletes a document from the vector store by its ID.
    /// Returns true on success, false if Qdrant is unreachable.
    /// </summary>
    public async Task<bool> DeleteAsync(
        string docId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureCollectionAsync(cancellationToken);
            var pointId = new PointId { Uuid = docId };
            await _client.DeleteAsync(_collectionName, pointId, cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qdrant delete failed for document {DocId}.", docId);
            return false;
        }
    }

    /// <summary>
    /// Gets basic information about the vector collection.
    /// Returns null if Qdrant is unreachable.
    /// </summary>
    public async Task<VectorCollectionInfo?> GetCollectionInfoAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await _client.GetCollectionInfoAsync(_collectionName, cancellationToken);
            return new VectorCollectionInfo
            {
                Name = _collectionName,
                PointsCount = info.PointsCount,
                VectorSize = _vectorSize,
                Status = info.Status.ToString(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qdrant collection info retrieval failed.");
            return null;
        }
    }

    private async Task EnsureCollectionAsync(CancellationToken cancellationToken)
    {
        if (_collectionEnsured)
        {
            return;
        }

        try
        {
            var collections = await _client.ListCollectionsAsync(cancellationToken);
            if (!collections.Any(c => c == _collectionName))
            {
                await _client.CreateCollectionAsync(
                    _collectionName,
                    new VectorParams { Size = _vectorSize, Distance = Distance.Cosine },
                    cancellationToken: cancellationToken);
                _logger.LogInformation("Created Qdrant collection {Collection} with vector size {Size}.", _collectionName, _vectorSize);
            }

            _collectionEnsured = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure Qdrant collection {Collection}.", _collectionName);
            throw;
        }
    }
}

/// <summary>
/// Represents a search result from the vector store.
/// </summary>
public sealed class VectorSearchResult
{
    public string DocumentId { get; set; } = string.Empty;
    public float Score { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Basic metadata about a vector collection.
/// </summary>
public sealed class VectorCollectionInfo
{
    public string Name { get; set; } = string.Empty;
    public ulong PointsCount { get; set; }
    public ulong VectorSize { get; set; }
    public string Status { get; set; } = string.Empty;
}
