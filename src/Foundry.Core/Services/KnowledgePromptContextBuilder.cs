using System.Text;
using System.Text.RegularExpressions;
using Foundry.Models;

namespace Foundry.Services;

public static class KnowledgePromptContextBuilder
{
    private static readonly Regex QueryTokenRegex = new(
        "[A-Za-z][A-Za-z0-9+#.-]{2,}",
        RegexOptions.Compiled
    );

    private static readonly Regex ParagraphBreakRegex = new(
        @"\n{2,}",
        RegexOptions.Compiled
    );

    private static readonly Regex SentenceBreakRegex = new(
        @"(?<=[.!?])\s+",
        RegexOptions.Compiled
    );

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "about",
            "after",
            "again",
            "also",
            "because",
            "being",
            "between",
            "build",
            "current",
            "desk",
            "document",
            "engineering",
            "file",
            "files",
            "from",
            "have",
            "help",
            "into",
            "just",
            "later",
            "local",
            "models",
            "notes",
            "project",
            "should",
            "study",
            "suite",
            "than",
            "that",
            "their",
            "them",
            "there",
            "these",
            "this",
            "through",
            "want",
            "with",
            "write",
            "your",
        };

    public static string BuildRelevantContext(
        LearningLibrary library,
        IReadOnlyList<string?> hints,
        int maxDocuments = 3,
        int maxTotalCharacters = 2400,
        int maxExcerptCharacters = 720
    )
    {
        if (library.Documents.Count == 0)
        {
            return "none recorded";
        }

        var terms = ExtractTerms(hints);
        var candidates = library.Documents
            .Where(HasPromptableContent)
            .Select(document => new
            {
                Document = document,
                Score = ScoreDocument(document, terms),
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Document.LastUpdated)
            .Take(Math.Max(maxDocuments * 3, maxDocuments))
            .ToList();

        if (candidates.Count == 0)
        {
            return "none recorded";
        }

        var builder = new StringBuilder();
        var remainingCharacters = Math.Max(400, maxTotalCharacters);
        var added = 0;

        foreach (var candidate in candidates)
        {
            if (added >= maxDocuments || remainingCharacters <= 180)
            {
                break;
            }

            var document = candidate.Document;
            var excerptBudget = Math.Min(maxExcerptCharacters, Math.Max(220, remainingCharacters - 160));
            var excerpt = BuildExcerpt(document, terms, excerptBudget);
            if (string.IsNullOrWhiteSpace(excerpt))
            {
                continue;
            }

            var topics = document.Topics.Count == 0
                ? "none"
                : string.Join(", ", document.Topics.Take(4));

            var block =
                $"[{document.SourceRootLabel}] {document.RelativePath} ({document.Kind}){Environment.NewLine}"
                + $"topics: {topics}{Environment.NewLine}"
                + $"evidence: {excerpt}";

            if (builder.Length > 0)
            {
                block = $"{Environment.NewLine}{Environment.NewLine}{block}";
            }

            if (block.Length > remainingCharacters && added > 0)
            {
                break;
            }

            if (block.Length > remainingCharacters)
            {
                block = TrimToLength(block, remainingCharacters);
            }

            builder.Append(block);
            remainingCharacters -= block.Length;
            added++;
        }

        return builder.Length == 0 ? "none recorded" : builder.ToString().Trim();
    }

    /// <summary>
    /// Builds context using semantic search (Qdrant) with keyword fallback.
    /// Semantic results are preferred; keyword results fill remaining slots.
    /// Falls back entirely to keyword search if embedding or vector store is unavailable.
    /// </summary>
    public static async Task<string> BuildRelevantContextWithSemanticSearchAsync(
        LearningLibrary library,
        IReadOnlyList<string?> hints,
        EmbeddingService? embeddingService,
        VectorStoreService? vectorStoreService,
        int maxDocuments = 3,
        int maxTotalCharacters = 2400,
        int maxExcerptCharacters = 720,
        CancellationToken cancellationToken = default)
    {
        if (library.Documents.Count == 0)
        {
            return "none recorded";
        }

        var semanticPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Attempt semantic search if both services are available
        if (embeddingService is not null && vectorStoreService is not null)
        {
            var queryText = string.Join(" ", hints.Where(h => !string.IsNullOrWhiteSpace(h)));
            if (!string.IsNullOrWhiteSpace(queryText))
            {
                try
                {
                    var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(queryText, cancellationToken);
                    if (queryEmbedding is not null)
                    {
                        var searchResults = await vectorStoreService.SearchAsync(queryEmbedding, topK: maxDocuments, cancellationToken);
                        foreach (var result in searchResults)
                        {
                            if (result.Metadata.TryGetValue("path", out var path) && !string.IsNullOrWhiteSpace(path))
                            {
                                semanticPaths.Add(path);
                            }
                        }
                    }
                }
                catch
                {
                    // Semantic search failed; fall through to keyword search
                }
            }
        }

        var terms = ExtractTerms(hints);

        // Build ranked candidate list: semantic results first, then keyword results
        var documentsByPath = library.Documents
            .Where(HasPromptableContent)
            .ToDictionary(d => d.RelativePath, d => d, StringComparer.OrdinalIgnoreCase);

        var orderedCandidates = new List<LearningDocument>();

        // Add semantic matches first (in search result order)
        foreach (var path in semanticPaths)
        {
            if (documentsByPath.TryGetValue(path, out var doc))
            {
                orderedCandidates.Add(doc);
            }
        }

        // Fill remaining slots with keyword-ranked documents
        var keywordCandidates = library.Documents
            .Where(HasPromptableContent)
            .Where(d => !semanticPaths.Contains(d.RelativePath))
            .Select(document => new { Document = document, Score = ScoreDocument(document, terms) })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Document.LastUpdated)
            .Select(item => item.Document)
            .ToList();

        orderedCandidates.AddRange(keywordCandidates);

        if (orderedCandidates.Count == 0)
        {
            return "none recorded";
        }

        // Build output using the same formatting as the synchronous method
        var builder = new StringBuilder();
        var remainingCharacters = Math.Max(400, maxTotalCharacters);
        var added = 0;

        foreach (var document in orderedCandidates)
        {
            if (added >= maxDocuments || remainingCharacters <= 180)
            {
                break;
            }

            var excerptBudget = Math.Min(maxExcerptCharacters, Math.Max(220, remainingCharacters - 160));
            var excerpt = BuildExcerpt(document, terms, excerptBudget);
            if (string.IsNullOrWhiteSpace(excerpt))
            {
                continue;
            }

            var topics = document.Topics.Count == 0
                ? "none"
                : string.Join(", ", document.Topics.Take(4));

            var block =
                $"[{document.SourceRootLabel}] {document.RelativePath} ({document.Kind}){Environment.NewLine}"
                + $"topics: {topics}{Environment.NewLine}"
                + $"evidence: {excerpt}";

            if (builder.Length > 0)
            {
                block = $"{Environment.NewLine}{Environment.NewLine}{block}";
            }

            if (block.Length > remainingCharacters && added > 0)
            {
                break;
            }

            if (block.Length > remainingCharacters)
            {
                block = TrimToLength(block, remainingCharacters);
            }

            builder.Append(block);
            remainingCharacters -= block.Length;
            added++;
        }

        return builder.Length == 0 ? "none recorded" : builder.ToString().Trim();
    }

    private static bool HasPromptableContent(LearningDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.ExtractedText))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(document.Summary)
            && !document.Summary.StartsWith("Import failed:", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ExtractTerms(IReadOnlyList<string?> hints)
    {
        var terms = new List<string>();
        foreach (var hint in hints)
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                continue;
            }

            foreach (
                var term in QueryTokenRegex
                    .Matches(hint)
                    .Select(match => match.Value.Trim().ToLowerInvariant())
            )
            {
                if (term.Length < 3 || StopWords.Contains(term))
                {
                    continue;
                }

                if (!terms.Contains(term, StringComparer.OrdinalIgnoreCase))
                {
                    terms.Add(term);
                }
            }
        }

        return terms;
    }

    private static int ScoreDocument(LearningDocument document, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return document.Topics.Count;
        }

        var score = 0;
        foreach (var term in terms)
        {
            score += CountWeightedMatches(document.RelativePath, term, 12, 1);
            score += CountWeightedMatches(document.FileName, term, 12, 1);
            score += CountWeightedMatches(document.Summary, term, 5, 2);
            score += document.Topics.Sum(topic =>
                ContainsInvariant(topic, term) ? 10 : 0
            );

            if (!string.IsNullOrWhiteSpace(document.ExtractedText))
            {
                score += CountWeightedMatches(document.ExtractedText, term, 3, 5);
            }
        }

        return score + Math.Min(document.Topics.Count, 4);
    }

    private static int CountWeightedMatches(
        string? value,
        string term,
        int weight,
        int maxMatches
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var matches = CountMatches(value, term, maxMatches);
        return matches * weight;
    }

    private static int CountMatches(string value, string term, int maxMatches)
    {
        var count = 0;
        var start = 0;
        while (start < value.Length && count < maxMatches)
        {
            var index = value.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            count++;
            start = index + term.Length;
        }

        return count;
    }

    private static string BuildExcerpt(
        LearningDocument document,
        IReadOnlyList<string> terms,
        int maxExcerptCharacters
    )
    {
        var text = document.ExtractedText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return TrimToLength(document.Summary, maxExcerptCharacters);
        }

        var segments = SegmentText(text, Math.Min(360, Math.Max(220, maxExcerptCharacters / 2)))
            .Select(segment => new
            {
                Segment = segment,
                Score = ScoreSegment(segment, terms),
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Segment.Length)
            .ToList();

        if (segments.Count == 0)
        {
            return TrimToLength(document.Summary, maxExcerptCharacters);
        }

        var selected = new List<string>();
        var remaining = maxExcerptCharacters;
        foreach (var segment in segments)
        {
            if (remaining <= 40)
            {
                break;
            }

            var candidate = TrimToLength(segment.Segment, Math.Min(remaining, 360));
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            selected.Add(candidate);
            remaining -= candidate.Length + 5;

            if (selected.Count >= 2)
            {
                break;
            }
        }

        if (selected.Count == 0)
        {
            return TrimToLength(document.Summary, maxExcerptCharacters);
        }

        return TrimToLength(string.Join(" ... ", selected), maxExcerptCharacters);
    }

    private static IEnumerable<string> SegmentText(string text, int maxSegmentCharacters)
    {
        var normalized = text.Replace("\r", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        var paragraphs = ParagraphBreakRegex
            .Split(normalized)
            .Select(CompactWhitespace)
            .Where(value => !string.IsNullOrWhiteSpace(value));

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length <= maxSegmentCharacters)
            {
                yield return paragraph;
                continue;
            }

            var sentences = SentenceBreakRegex
                .Split(paragraph)
                .Select(CompactWhitespace)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (sentences.Count <= 1)
            {
                foreach (var chunk in ChunkByWords(paragraph, maxSegmentCharacters))
                {
                    yield return chunk;
                }

                continue;
            }

            var builder = new StringBuilder();
            foreach (var sentence in sentences)
            {
                if (builder.Length == 0)
                {
                    builder.Append(sentence);
                    continue;
                }

                if (builder.Length + sentence.Length + 1 > maxSegmentCharacters)
                {
                    yield return builder.ToString();
                    builder.Clear();
                    builder.Append(sentence);
                    continue;
                }

                builder.Append(' ');
                builder.Append(sentence);
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
            }
        }
    }

    private static IEnumerable<string> ChunkByWords(string text, int maxChunkCharacters)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            yield break;
        }

        var builder = new StringBuilder();
        foreach (var word in words)
        {
            if (builder.Length == 0)
            {
                builder.Append(word);
                continue;
            }

            if (builder.Length + word.Length + 1 > maxChunkCharacters)
            {
                yield return builder.ToString();
                builder.Clear();
                builder.Append(word);
                continue;
            }

            builder.Append(' ');
            builder.Append(word);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static int ScoreSegment(string segment, IReadOnlyList<string> terms)
    {
        var score = 0;
        if (segment.StartsWith("OneNote package:", StringComparison.OrdinalIgnoreCase))
        {
            score -= 2;
        }

        if (segment.Contains("No readable text extracted", StringComparison.OrdinalIgnoreCase))
        {
            score -= 4;
        }

        if (terms.Count == 0)
        {
            return score + 1;
        }

        foreach (var term in terms)
        {
            score += CountMatches(segment, term, 6) * 8;
        }

        return score;
    }

    private static string CompactWhitespace(string value) =>
        WhitespaceRegex.Replace(value, " ").Trim();

    private static bool ContainsInvariant(string value, string term) =>
        value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string TrimToLength(string value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var compact = CompactWhitespace(value);
        if (compact.Length <= maxCharacters)
        {
            return compact;
        }

        if (maxCharacters <= 3)
        {
            return compact[..maxCharacters];
        }

        return $"{compact[..(maxCharacters - 3)].TrimEnd()}...";
    }
}
