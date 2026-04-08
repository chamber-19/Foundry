using System.Text.Json.Serialization;

namespace DailyDesk.Models;

public sealed class LearningDocument
{
    public string SourceRootPath { get; init; } = string.Empty;
    public string SourceRootLabel { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public DateTimeOffset LastUpdated { get; init; }
    public int CharacterCount { get; init; }
    public IReadOnlyList<string> Topics { get; init; } = Array.Empty<string>();
    public string Summary { get; init; } = string.Empty;
    public string ExtractedText { get; init; } = string.Empty;

    /// <summary>Structured tables extracted from the document (e.g. PDF tables via Docling).</summary>
    public IReadOnlyList<ExtractedTable> Tables { get; init; } = Array.Empty<ExtractedTable>();

    /// <summary>Figure descriptions extracted from the document (e.g. via Docling OCR/captions).</summary>
    public IReadOnlyList<ExtractedFigure> Figures { get; init; } = Array.Empty<ExtractedFigure>();

    public string PromptSummary =>
        $"[{SourceRootLabel}] {RelativePath} ({Kind}) | topics: {string.Join(", ", Topics.Take(4))} | {Summary}";

    public string DisplaySummary =>
        $"[{SourceRootLabel}] {RelativePath} | {Kind} | {CharacterCount} chars | {string.Join(", ", Topics.Take(4))}";
}

/// <summary>A table extracted from a document, represented as headers and rows.</summary>
public sealed class ExtractedTable
{
    [JsonPropertyName("headers")]
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();

    [JsonPropertyName("rows")]
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = Array.Empty<IReadOnlyList<object?>>();

    /// <summary>Renders the table as a Markdown table string.</summary>
    public string ToMarkdown()
    {
        if (Headers.Count == 0)
            return string.Empty;

        var markdownBuilder = new System.Text.StringBuilder();
        markdownBuilder.AppendLine("| " + string.Join(" | ", Headers) + " |");
        markdownBuilder.AppendLine("| " + string.Join(" | ", Headers.Select(_ => "---")) + " |");
        foreach (var row in Rows)
        {
            var cells = row.Select(c => c?.ToString() ?? "").ToList();
            // Pad or truncate to match header count
            while (cells.Count < Headers.Count) cells.Add("");
            markdownBuilder.AppendLine("| " + string.Join(" | ", cells.Take(Headers.Count)) + " |");
        }
        return markdownBuilder.ToString().TrimEnd();
    }
}

/// <summary>A figure/image extracted from a document with a description.</summary>
public sealed class ExtractedFigure
{
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
}
