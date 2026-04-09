using System.Net.Http.Headers;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Foundry.Services;

/// <summary>
/// A single enriched search result produced by <see cref="LiveResearchService"/>.
/// </summary>
public sealed record ResearchResult
{
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Snippet { get; init; } = string.Empty;
    /// <summary>
    /// Full-page preview text extracted from the source URL, if enrichment was requested.
    /// </summary>
    public string Preview { get; init; } = string.Empty;
}

/// <summary>
/// Performs live web research via DuckDuckGo HTML search and optional per-source page enrichment.
/// Uses AngleSharp for all HTML parsing — no regex patterns for HTML extraction.
/// </summary>
public sealed class LiveResearchService : IDisposable
{
    private const string DuckDuckGoUrl = "https://html.duckduckgo.com/html/?q=";
    internal const int MaxPreviewLength = 900;
    private const int MaxResults = 10;

    private static readonly string UserAgent =
        "Mozilla/5.0 (compatible; FoundryBot/1.0; +https://github.com/Foundry)";

    private readonly HttpClient _httpClient;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly ILogger<LiveResearchService> _logger;
    private readonly HtmlParser _parser = new();

    /// <param name="httpClient">Optional pre-configured client (e.g. for testing). If null, a default client is created.</param>
    /// <param name="resiliencePipeline">Optional Polly pipeline. Defaults to <see cref="FoundryResiliencePipelines.BuildWebResearchPipeline"/>.</param>
    /// <param name="logger">Optional structured logger.</param>
    public LiveResearchService(
        HttpClient? httpClient = null,
        ResiliencePipeline? resiliencePipeline = null,
        ILogger<LiveResearchService>? logger = null)
    {
        _httpClient = httpClient ?? BuildDefaultHttpClient();
        _resiliencePipeline = resiliencePipeline ?? FoundryResiliencePipelines.BuildWebResearchPipeline();
        _logger = logger ?? NullLogger<LiveResearchService>.Instance;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Searches DuckDuckGo for the given query and returns up to <see cref="MaxResults"/> results.
    /// </summary>
    public async Task<IReadOnlyList<ResearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ResearchResult>();
        }

        var url = DuckDuckGoUrl + Uri.EscapeDataString(query.Trim());
        _logger.LogInformation("LiveResearch: searching DuckDuckGo for '{Query}'", query);

        try
        {
            var html = await _resiliencePipeline.ExecuteAsync(
                async ct => await _httpClient.GetStringAsync(url, ct),
                cancellationToken);

            return await ParseSearchResultsAsync(html, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiveResearch: DuckDuckGo search failed for '{Query}'. Returning empty results.", query);
            return Array.Empty<ResearchResult>();
        }
    }

    /// <summary>
    /// Fetches each result's URL and enriches the result with a page preview.
    /// Runs all fetches in parallel. Results that fail to fetch keep an empty preview.
    /// </summary>
    public async Task<IReadOnlyList<ResearchResult>> EnrichSourcesAsync(
        IReadOnlyList<ResearchResult> results,
        CancellationToken cancellationToken = default)
    {
        if (results.Count == 0)
        {
            return results;
        }

        var tasks = results.Select(r => EnrichSingleResultAsync(r, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Fetches the given URL and returns a plain-text preview of up to 900 characters.
    /// Returns an empty string when the fetch fails or the page has no extractable body text.
    /// </summary>
    public async Task<string> ExtractPreviewAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        try
        {
            var html = await _resiliencePipeline.ExecuteAsync(
                async ct => await _httpClient.GetStringAsync(url, ct),
                cancellationToken);

            return await ExtractBodyTextAsync(html, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiveResearch: page fetch failed for '{Url}'.", url);
            return string.Empty;
        }
    }

    // -------------------------------------------------------------------------
    // Parsing helpers (AngleSharp)
    // -------------------------------------------------------------------------

    private async Task<IReadOnlyList<ResearchResult>> ParseSearchResultsAsync(
        string html,
        CancellationToken cancellationToken)
    {
        using var document = await _parser.ParseDocumentAsync(html, cancellationToken);

        // DuckDuckGo HTML search result links: <a class="result__a">
        var linkElements = document.QuerySelectorAll("a.result__a");

        // DuckDuckGo snippet elements: <a class="result__snippet">
        var snippetElements = document.QuerySelectorAll("a.result__snippet");

        var results = new List<ResearchResult>(Math.Min(linkElements.Length, MaxResults));

        for (var i = 0; i < Math.Min(linkElements.Length, MaxResults); i++)
        {
            var link = linkElements[i];
            var href = link.GetAttribute("href") ?? string.Empty;
            var title = link.TextContent.Trim();
            var snippet = i < snippetElements.Length
                ? snippetElements[i].TextContent.Trim()
                : string.Empty;

            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            results.Add(new ResearchResult
            {
                Title = title,
                Url = href,
                Snippet = snippet,
            });
        }

        _logger.LogInformation("LiveResearch: parsed {Count} results from DuckDuckGo HTML.", results.Count);
        return results;
    }

    private async Task<string> ExtractBodyTextAsync(string html, CancellationToken cancellationToken)
    {
        using var document = await _parser.ParseDocumentAsync(html, cancellationToken);

        // Try meta description first (most reliable summary)
        var metaDescription =
            document.QuerySelector("meta[name='description']")?.GetAttribute("content")
            ?? document.QuerySelector("meta[property='og:description']")?.GetAttribute("content");

        if (!string.IsNullOrWhiteSpace(metaDescription))
        {
            var trimmed = metaDescription.Trim();
            return trimmed.Length > MaxPreviewLength
                ? trimmed[..(MaxPreviewLength - 3)] + "..."
                : trimmed;
        }

        // Fall back to full body text extraction — AngleSharp handles entity decoding natively.
        // Whitespace-normalize the extracted plain text (not HTML parsing).
        var bodyText = document.QuerySelector("body")?.TextContent ?? string.Empty;
        var cleaned = string.Join(" ", bodyText.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries));

        return cleaned.Length > MaxPreviewLength
            ? cleaned[..(MaxPreviewLength - 3)] + "..."
            : cleaned;
    }

    private async Task<ResearchResult> EnrichSingleResultAsync(
        ResearchResult result,
        CancellationToken cancellationToken)
    {
        var preview = await ExtractPreviewAsync(result.Url, cancellationToken);
        return result with { Preview = preview };
    }

    // -------------------------------------------------------------------------
    // Infrastructure
    // -------------------------------------------------------------------------

    private static HttpClient BuildDefaultHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/html"));
        return client;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
