using Foundry.Services;
using Xunit;

namespace Foundry.Core.Tests;

/// <summary>
/// Unit tests for LiveResearchService.
/// Tests exercise parsing logic by injecting pre-canned HTML via a fake HttpMessageHandler
/// so that no real network connections are made.
/// </summary>
public sealed class LiveResearchServiceTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns an HttpClient whose only GET handler returns <paramref name="html"/>.
    /// </summary>
    private static HttpClient FakeClient(string html)
    {
        return new HttpClient(new FakeHttpHandler(html)) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _html;
        public FakeHttpHandler(string html) => _html = html;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_html, System.Text.Encoding.UTF8, "text/html"),
            });
    }

    // -------------------------------------------------------------------------
    // SearchAsync — happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_ParsesDuckDuckGoResultLinks()
    {
        const string html = """
            <html><body>
              <a class="result__a" href="https://example.com/page1">First Result</a>
              <a class="result__snippet">A snippet about the first result.</a>
              <a class="result__a" href="https://example.com/page2">Second Result</a>
              <a class="result__snippet">Another snippet.</a>
            </body></html>
            """;

        using var svc = new LiveResearchService(httpClient: FakeClient(html));
        var results = await svc.SearchAsync("test query");

        Assert.Equal(2, results.Count);
        Assert.Equal("https://example.com/page1", results[0].Url);
        Assert.Equal("First Result", results[0].Title);
        Assert.Equal("A snippet about the first result.", results[0].Snippet);
        Assert.Equal("https://example.com/page2", results[1].Url);
        Assert.Equal("Second Result", results[1].Title);
        Assert.Equal("Another snippet.", results[1].Snippet);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenNoResultLinks()
    {
        const string html = "<html><body><p>No results found.</p></body></html>";

        using var svc = new LiveResearchService(httpClient: FakeClient(html));
        var results = await svc.SearchAsync("obscure query");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_ForEmptyQuery()
    {
        using var svc = new LiveResearchService(httpClient: FakeClient(string.Empty));
        var results = await svc.SearchAsync(string.Empty);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_ForWhitespaceQuery()
    {
        using var svc = new LiveResearchService(httpClient: FakeClient(string.Empty));
        var results = await svc.SearchAsync("   ");
        Assert.Empty(results);
    }

    // -------------------------------------------------------------------------
    // SearchAsync — result caps at MaxResults (10)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_CapsAt10Results()
    {
        var links = string.Concat(Enumerable.Range(1, 15).Select(i =>
            $"""<a class="result__a" href="https://example.com/{i}">Result {i}</a>"""));
        var html = $"<html><body>{links}</body></html>";

        using var svc = new LiveResearchService(httpClient: FakeClient(html));
        var results = await svc.SearchAsync("broad query");

        Assert.Equal(10, results.Count);
    }

    // -------------------------------------------------------------------------
    // ExtractPreviewAsync — meta description preferred
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExtractPreviewAsync_ReturnsMetaDescription_WhenPresent()
    {
        const string html = """
            <html>
              <head>
                <meta name="description" content="This is the meta description." />
              </head>
              <body><p>Body text that should be ignored.</p></body>
            </html>
            """;

        using var svc = new LiveResearchService(httpClient: FakeClient(html));
        var preview = await svc.ExtractPreviewAsync("https://example.com");

        Assert.Equal("This is the meta description.", preview);
    }

    [Fact]
    public async Task ExtractPreviewAsync_FallsBackToOgDescription()
    {
        const string html = """
            <html>
              <head>
                <meta property="og:description" content="OG description content." />
              </head>
              <body><p>Body text.</p></body>
            </html>
            """;

        using var svc = new LiveResearchService(httpClient: FakeClient(html));
        var preview = await svc.ExtractPreviewAsync("https://example.com");

        Assert.Equal("OG description content.", preview);
    }

    // -------------------------------------------------------------------------
    // ExtractPreviewAsync — body text fallback
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExtractPreviewAsync_FallsBackToBodyText_WhenNoMetaDescription()
    {
        const string html = """
            <html>
              <body><p>Hello world content.</p></body>
            </html>
            """;

        using var svc = new LiveResearchService(httpClient: FakeClient(html));
        var preview = await svc.ExtractPreviewAsync("https://example.com");

        Assert.Contains("Hello world content.", preview);
    }

    [Fact]
    public async Task ExtractPreviewAsync_TruncatesLongBodyText()
    {
        var longText = new string('a', 1000);
        var html = $"<html><body><p>{longText}</p></body></html>";

        using var svc = new LiveResearchService(httpClient: FakeClient(html));
        var preview = await svc.ExtractPreviewAsync("https://example.com");

        Assert.True(preview.Length <= LiveResearchService.MaxPreviewLength);
        Assert.EndsWith("...", preview);
    }

    // -------------------------------------------------------------------------
    // ExtractPreviewAsync — edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExtractPreviewAsync_ReturnsEmpty_ForEmptyUrl()
    {
        using var svc = new LiveResearchService(httpClient: FakeClient(string.Empty));
        var preview = await svc.ExtractPreviewAsync(string.Empty);
        Assert.Equal(string.Empty, preview);
    }

    [Fact]
    public async Task ExtractPreviewAsync_ReturnsEmpty_OnHttpFailure()
    {
        var client = new HttpClient(new FailingHttpHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        using var svc = new LiveResearchService(httpClient: client);
        var preview = await svc.ExtractPreviewAsync("https://example.com");
        Assert.Equal(string.Empty, preview);
    }

    private sealed class FailingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Simulated network failure");
    }

    // -------------------------------------------------------------------------
    // EnrichSourcesAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EnrichSourcesAsync_SetsPreviewOnEachResult()
    {
        const string html = """
            <html>
              <head><meta name="description" content="Enriched preview." /></head>
              <body></body>
            </html>
            """;

        var results = new[]
        {
            new ResearchResult { Title = "T1", Url = "https://example.com/1", Snippet = "S1" },
            new ResearchResult { Title = "T2", Url = "https://example.com/2", Snippet = "S2" },
        };

        using var svc = new LiveResearchService(httpClient: FakeClient(html));
        var enriched = await svc.EnrichSourcesAsync(results);

        Assert.Equal(2, enriched.Count);
        Assert.All(enriched, r => Assert.Equal("Enriched preview.", r.Preview));
        // Original fields preserved
        Assert.Equal("T1", enriched[0].Title);
        Assert.Equal("T2", enriched[1].Title);
    }

    [Fact]
    public async Task EnrichSourcesAsync_ReturnsEmpty_ForEmptyInput()
    {
        using var svc = new LiveResearchService(httpClient: FakeClient(string.Empty));
        var enriched = await svc.EnrichSourcesAsync(Array.Empty<ResearchResult>());
        Assert.Empty(enriched);
    }
}
