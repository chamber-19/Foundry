using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foundry.Models;

public sealed class FoundrySettings
{
    public string SuiteRepoPath { get; init; } = GetDefaultSuiteRepoPath();
    public string SuiteRuntimeStatusEndpoint { get; init; } =
        "http://127.0.0.1:5000/api/runtime/status";
    public string OllamaEndpoint { get; init; } = "http://127.0.0.1:11434";
    public string MLModel { get; init; } = "qwen3:8b";
    public bool EnableMLPipeline { get; init; }
    public string MLArtifactExportPath { get; init; } = string.Empty;
    public int JobRetentionDays { get; init; } = 30;
    public string KnowledgeLibraryPath { get; init; } = string.Empty;
    public string StateRootPath { get; init; } = string.Empty;
    public IReadOnlyList<string> AdditionalKnowledgePaths { get; init; } = Array.Empty<string>();
    public string? DiscordBotToken { get; init; }

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

    public static FoundrySettings Load(string baseDirectory)
    {
        var settingsPath = Path.Combine(baseDirectory, "foundry.settings.json");
        var localSettingsPath = Path.Combine(baseDirectory, "foundry.settings.local.json");
        if (!File.Exists(settingsPath) && !File.Exists(localSettingsPath))
        {
            return new FoundrySettings();
        }

        try
        {
            var rootNode = new JsonObject();
            MergeSettingsFile(rootNode, settingsPath);
            MergeSettingsFile(rootNode, localSettingsPath);
            return rootNode.Deserialize<FoundrySettings>(
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                   )
                   ?? new FoundrySettings();
        }
        catch
        {
            return new FoundrySettings();
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
            return Path.Combine(userProfile, "Dropbox", "SuiteWorkspace", "Foundry", "Knowledge");
        }

        return Path.Combine(
            "C:\\Users\\Public",
            "Dropbox",
            "SuiteWorkspace",
            "Foundry",
            "Knowledge"
        );
    }

    private static string GetDefaultStateRootPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile, "Dropbox", "SuiteWorkspace", "Foundry", "State");
        }

        return Path.Combine("C:\\Users\\Public", "Dropbox", "SuiteWorkspace", "Foundry", "State");
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
