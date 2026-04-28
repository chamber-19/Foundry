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
    public int JobRetentionDays { get; init; } = 30;
    public string KnowledgeLibraryPath { get; init; } = string.Empty;
    public string StateRootPath { get; init; } = string.Empty;
    public IReadOnlyList<string> AdditionalKnowledgePaths { get; init; } = Array.Empty<string>();
    public string? DiscordBotToken { get; init; }

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
        var stateRoot = GetDefaultStateRootPath();
        return Path.Combine(stateRoot, "knowledge");
    }

    private static string GetDefaultStateRootPath()
    {
        var envVal = Environment.GetEnvironmentVariable("FOUNDRY_STATE_ROOT");
        if (!string.IsNullOrWhiteSpace(envVal))
        {
            return envVal;
        }

        if (OperatingSystem.IsWindows())
        {
            return @"C:\FoundryState";
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine("/tmp", "foundry-state")
            : Path.Combine(home, "foundry-state");
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
