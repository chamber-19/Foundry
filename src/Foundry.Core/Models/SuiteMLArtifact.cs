namespace Foundry.Models;

public sealed class SuiteMLArtifactBundle
{
    public bool Ok { get; init; }
    public string GeneratedAt { get; init; } = string.Empty;
    public IReadOnlyList<SuiteMLArtifact> Artifacts { get; init; } = Array.Empty<SuiteMLArtifact>();
}

public sealed class SuiteMLArtifact
{
    public string ArtifactType { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0.0";
    public string GeneratedAt { get; init; } = string.Empty;
    public string Source { get; init; } = "office-ml-pipeline";
    public bool ReviewRequired { get; init; } = true;
    public object? Data { get; init; }
}
