namespace Foundry.Models;

public sealed class LearningLibrary
{
    public string RootPath { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public IReadOnlyList<string> SourceRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<LearningDocument> Documents { get; init; } = Array.Empty<LearningDocument>();
    public IReadOnlyList<string> TopicHeadlines { get; init; } = Array.Empty<string>();

    public string Summary
    {
        get
        {
            if (!Exists)
            {
                return $"Knowledge folder not found at {RootPath}.";
            }

            if (Documents.Count == 0)
            {
                return $"Knowledge library is ready, but no supported study files were found in the scanned sources.";
            }

            return
                $"{Documents.Count} knowledge files loaded from {SourceRoots.Count} source{(SourceRoots.Count == 1 ? string.Empty : "s")}. Dominant topics: {string.Join(", ", TopicHeadlines.Take(5))}.";
        }
    }
}
