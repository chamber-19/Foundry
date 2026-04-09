using Foundry.Services;
using Xunit;

namespace Foundry.Core.Tests;

/// <summary>
/// Unit tests for <see cref="LiveResearchService"/>.
///
/// All tests operate entirely offline: instead of making live HTTP calls the
/// tests inject pre-built HTML strings directly through the internal
/// <see cref="LiveResearchService.ExtractPreviewAsync"/> method, or they
/// exercise public methods with an <see cref="HttpClient"/> backed by a
/// <see cref="FakeHttpMessageHandler"/> that returns controlled responses.
/// </summary>
public sealed class LiveResearchServiceTests
{
    // -------------------------------------------------------------------------
    // ExtractPreviewAsync — normal HTML
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExtractPreviewAsync_NormalPage_ReturnsTrimmedBodyText()
    {
        var svc = new LiveResearchService();
        var html = "<html><body><p>Hello world. This is a test page with enough content to pass the threshold.</p></body></html>";

        var preview = await svc.ExtractPreviewAsync(html);

        Assert.False(string.IsNullOrWhiteSpace(preview));
        Assert.Contains("Hello world", preview);
    }

    [Fact]
    public async Task ExtractPreviewAsync_LongBody_TruncatesAt900Characters()
    {
        var svc = new LiveResearchService();
        var longText = new string('a', 1000);
        var html = $"<html><body><p>{longText}</p></body></html>";

        var preview = await svc.ExtractPreviewAsync(html);

        Assert.True(preview.Length <= 900);
        Assert.EndsWith("...", preview);
    }

    [Fact]
    public async Task ExtractPreviewAsync_EmptyBody_FallsBackToMetaDescription()
    {
        var svc = new LiveResearchService();
        const string description = "This page has a meta description only.";
        var html = $"<html><head><meta name='description' content='{description}'/></head><body></body></html>";

        var preview = await svc.ExtractPreviewAsync(html);

        Assert.Equal(description, preview);
    }

    [Fact]
    public async Task ExtractPreviewAsync_EmptyBodyWithOgDescription_FallsBackToOgDescription()
    {
        var svc = new LiveResearchService();
        const string ogDescription = "Open Graph description content.";
        var html = $"<html><head><meta property='og:description' content='{ogDescription}'/></head><body></body></html>";

        var preview = await svc.ExtractPreviewAsync(html);

        Assert.Equal(ogDescription, preview);
    }

    [Fact]
    public async Task ExtractPreviewAsync_NullOrWhitespaceHtml_ReturnsEmptyString()
    {
        var svc = new LiveResearchService();

        Assert.Equal(string.Empty, await svc.ExtractPreviewAsync(string.Empty));
        Assert.Equal(string.Empty, await svc.ExtractPreviewAsync("   "));
    }

    [Fact]
    public async Task ExtractPreviewAsync_HtmlEntities_AreDecodedByAngleSharp()
    {
        var svc = new LiveResearchService();
        // Body text must be long enough to exceed the MinBodyLength threshold in LiveResearchService
        // so the body path is exercised (not the meta-description fallback).
        var html = "<html><body><p>AT&amp;T &lt;rocks&gt; &#8212; a great company with plenty of words to exceed the minimum body length check.</p></body></html>";

        var preview = await svc.ExtractPreviewAsync(html);

        Assert.Contains("AT&T", preview);
        Assert.Contains("<rocks>", preview);
    }

    [Fact]
    public async Task ExtractPreviewAsync_MultipleWhitespacesAndNewlines_NormalizedToSingleSpace()
    {
        var svc = new LiveResearchService();
        var html = "<html><body><p>Line one\n\nLine   two\t\ttabs</p></body></html>";

        var preview = await svc.ExtractPreviewAsync(html);

        Assert.DoesNotContain("\n", preview);
        Assert.DoesNotContain("\t", preview);
        Assert.DoesNotContain("  ", preview);
    }

    // -------------------------------------------------------------------------
    // SearchAsync — validation guards
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchAsync_EmptyOrWhitespaceQuery_ReturnsEmptyList(string query)
    {
        var svc = new LiveResearchService();

        var results = await svc.SearchAsync(query);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_HttpClientThrows_ReturnsEmptyListWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("network down"));
        var client = new HttpClient(handler);
        var svc = new LiveResearchService(client);

        var results = await svc.SearchAsync("test query");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_EmptyResponse_ReturnsEmptyList()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty),
            });
        var client = new HttpClient(handler);
        var svc = new LiveResearchService(client);

        var results = await svc.SearchAsync("test query");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ValidDuckDuckGoHtml_ReturnsResults()
    {
        // Minimal DuckDuckGo HTML structure with two results
        const string html = """
            <html><body>
              <a class="result__a" href="https://example.com/page1">Example Page One</a>
              <a class="result__snippet">First result snippet text.</a>
              <a class="result__a" href="https://example.com/page2">Example Page Two</a>
              <a class="result__snippet">Second result snippet text.</a>
            </body></html>
            """;

        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(html),
            });
        var client = new HttpClient(handler);
        var svc = new LiveResearchService(client);

        var results = await svc.SearchAsync("test query", maxResults: 5);

        Assert.Equal(2, results.Count);

        Assert.Equal("https://example.com/page1", results[0].Url);
        Assert.Equal("Example Page One", results[0].Title);
        Assert.Equal("First result snippet text.", results[0].Snippet);

        Assert.Equal("https://example.com/page2", results[1].Url);
        Assert.Equal("Example Page Two", results[1].Title);
        Assert.Equal("Second result snippet text.", results[1].Snippet);
    }

    [Fact]
    public async Task SearchAsync_MaxResultsRespected_ReturnsAtMostRequested()
    {
        const string html = """
            <html><body>
              <a class="result__a" href="https://example.com/1">Result 1</a>
              <a class="result__a" href="https://example.com/2">Result 2</a>
              <a class="result__a" href="https://example.com/3">Result 3</a>
              <a class="result__a" href="https://example.com/4">Result 4</a>
            </body></html>
            """;

        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(html),
            });
        var client = new HttpClient(handler);
        var svc = new LiveResearchService(client);

        var results = await svc.SearchAsync("query", maxResults: 2);

        Assert.Equal(2, results.Count);
    }

    // -------------------------------------------------------------------------
    // EnrichSourcesAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EnrichSourcesAsync_EmptyList_ReturnsEmptyList()
    {
        var svc = new LiveResearchService();
        var enriched = await svc.EnrichSourcesAsync(Array.Empty<ResearchResult>());
        Assert.Empty(enriched);
    }

    [Fact]
    public async Task EnrichSourcesAsync_FetchFails_KeepsResultWithEmptyPreview()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("timeout"));
        var client = new HttpClient(handler);
        var svc = new LiveResearchService(client);

        var input = new[]
        {
            new ResearchResult { Url = "https://example.com", Title = "Example", Snippet = "snip" },
        };

        var enriched = await svc.EnrichSourcesAsync(input);

        Assert.Single(enriched);
        Assert.Equal("https://example.com", enriched[0].Url);
        Assert.Equal("Example", enriched[0].Title);
        Assert.Equal(string.Empty, enriched[0].Preview);
    }

    [Fact]
    public async Task EnrichSourcesAsync_ValidPageHtml_PopulatesPreview()
    {
        const string pageHtml =
            "<html><body><p>AngleSharp extracted this text correctly and it is long enough.</p></body></html>";

        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(pageHtml),
            });
        var client = new HttpClient(handler);
        var svc = new LiveResearchService(client);

        var input = new[]
        {
            new ResearchResult { Url = "https://example.com", Title = "Example", Snippet = "snip" },
        };

        var enriched = await svc.EnrichSourcesAsync(input);

        Assert.Single(enriched);
        Assert.Contains("AngleSharp extracted this text correctly", enriched[0].Preview);
    }

    [Fact]
    public async Task EnrichSourcesAsync_ResultWithEmptyUrl_PreviewRemainsEmpty()
    {
        var svc = new LiveResearchService();
        var input = new[] { new ResearchResult { Url = string.Empty, Title = "No URL" } };

        var enriched = await svc.EnrichSourcesAsync(input);

        Assert.Single(enriched);
        Assert.Equal(string.Empty, enriched[0].Preview);
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal <see cref="HttpMessageHandler"/> that delegates every request to
/// a caller-supplied factory, making it easy to control responses in tests.
/// </summary>
file sealed class FakeHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(responseFactory(request));
    }
}
