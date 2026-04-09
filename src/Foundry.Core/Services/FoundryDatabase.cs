using LiteDB;
using Foundry.Models;

namespace Foundry.Services;

/// <summary>
/// Manages a single LiteDB database instance for all Foundry ML pipeline persistence.
/// </summary>
public sealed class FoundryDatabase : IDisposable
{
    private readonly LiteDatabase _db;
    private bool _disposed;

    public FoundryDatabase(string stateRootPath)
    {
        var root = string.IsNullOrWhiteSpace(stateRootPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Foundry"
            )
            : Path.GetFullPath(stateRootPath);
        Directory.CreateDirectory(root);

        var dbPath = Path.Combine(root, "foundry.db");
        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");

        EnsureIndexes();
    }

    public ILiteCollection<TrainingAttemptRecord> PracticeAttempts =>
        _db.GetCollection<TrainingAttemptRecord>("training_practice_attempts");

    public ILiteCollection<DailyRunTemplate> DailyRuns =>
        _db.GetCollection<DailyRunTemplate>("operator_daily_runs");

    public ILiteCollection<FoundryJob> Jobs =>
        _db.GetCollection<FoundryJob>("jobs");

    public ILiteCollection<JobSchedule> JobSchedules =>
        _db.GetCollection<JobSchedule>("job_schedules");

    public ILiteCollection<WorkflowTemplate> WorkflowTemplates =>
        _db.GetCollection<WorkflowTemplate>("workflow_templates");

    public ILiteCollection<PersistedMLResult> MLAnalyticsResults =>
        _db.GetCollection<PersistedMLResult>("ml_analytics");

    public ILiteCollection<PersistedMLResult> MLForecastResults =>
        _db.GetCollection<PersistedMLResult>("ml_forecast");

    public ILiteCollection<PersistedMLResult> MLEmbeddingsResults =>
        _db.GetCollection<PersistedMLResult>("ml_embeddings");

    public ILiteCollection<IndexedDocumentRecord> KnowledgeIndex =>
        _db.GetCollection<IndexedDocumentRecord>("knowledge_index");

    private void EnsureIndexes()
    {
        PracticeAttempts.EnsureIndex(x => x.CompletedAt);
        DailyRuns.EnsureIndex(x => x.DateKey);
        Jobs.EnsureIndex(x => x.Id);
        Jobs.EnsureIndex(x => x.Status);
        Jobs.EnsureIndex(x => x.CreatedAt);
        JobSchedules.EnsureIndex(x => x.Id);
        JobSchedules.EnsureIndex(x => x.Enabled);
        WorkflowTemplates.EnsureIndex(x => x.Id);
        KnowledgeIndex.EnsureIndex(x => x.DocumentPath);
    }

    /// <summary>
    /// Checks whether the database has been migrated from JSON files.
    /// Uses a metadata collection to track migration status.
    /// </summary>
    public bool HasMigrated(string storeName)
    {
        var meta = _db.GetCollection("_migration_meta");
        return meta.Exists(Query.EQ("_id", storeName));
    }

    /// <summary>
    /// Marks a store as migrated from JSON.
    /// </summary>
    public void MarkMigrated(string storeName)
    {
        var meta = _db.GetCollection("_migration_meta");
        meta.Upsert(new BsonDocument
        {
            ["_id"] = storeName,
            ["migratedAt"] = DateTime.UtcNow,
        });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _db.Dispose();
        }
    }
}
