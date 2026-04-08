using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DailyDesk.Models;

public sealed class DailySettings
{
    public string SuiteRepoPath { get; init; } = GetDefaultSuiteRepoPath();
    public string SuiteRuntimeStatusEndpoint { get; init; } =
        "http://127.0.0.1:5000/api/runtime/status";
    public string OllamaEndpoint { get; init; } = "http://127.0.0.1:11434";
    public string PrimaryModelProvider { get; init; } = "ollama";
    public bool EnableHuggingFaceCatalog { get; init; }
    public string HuggingFaceTokenEnvVar { get; init; } = "HF_TOKEN";
    public string HuggingFaceMcpUrl { get; init; } = "https://huggingface.co/mcp";
    public string KnowledgeLibraryPath { get; init; } = string.Empty;
    public string StateRootPath { get; init; } = string.Empty;
    public IReadOnlyList<string> AdditionalKnowledgePaths { get; init; } = Array.Empty<string>();
    public string OfficeName { get; init; } = "Office";
    public string SuiteFocus { get; init; } =
        "Read-only Suite awareness, unified doctor/runtime trust, and developer workshop signals.";
    public string EngineeringFocus { get; init; } =
        "Electrical engineering growth, standards, grounding, protection, review-first technical judgment, and operator-safe reasoning.";
    public string CadFocus { get; init; } =
        "AutoCAD drafting QA, markup-first review, production drawing reliability, and CAD automation that stays human-reviewed.";
    public string BusinessFocus { get; init; } =
        "Internal operating discipline, monetization hypotheses, pilot framing, pricing realism, and measurable operator value.";
    public string CareerFocus { get; init; } =
        "Turn Suite work, EE learning, and CAD workflow judgment into strong career proof and future business leverage.";
    public string ChiefModel { get; init; } = "qwen3:8b";
    public string MentorModel { get; init; } = "qwen3:8b";
    public string RepoModel { get; init; } = "qwen3:8b";
    public string TrainingModel { get; init; } = "qwen3:8b";
    public string BusinessModel { get; init; } = "qwen3:8b";
    public string MLModel { get; init; } = "qwen3:8b";
    public bool EnableMLPipeline { get; init; }
    public string MLArtifactExportPath { get; init; } = string.Empty;
    public int JobRetentionDays { get; init; } = 30;

    public string ResolveMLArtifactExportPath(string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(MLArtifactExportPath))
        {
            return Path.GetFullPath(MLArtifactExportPath);
        }

        return Path.Combine(ResolveStateRootPath(baseDirectory), "ml-artifacts");
    }

    public string ResolveKnowledgeLibraryPath(string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(KnowledgeLibraryPath))
        {
            return Path.GetFullPath(KnowledgeLibraryPath);
        }

        return GetDefaultKnowledgeLibraryPath();
    }

    public string ResolveStateRootPath(string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(StateRootPath))
        {
            return Path.GetFullPath(StateRootPath);
        }

        return GetDefaultStateRootPath();
    }

    public IReadOnlyList<string> ResolveAdditionalKnowledgePaths()
    {
        return AdditionalKnowledgePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static DailySettings Load(string baseDirectory)
    {
        var settingsPath = Path.Combine(baseDirectory, "dailydesk.settings.json");
        var localSettingsPath = Path.Combine(baseDirectory, "dailydesk.settings.local.json");
        if (!File.Exists(settingsPath) && !File.Exists(localSettingsPath))
        {
            return new DailySettings();
        }

        try
        {
            var rootNode = new JsonObject();
            MergeSettingsFile(rootNode, settingsPath);
            MergeSettingsFile(rootNode, localSettingsPath);
            return rootNode.Deserialize<DailySettings>(
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                   )
                   ?? new DailySettings();
        }
        catch
        {
            return new DailySettings();
        }
    }

    private static string GetDefaultSuiteRepoPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile, "Documents", "GitHub", "Suite");
        }

        return Path.Combine("C:\\Users\\Public", "Documents", "GitHub", "Suite");
    }

    private static string GetDefaultKnowledgeLibraryPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile, "Dropbox", "SuiteWorkspace", "Office", "Knowledge");
        }

        return Path.Combine(
            "C:\\Users\\Public",
            "Dropbox",
            "SuiteWorkspace",
            "Office",
            "Knowledge"
        );
    }

    private static string GetDefaultStateRootPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile, "Dropbox", "SuiteWorkspace", "Office", "State");
        }

        return Path.Combine("C:\\Users\\Public", "Dropbox", "SuiteWorkspace", "Office", "State");
    }

    private static void MergeSettingsFile(JsonObject target, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var payload = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        var parsed = JsonNode.Parse(payload) as JsonObject;
        if (parsed is null)
        {
            return;
        }

        foreach (var property in parsed)
        {
            target[property.Key] = property.Value?.DeepClone();
        }
    }
}
