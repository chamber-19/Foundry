using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foundry.Models;

public sealed class FoundrySettings
{
    public string OllamaEndpoint { get; init; } = "http://127.0.0.1:11434";
    public string OllamaChatModel { get; init; } = "qwen2.5-coder:14b-instruct-q5_K_M";
    public string OllamaEmbeddingModel { get; init; } = "nomic-embed-text";
    public IReadOnlyList<string> GitHubRepos { get; init; } = GetDefaultGitHubRepos();
    public string GitHubTokenEnvironmentVariable { get; init; } = "GITHUB_TOKEN";
    public int DependencyPollingIntervalMinutes { get; init; } = 10;
    public int JobRetentionDays { get; init; } = 30;
    public string KnowledgeLibraryPath { get; init; } = string.Empty;
    public string StateRootPath { get; init; } = string.Empty;
    public IReadOnlyList<string> AdditionalKnowledgePaths { get; init; } = Array.Empty<string>();
    public FoundryNotificationChannels NotificationChannels { get; init; } = new();
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

    private static IReadOnlyList<string> GetDefaultGitHubRepos() =>
    [
        "chamber-19/launcher",
        "chamber-19/Foundry",
        "chamber-19/Transmittal-Builder",
        "chamber-19/Drawing-List-Manager",
        "chamber-19/desktop-toolkit",
    ];

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

public sealed class FoundryNotificationChannels
{
    public string DiscordAlerts { get; init; } = "alerts";
}
