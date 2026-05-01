using System.Net;
using Foundry.Services;
using Xunit;

namespace Foundry.Core.Tests;

public sealed class ResiliencePipelineTests
{
    /// <summary>
    /// A 404 from Ollama means the model is not found — this is not transient and
    /// must NOT be retried. The pipeline should propagate the exception immediately.
    /// </summary>
    [Fact]
    public async Task OllamaPipeline_DoesNotRetry_On404()
    {
        var pipeline = FoundryResiliencePipelines.BuildOllamaPipeline();
        var callCount = 0;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await pipeline.ExecuteAsync(_ =>
            {
                callCount++;
                throw new HttpRequestException("Not Found", null, HttpStatusCode.NotFound);
            });
        });

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal(1, callCount);
    }

    /// <summary>
    /// A 400 Bad Request from Ollama is a client error and must NOT be retried.
    /// </summary>
    [Fact]
    public async Task OllamaPipeline_DoesNotRetry_On400()
    {
        var pipeline = FoundryResiliencePipelines.BuildOllamaPipeline();
        var callCount = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await pipeline.ExecuteAsync(_ =>
            {
                callCount++;
                throw new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest);
            });
        });

        Assert.Equal(1, callCount);
    }

    /// <summary>
    /// A connection-level failure (no status code) IS transient and SHOULD be retried.
    /// The pipeline retries up to 3 times so the total call count should be 4 (1 initial + 3 retries).
    /// </summary>
    [Fact]
    public async Task OllamaPipeline_Retries_On_ConnectionFailure()
    {
        var pipeline = FoundryResiliencePipelines.BuildOllamaPipeline();
        var callCount = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await pipeline.ExecuteAsync(_ =>
            {
                callCount++;
                // No StatusCode — simulates a TCP-level connection refused / network error
                throw new HttpRequestException("Connection refused");
            });
        });

        Assert.Equal(4, callCount); // 1 initial + 3 retries
    }

    /// <summary>
    /// A 500 server error IS transient and SHOULD be retried.
    /// </summary>
    [Fact]
    public async Task OllamaPipeline_Retries_On500()
    {
        var pipeline = FoundryResiliencePipelines.BuildOllamaPipeline();
        var callCount = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await pipeline.ExecuteAsync(_ =>
            {
                callCount++;
                throw new HttpRequestException("Internal Server Error", null, HttpStatusCode.InternalServerError);
            });
        });

        Assert.Equal(4, callCount); // 1 initial + 3 retries
    }
}
