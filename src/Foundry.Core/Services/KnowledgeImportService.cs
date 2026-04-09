using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foundry.Models;

namespace Foundry.Services;

public sealed class KnowledgeImportService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".pdf", ".docx", ".pptx", ".onepkg" };

    private readonly ProcessRunner _processRunner;
    private readonly string _pythonScriptPath;

    public KnowledgeImportService(ProcessRunner processRunner, string pythonScriptPath)
    {
        _processRunner = processRunner;
        _pythonScriptPath = pythonScriptPath;
    }

    public async Task<LearningLibrary> LoadAsync(
        string rootPath,
        IReadOnlyList<string>? additionalRootPaths = null,
        CancellationToken cancellationToken = default
    )
    {
        Directory.CreateDirectory(rootPath);
        EnsureKnowledgeRootFolders(rootPath);

        var documents = new List<LearningDocument>();
        var sourceRoots = new List<string> { rootPath };
        if (additionalRootPaths is not null)
        {
            sourceRoots.AddRange(additionalRootPaths.Where(path => !string.IsNullOrWhiteSpace(path)));
        }

        var normalizedRoots = sourceRoots
            .Select(path => Path.GetFullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (
            var source in normalizedRoots
                .Where(Directory.Exists)
                .SelectMany(sourceRoot =>
                    Directory
                        .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                        .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                        .Select(path => new
                        {
                            SourceRoot = sourceRoot,
                            Path = path,
                            LastWrite = File.GetLastWriteTimeUtc(path),
                        })
                )
                .OrderByDescending(item => item.LastWrite)
                .Take(64)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await LoadDocumentAsync(source.SourceRoot, source.Path, cancellationToken);
            documents.Add(document);
        }

        var topicHeadlines = documents
            .SelectMany(document => document.Topics)
            .GroupBy(topic => topic, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(8)
            .Select(group => group.Key)
            .ToList();

        return new LearningLibrary
        {
            RootPath = rootPath,
            Exists = Directory.Exists(rootPath),
            SourceRoots = normalizedRoots.Where(Directory.Exists).ToList(),
            Documents = documents,
            TopicHeadlines = topicHeadlines,
        };
    }

    private async Task<LearningDocument> LoadDocumentAsync(
        string rootPath,
        string path,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var tables = Array.Empty<ExtractedTable>();
            var figures = Array.Empty<ExtractedFigure>();
            string text;

            var extension = Path.GetExtension(path);
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var response = await ExtractViaPythonRichAsync(path, cancellationToken);
                text = response.Text ?? string.Empty;
                if (response.Tables is { Count: > 0 })
                    tables = response.Tables.ToArray();
                if (response.Figures is { Count: > 0 })
                    figures = response.Figures.ToArray();
            }
            else
            {
                text = await ExtractTextAsync(path, cancellationToken);
            }

            var normalized = NormalizeText(text);

            // Append table markdown and figure descriptions to extracted text
            var textBuilder = new StringBuilder(normalized);
            foreach (var table in tables)
            {
                var md = table.ToMarkdown();
                if (!string.IsNullOrWhiteSpace(md))
                {
                    textBuilder.AppendLine();
                    textBuilder.AppendLine(md);
                }
            }
            foreach (var figure in figures)
            {
                if (!string.IsNullOrWhiteSpace(figure.Description))
                {
                    textBuilder.AppendLine();
                    textBuilder.AppendLine($"[Figure: {figure.Description}]");
                }
            }

            var fullText = textBuilder.ToString();
            var topics = ExtractTopics(fullText, path);

            return new LearningDocument
            {
                SourceRootPath = rootPath,
                SourceRootLabel = BuildSourceRootLabel(rootPath),
                FileName = Path.GetFileName(path),
                FullPath = path,
                RelativePath = Path.GetRelativePath(rootPath, path),
                Kind = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                LastUpdated = File.GetLastWriteTimeUtc(path),
                CharacterCount = fullText.Length,
                Topics = topics,
                Summary = BuildSummary(fullText),
                ExtractedText = BuildPromptContextText(fullText),
                Tables = tables,
                Figures = figures,
            };
        }
        catch (Exception exception)
        {
            return new LearningDocument
            {
                SourceRootPath = rootPath,
                SourceRootLabel = BuildSourceRootLabel(rootPath),
                FileName = Path.GetFileName(path),
                FullPath = path,
                RelativePath = Path.GetRelativePath(rootPath, path),
                Kind = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                LastUpdated = File.GetLastWriteTimeUtc(path),
                Summary = $"Import failed: {exception.Message}",
            };
        }
    }

    private async Task<string> ExtractTextAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractDocxText(path);
        }

        if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractPptxText(path);
        }

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return await ExtractViaPythonAsync(path, cancellationToken);
        }

        if (extension.Equals(".onepkg", StringComparison.OrdinalIgnoreCase))
        {
            return await ExtractOneNotePackageTextAsync(path, cancellationToken);
        }

        return string.Empty;
    }

    private async Task<string> ExtractOneNotePackageTextAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var extractionRoot = Path.Combine(
            Path.GetTempPath(),
            "Foundry-OneNote",
            Path.GetFileNameWithoutExtension(path),
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(extractionRoot);

        var builder = new StringBuilder();
        builder.AppendLine($"OneNote package: {Path.GetFileName(path)}");
        builder.AppendLine();

        try
        {
            await _processRunner.RunAsync(
                "expand.exe",
                $"-F:* \"{path}\" \"{extractionRoot}\"",
                workingDirectory: extractionRoot,
                cancellationToken: cancellationToken
            );

            var extractedFiles = Directory
                .EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var entryCount = 0;
            foreach (var extractedPath in extractedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryCount++;

                var entryName = Path.GetRelativePath(extractionRoot, extractedPath);
                var extension = Path.GetExtension(extractedPath);
                string entryText;
                if (extension.Equals(".one", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".onetoc2", StringComparison.OrdinalIgnoreCase))
                {
                    await using var stream = File.OpenRead(extractedPath);
                    entryText = ExtractOneNoteBinaryText(stream);
                }
                else
                {
                    entryText = await ExtractArchiveTextAsync(extractedPath, cancellationToken);
                }

                builder.AppendLine($"Entry: {entryName}");
                if (string.IsNullOrWhiteSpace(entryText))
                {
                    builder.AppendLine("No readable text extracted from this entry.");
                }
                else
                {
                    builder.AppendLine(entryText.Trim());
                }

                builder.AppendLine();
            }

            if (entryCount == 0)
            {
                return "OneNote package opened, but no readable entries were found.";
            }

            return builder.ToString().Trim();
        }
        finally
        {
            if (Directory.Exists(extractionRoot))
            {
                try
                {
                    Directory.Delete(extractionRoot, recursive: true);
                }
                catch
                {
                    // Temp extraction cleanup is best-effort only.
                }
            }
        }
    }

    private static async Task<string> ExtractArchiveTextAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }

        return string.Empty;
    }

    private static string ExtractOneNoteBinaryText(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        var strings = ExtractUtf16LittleEndianStrings(bytes)
            .Concat(ExtractPrintableAsciiStrings(bytes))
            .Select(static value => Regex.Replace(value, "\\s+", " ").Trim())
            .Where(IsUsefulExtractedString)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static value => new { Value = value, Score = ScoreExtractedString(value) })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Value.Length)
            .Select(item => item.Value)
            .Take(120)
            .ToList();

        return strings.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, strings);
    }

    private static IEnumerable<string> ExtractPrintableAsciiStrings(byte[] bytes)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            if (value is >= 32 and <= 126)
            {
                builder.Append((char)value);
                continue;
            }

            if (builder.Length >= 6)
            {
                yield return builder.ToString();
            }

            builder.Clear();
        }

        if (builder.Length >= 6)
        {
            yield return builder.ToString();
        }
    }

    private static IEnumerable<string> ExtractUtf16LittleEndianStrings(byte[] bytes)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < bytes.Length - 1;)
        {
            if (bytes[index + 1] == 0 && bytes[index] is >= 32 and <= 126)
            {
                builder.Clear();
                while (index < bytes.Length - 1 && bytes[index + 1] == 0 && bytes[index] is >= 32 and <= 126)
                {
                    builder.Append((char)bytes[index]);
                    index += 2;
                }

                if (builder.Length >= 6)
                {
                    yield return builder.ToString();
                }

                continue;
            }

            index++;
        }
    }

    private static bool IsUsefulExtractedString(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 6)
        {
            return false;
        }

        if ((value.Contains('<') && value.Contains('>'))
            || value.Contains("provider=\"", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var letterCount = value.Count(char.IsLetter);
        var spaceCount = value.Count(char.IsWhiteSpace);
        var noisySymbolCount = value.Count(static c =>
            !char.IsLetterOrDigit(c)
            && !char.IsWhiteSpace(c)
            && c is not '.' and not ',' and not '-' and not '_' and not ':' and not ';'
            && c is not '(' and not ')' and not '[' and not ']' and not '/' and not '&'
            && c is not '#' and not '\'');

        if (letterCount < 4)
        {
            return false;
        }

        if (spaceCount == 0 && value.Length > 48)
        {
            return false;
        }

        return noisySymbolCount <= Math.Max(6, value.Length / 10);
    }

    private static int ScoreExtractedString(string value)
    {
        var wordCount = Regex.Matches(value, "[A-Za-z]{2,}").Count;
        var letterCount = value.Count(char.IsLetter);
        var whitespaceCount = value.Count(char.IsWhiteSpace);
        var digitCount = value.Count(char.IsDigit);
        var penaltyCount = value.Count(static c =>
            c is '<' or '>' or '{' or '}' or '|' or '`' or '~' or '^' or '"' or '*');

        return (wordCount * 5)
            + (letterCount / 14)
            + (whitespaceCount * 2)
            - (digitCount / 5)
            - (penaltyCount * 8);
    }

    private async Task<string> ExtractViaPythonAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var response = await ExtractViaPythonRichAsync(path, cancellationToken);
        return response.Text ?? string.Empty;
    }

    private async Task<PythonExtractionResponse> ExtractViaPythonRichAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(_pythonScriptPath))
        {
            throw new FileNotFoundException("Python extractor script was not found.", _pythonScriptPath);
        }

        var output = await _processRunner.RunAsync(
            "python",
            $"\"{_pythonScriptPath}\" \"{path}\"",
            workingDirectory: Path.GetDirectoryName(_pythonScriptPath),
            cancellationToken: cancellationToken
        );

        var response =
            JsonSerializer.Deserialize<PythonExtractionResponse>(
                output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? new PythonExtractionResponse();

        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error ?? "Python extraction failed.");
        }

        return response;
    }

    private static string ExtractDocxText(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml");
        if (entry is null)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var xml = reader.ReadToEnd();

        var textMatches = Regex.Matches(xml, "<w:t[^>]*>(.*?)</w:t>", RegexOptions.Singleline);
        var builder = new StringBuilder();
        foreach (Match match in textMatches)
        {
            var value = Regex.Replace(match.Groups[1].Value, "<.*?>", string.Empty);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            builder.Append(value.Trim());
            builder.Append(' ');
        }

        return builder.ToString();
    }

    private static string ExtractPptxText(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var slideEntries = archive.Entries
            .Where(entry =>
                entry.FullName.StartsWith("ppt/slides/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var notesEntries = archive.Entries
            .Where(entry =>
                entry.FullName.StartsWith("ppt/notesSlides/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var builder = new StringBuilder();
        foreach (var entry in slideEntries.Concat(notesEntries))
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var xml = reader.ReadToEnd();
            var textMatches = Regex.Matches(xml, "<a:t[^>]*>(.*?)</a:t>", RegexOptions.Singleline);
            foreach (Match match in textMatches)
            {
                var value = Regex.Replace(match.Groups[1].Value, "<.*?>", string.Empty);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                builder.Append(value.Trim());
                builder.Append(' ');
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> ExtractTopics(string text, string path)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void AddContent(string content, int weight)
        {
            foreach (
                var match in Regex.Matches(content, "[A-Za-z][A-Za-z0-9+#.-]{3,}")
                    .Select(match => match.Value)
            )
            {
                var normalized = match.Trim().ToLowerInvariant();
                if (StopWords.Contains(normalized))
                {
                    continue;
                }

                counts[normalized] = counts.TryGetValue(normalized, out var current)
                    ? current + weight
                    : weight;
            }
        }

        AddContent(Path.GetFileNameWithoutExtension(path).Replace('-', ' ').Replace('_', ' '), 3);
        AddContent(text, 1);

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(6)
            .Select(pair => FormatTopic(pair.Key))
            .ToList();
    }

    private static string BuildSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "No extractable text was found.";
        }

        var compact = Regex.Replace(text, @"\s+", " ").Trim();
        if (compact.Length <= 320)
        {
            return compact;
        }

        return $"{compact[..317]}...";
    }

    private static string NormalizeText(string text)
    {
        return text.Replace("\r", "\n").Replace("\t", " ").Trim();
    }

    private static string BuildPromptContextText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(text, @"[ \t]+", " ");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n").Trim();

        const int maxCharacters = 12000;
        if (normalized.Length <= maxCharacters)
        {
            return normalized;
        }

        return $"{normalized[..(maxCharacters - 3)].TrimEnd()}...";
    }

    private static string FormatTopic(string value)
    {
        return string.Join(
            " ",
            value.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant())
        );
    }

    private static string BuildSourceRootLabel(string rootPath)
    {
        var normalized = rootPath.Replace('/', '\\');
        if (normalized.Contains("\\OneNote Notebooks", StringComparison.OrdinalIgnoreCase))
        {
            return "OneNote";
        }

        var directoryName = Path.GetFileName(normalized.TrimEnd('\\'));
        return string.IsNullOrWhiteSpace(directoryName) ? "Knowledge" : directoryName;
    }

    private static void EnsureKnowledgeRootFolders(string rootPath)
    {
        foreach (var relativeDirectory in new[] { "Class Notes", "Research", "Follow Through" })
        {
            Directory.CreateDirectory(Path.Combine(rootPath, relativeDirectory));
        }
    }

    private sealed class PythonExtractionResponse
    {
        public bool Ok { get; set; }
        public string? Text { get; set; }
        public string? Error { get; set; }
        public PythonExtractionMetadata? Metadata { get; set; }
        public List<ExtractedTable>? Tables { get; set; }
        public List<ExtractedFigure>? Figures { get; set; }
    }

    private sealed class PythonExtractionMetadata
    {
        public string? Extractor { get; set; }
        public string? Format { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("table_count")]
        public int TableCount { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("figure_count")]
        public int FigureCount { get; set; }
    }

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
}
