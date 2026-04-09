using System.Net;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Foundry.Services;

/// <summary>
/// Fetches web search results from DuckDuckGo HTML and enriches each result
/// with a text preview scraped from the target page.
///
/// HTML extraction uses AngleSharp DOM queries rather than regex patterns,
/// which handles malformed markup, nested tags, and encoded entities correctly.
/// All parsing paths include validation and fallback so callers always receive
/// a usable result even when network or parsing errors occur.
/// </summary>
public sealed class LiveResearchService
{
    // Snippet/preview length cap (characters)
    private const int PreviewMaxLength = 900;
    private const int PreviewTruncateAt = 897;

    // Minimum body text length before we fall back to meta description
    private const int MinBodyLength = 50;

    private readonly HttpClient _httpClient;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly ILogger<LiveResearchService> _logger;

    /// <param name="httpClient">
    ///   Optional pre-configured client (useful in tests). When <c>null</c> a
    ///   default client is created with a 25-second timeout and a browser-like
    ///   User-Agent header so DuckDuckGo does not block the request.
    /// </param>
    /// <param name="resiliencePipeline">
    ///   Polly pipeline that wraps every outbound HTTP call. Defaults to
    ///   <see cref="ResiliencePipeline.Empty"/> (no retry / no timeout).
    ///   Pass <see cref="FoundryResiliencePipelines.BuildWebResearchPipeline"/>
    ///   for production use.
    /// </param>
    /// <param name="logger">Optional structured logger.</param>
    public LiveResearchService(
        HttpClient? httpClient = null,
        ResiliencePipeline? resiliencePipeline = null,
        ILogger<LiveResearchService>? logger = null)
    {
        _httpClient = httpClient ?? BuildDefaultHttpClient();
        _resiliencePipeline = resiliencePipeline ?? ResiliencePipeline.Empty;
        _logger = logger ?? NullLogger<LiveResearchService>.Instance;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Searches DuckDuckGo for <paramref name="query"/> and returns up to
    /// <paramref name="maxResults"/> structured results.
    ///
    /// Returns an empty list (never throws) when the query is blank, the
    /// network is unavailable, or HTML parsing fails.
    /// </summary>
    public async Task<IReadOnlyList<ResearchResult>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogWarning("SearchAsync called with an empty query — returning empty results.");
            return Array.Empty<ResearchResult>();
        }

        var url = BuildSearchUrl(query);
        _logger.LogDebug("Fetching DuckDuckGo results for query={Query} url={Url}", query, url);

        string html;
        try
        {
            html = await _resiliencePipeline.ExecuteAsync(
                async ct => await _httpClient.GetStringAsync(url, ct),
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "DuckDuckGo request failed for query={Query}", query);
            return Array.Empty<ResearchResult>();
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            _logger.LogWarning("DuckDuckGo returned empty response for query={Query}", query);
            return Array.Empty<ResearchResult>();
        }

        return await ParseSearchResultsAsync(html, maxResults, cancellationToken);
    }

    /// <summary>
    /// Fetches each result's target URL in parallel and populates
    /// <see cref="ResearchResult.Preview"/> with a trimmed plain-text extract.
    ///
    /// Results that fail to fetch or parse are kept with an empty preview
    /// rather than being removed.
    /// </summary>
    public async Task<IReadOnlyList<ResearchResult>> EnrichSourcesAsync(
        IReadOnlyList<ResearchResult> results,
        CancellationToken cancellationToken = default)
    {
        if (results.Count == 0)
        {
            return results;
        }

        var enriched = await Task.WhenAll(results.Select(r => EnrichOneAsync(r, cancellationToken)));
        return enriched;
    }

    // -------------------------------------------------------------------------
    // Search result parsing (AngleSharp)
    // -------------------------------------------------------------------------

    private async Task<IReadOnlyList<ResearchResult>> ParseSearchResultsAsync(
        string html,
        int maxResults,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ResearchResult> results = Array.Empty<ResearchResult>();
        try
        {
            var parser = new HtmlParser();
            using var document = await parser.ParseDocumentAsync(html, cancellationToken);

            var parsed = new List<ResearchResult>();

            // DuckDuckGo HTML search: result links use class "result__a"
            var linkElements = document.QuerySelectorAll("a.result__a");
            var snippetElements = document.QuerySelectorAll("a.result__snippet");

            for (var i = 0; i < linkElements.Length && parsed.Count < maxResults; i++)
            {
                var link = linkElements[i];
                var href = link.GetAttribute("href") ?? string.Empty;
                var title = link.TextContent.Trim();

                if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var snippet = i < snippetElements.Length
                    ? snippetElements[i].TextContent.Trim()
                    : string.Empty;

                parsed.Add(new ResearchResult
                {
                    Url = href,
                    Title = title,
                    Snippet = snippet,
                });
            }

            if (parsed.Count > 0)
            {
                results = parsed;
                _logger.LogDebug("Parsed {Count} search results from DuckDuckGo HTML.", parsed.Count);
            }
            else
            {
                _logger.LogWarning(
                    "AngleSharp found 0 results with selector 'a.result__a' — DuckDuckGo HTML structure may have changed.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AngleSharp failed to parse DuckDuckGo search results HTML.");
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // Page enrichment (AngleSharp)
    // -------------------------------------------------------------------------

    private async Task<ResearchResult> EnrichOneAsync(
        ResearchResult result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.Url))
        {
            return result;
        }

        string pageHtml;
        try
        {
            pageHtml = await _resiliencePipeline.ExecuteAsync(
                async ct => await _httpClient.GetStringAsync(result.Url, ct),
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to fetch page for enrichment url={Url}", result.Url);
            return result;
        }

        if (string.IsNullOrWhiteSpace(pageHtml))
        {
            return result;
        }

        var preview = await ExtractPreviewAsync(pageHtml, result.Url, cancellationToken);
        return result with { Preview = preview };
    }

    /// <summary>
    /// Extracts a plain-text preview from <paramref name="html"/> using
    /// AngleSharp's built-in text extraction.
    ///
    /// Falls back to the meta description when the body is shorter than
    /// <c>50</c> characters (threshold controlled by <c>MinBodyLength</c>).
    /// Returns an empty string (never throws) on any parsing failure.
    ///
    /// This method is <c>public</c> so callers can extract a preview from
    /// arbitrary HTML without going through the full search/enrich pipeline.
    /// </summary>
    public async Task<string> ExtractPreviewAsync(
        string html,
        string sourceUrl = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        try
        {
            var parser = new HtmlParser();
            using var document = await parser.ParseDocumentAsync(html, cancellationToken);

            // Prefer body text (AngleSharp decodes entities and strips all tags)
            var bodyText = document.Body?.TextContent ?? string.Empty;
            var normalized = NormalizeWhitespace(bodyText);

            if (normalized.Length >= MinBodyLength)
            {
                return normalized.Length > PreviewMaxLength
                    ? normalized[..PreviewTruncateAt] + "..."
                    : normalized;
            }

            // Body was empty or trivially small — fall back to meta description
            var description = ExtractMetaDescription(document);
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description.Length > PreviewMaxLength
                    ? description[..PreviewTruncateAt] + "..."
                    : description;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AngleSharp failed to extract preview from url={Url}", sourceUrl);
            return string.Empty;
        }
    }

    // -------------------------------------------------------------------------
    // Meta description extraction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads <c>meta[name='description']</c> or <c>meta[property='og:description']</c>.
    /// Returns an empty string when neither is present.
    /// </summary>
    private static string ExtractMetaDescription(AngleSharp.Dom.IDocument document)
    {
        return document.QuerySelector("meta[name='description']")?.GetAttribute("content")
            ?? document.QuerySelector("meta[property='og:description']")?.GetAttribute("content")
            ?? string.Empty;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string BuildSearchUrl(string query)
    {
        var encoded = WebUtility.UrlEncode(query);
        return $"https://html.duckduckgo.com/html/?q={encoded}";
    }

    /// <summary>
    /// Collapses consecutive whitespace characters (newlines, tabs, multiple
    /// spaces) to a single space and trims the result.
    /// </summary>
    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static HttpClient BuildDefaultHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; Foundry/1.0; +https://github.com/Koraji95-coder/Foundry)");
        return client;
    }
}

/// <summary>
/// A single web research result returned by <see cref="LiveResearchService"/>.
/// </summary>
public sealed record ResearchResult
{
    /// <summary>Target URL of the result.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Page title as shown in the search engine listing.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>One-sentence snippet from the search engine result page.</summary>
    public string Snippet { get; init; } = string.Empty;

    /// <summary>
    /// Trimmed plain-text extract fetched from <see cref="Url"/>.
    /// Populated by <see cref="LiveResearchService.EnrichSourcesAsync"/>.
    /// Empty when the page could not be fetched or parsed.
    /// </summary>
    public string Preview { get; init; } = string.Empty;
}
