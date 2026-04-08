using DailyDesk.Models;

namespace DailyDesk.Services;

/// <summary>
/// Manages workflow template persistence via LiteDB.
/// Provides CRUD operations and workflow execution support.
/// </summary>
public sealed class WorkflowStore
{
    private readonly OfficeDatabase _db;

    public WorkflowStore(OfficeDatabase db)
    {
        _db = db;
        SeedBuiltInTemplates();
    }

    /// <summary>
    /// Creates a new workflow template.
    /// </summary>
    public WorkflowTemplate Create(WorkflowTemplate template)
    {
        template.Id = Guid.NewGuid().ToString();
        template.CreatedAt = DateTimeOffset.Now;
        _db.WorkflowTemplates.Insert(template);
        return template;
    }

    /// <summary>
    /// Retrieves a workflow template by ID.
    /// </summary>
    public WorkflowTemplate? GetById(string id)
    {
        return _db.WorkflowTemplates.FindOne(w => w.Id == id);
    }

    /// <summary>
    /// Lists all workflow templates, most recently created first.
    /// </summary>
    public IReadOnlyList<WorkflowTemplate> ListAll()
    {
        return _db.WorkflowTemplates.Query()
            .OrderByDescending(w => w.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Deletes a workflow template by ID. Returns true if found and deleted.
    /// Built-in templates cannot be deleted.
    /// </summary>
    public bool Delete(string id)
    {
        var template = _db.WorkflowTemplates.FindOne(w => w.Id == id);
        if (template is null || template.BuiltIn) return false;
        return _db.WorkflowTemplates.Delete(template.Id);
    }

    /// <summary>
    /// Seeds the built-in workflow templates if they don't already exist.
    /// </summary>
    private void SeedBuiltInTemplates()
    {
        var existing = _db.WorkflowTemplates.Query()
            .Where(w => w.BuiltIn)
            .ToList();

        if (existing.Count > 0) return;

        var builtIns = new[]
        {
            new WorkflowTemplate
            {
                Name = "Daily Run",
                Description = "Full daily workflow: ML pipeline, export artifacts, generate operator suggestions.",
                BuiltIn = true,
                FailurePolicy = WorkflowFailurePolicy.Continue,
                Steps =
                [
                    new WorkflowStep { JobType = OfficeJobType.MLPipeline, Label = "Run ML Pipeline" },
                    new WorkflowStep { JobType = OfficeJobType.MLExportArtifacts, Label = "Export Suite Artifacts" },
                    new WorkflowStep { JobType = OfficeJobType.KnowledgeIndex, Label = "Index Knowledge Documents" },
                ],
            },
            new WorkflowTemplate
            {
                Name = "Exam Prep",
                Description = "Focused workflow for exam preparation: analytics and knowledge indexing.",
                BuiltIn = true,
                FailurePolicy = WorkflowFailurePolicy.Continue,
                Steps =
                [
                    new WorkflowStep { JobType = OfficeJobType.MLAnalytics, Label = "Run Learning Analytics" },
                    new WorkflowStep { JobType = OfficeJobType.MLEmbeddings, Label = "Generate Document Embeddings" },
                    new WorkflowStep { JobType = OfficeJobType.KnowledgeIndex, Label = "Index Knowledge Documents" },
                ],
            },
            new WorkflowTemplate
            {
                Name = "Knowledge Refresh",
                Description = "Re-index all knowledge documents and update embeddings.",
                BuiltIn = true,
                FailurePolicy = WorkflowFailurePolicy.Abort,
                Steps =
                [
                    new WorkflowStep { JobType = OfficeJobType.KnowledgeIndex, Label = "Index Knowledge Documents" },
                    new WorkflowStep { JobType = OfficeJobType.MLEmbeddings, Label = "Refresh Document Embeddings" },
                ],
            },
        };

        foreach (var template in builtIns)
        {
            template.Id = Guid.NewGuid().ToString();
            template.CreatedAt = DateTimeOffset.Now;
            _db.WorkflowTemplates.Insert(template);
        }
    }
}
