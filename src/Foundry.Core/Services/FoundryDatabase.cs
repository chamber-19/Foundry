using LiteDB;
using Foundry.Models;

namespace Foundry.Services;

/// <summary>
/// Manages a single LiteDB database instance for Foundry broker persistence.
/// </summary>
public sealed class FoundryDatabase : IDisposable
{
    private readonly LiteDatabase _db;
    private bool _disposed;

    /// <summary>
    /// Pre-warms BsonMapper.Global for every entity type used by this database.
    /// LiteDB's internal entity-mapper cache is lazily populated and has a race
    /// condition when multiple threads request the same type simultaneously for
    /// the first time.  The CLR guarantees this static constructor runs exactly
    /// once before any instance is constructed, so all mappers are fully built
    /// before any concurrent code can call EnsureIndex or GetCollection.
    /// </summary>
    static FoundryDatabase()
    {
        BsonMapper.Global.Entity<DailyRunTemplate>();
        BsonMapper.Global.Entity<FoundryJob>();
        BsonMapper.Global.Entity<JobSchedule>();
        BsonMapper.Global.Entity<WorkflowTemplate>();
        BsonMapper.Global.Entity<IndexedDocumentRecord>();
        BsonMapper.Global.Entity<FoundryNotification>();
    }

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

    public ILiteCollection<DailyRunTemplate> DailyRuns =>
        _db.GetCollection<DailyRunTemplate>("operator_daily_runs");

    public ILiteCollection<FoundryJob> Jobs =>
        _db.GetCollection<FoundryJob>("jobs");

    public ILiteCollection<JobSchedule> JobSchedules =>
        _db.GetCollection<JobSchedule>("job_schedules");

    public ILiteCollection<WorkflowTemplate> WorkflowTemplates =>
        _db.GetCollection<WorkflowTemplate>("workflow_templates");

    public ILiteCollection<IndexedDocumentRecord> KnowledgeIndex =>
        _db.GetCollection<IndexedDocumentRecord>("knowledge_index");

    public ILiteCollection<FoundryNotification> Notifications =>
        _db.GetCollection<FoundryNotification>("notifications");

    private void EnsureIndexes()
    {
        DailyRuns.EnsureIndex(x => x.DateKey);
        Jobs.EnsureIndex(x => x.Status);
        Jobs.EnsureIndex(x => x.CreatedAt);
        JobSchedules.EnsureIndex(x => x.Enabled);
        KnowledgeIndex.EnsureIndex(x => x.DocumentPath);
        Notifications.EnsureIndex(x => x.DedupeKey, unique: true);
        Notifications.EnsureIndex(x => x.Repository);
        Notifications.EnsureIndex(x => x.DeliveredAt);
        Notifications.EnsureIndex(x => x.CreatedAt);
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
