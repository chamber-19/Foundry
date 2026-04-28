using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundry.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Services;

public interface IGitHubDependencyClient
{
    Task<IReadOnlyList<DependencyPullRequest>> ListDependabotPullRequestsAsync(
        string repository,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DependencyAlert>> ListDependabotAlertsAsync(
        string repository,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubDependencyClient : IGitHubDependencyClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly FoundrySettings _settings;
    private readonly ILogger<GitHubDependencyClient> _logger;

    public GitHubDependencyClient(
        FoundrySettings settings,
        HttpClient? httpClient = null,
        ILogger<GitHubDependencyClient>? logger = null)
    {
        _settings = settings;
        _logger = logger ?? NullLogger<GitHubDependencyClient>.Instance;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri("https://api.github.com/");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FoundryDependencyMonitor/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Remove("X-GitHub-Api-Version");
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        var token = ResolveGitHubToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<IReadOnlyList<DependencyPullRequest>> ListDependabotPullRequestsAsync(
        string repository,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateRepository(repository, out var normalized))
        {
            _logger.LogWarning("Skipping invalid GitHub repository value: {Repository}", repository);
            return Array.Empty<DependencyPullRequest>();
        }

        try
        {
            var response = await _httpClient.GetAsync(
                $"repos/{normalized}/pulls?state=open&per_page=100",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await LogGitHubFailureAsync(response, normalized, "pull requests", cancellationToken);
                return Array.Empty<DependencyPullRequest>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var prs = await JsonSerializer.DeserializeAsync<List<PullRequestDto>>(stream, JsonOptions, cancellationToken)
                ?? [];

            return prs
                .Where(pr => string.Equals(pr.User?.Login, "dependabot[bot]", StringComparison.OrdinalIgnoreCase))
                .Select(pr => new DependencyPullRequest
                {
                    Repository = normalized,
                    Number = pr.Number,
                    Title = pr.Title ?? string.Empty,
                    Body = pr.Body ?? string.Empty,
                    State = pr.State ?? string.Empty,
                    Author = pr.User?.Login ?? string.Empty,
                    HtmlUrl = pr.HtmlUrl ?? string.Empty,
                    CreatedAt = pr.CreatedAt,
                    UpdatedAt = pr.UpdatedAt,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to query Dependabot pull requests for {Repository}.", normalized);
            return Array.Empty<DependencyPullRequest>();
        }
    }

    public async Task<IReadOnlyList<DependencyAlert>> ListDependabotAlertsAsync(
        string repository,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateRepository(repository, out var normalized))
        {
            _logger.LogWarning("Skipping invalid GitHub repository value: {Repository}", repository);
            return Array.Empty<DependencyAlert>();
        }

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
        {
            _logger.LogInformation(
                "No GitHub token configured; skipping Dependabot alerts for {Repository}.",
                normalized);
            return Array.Empty<DependencyAlert>();
        }

        try
        {
            var response = await _httpClient.GetAsync(
                $"repos/{normalized}/dependabot/alerts?state=open&per_page=100",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await LogGitHubFailureAsync(response, normalized, "Dependabot alerts", cancellationToken);
                return Array.Empty<DependencyAlert>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var alerts = await JsonSerializer.DeserializeAsync<List<DependabotAlertDto>>(stream, JsonOptions, cancellationToken)
                ?? [];

            return alerts.Select(alert => new DependencyAlert
                {
                    Repository = normalized,
                    AlertId = alert.Number?.ToString() ?? alert.HtmlUrl ?? Guid.NewGuid().ToString("N"),
                    PackageName = alert.Dependency?.Package?.Name ?? string.Empty,
                    Ecosystem = alert.Dependency?.Package?.Ecosystem ?? string.Empty,
                    Severity = alert.SecurityAdvisory?.Severity ?? "unknown",
                    State = alert.State ?? string.Empty,
                    AdvisorySummary = alert.SecurityAdvisory?.Summary ?? string.Empty,
                    VulnerableRequirements = alert.SecurityVulnerability?.VulnerableVersionRange ?? string.Empty,
                    PatchedVersion = alert.SecurityVulnerability?.FirstPatchedVersion?.Identifier ?? string.Empty,
                    HtmlUrl = alert.HtmlUrl ?? string.Empty,
                    CreatedAt = alert.CreatedAt,
                    UpdatedAt = alert.UpdatedAt,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to query Dependabot alerts for {Repository}.", normalized);
            return Array.Empty<DependencyAlert>();
        }
    }

    private string? ResolveGitHubToken()
    {
        var configured = Environment.GetEnvironmentVariable(_settings.GitHubTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var foundryToken = Environment.GetEnvironmentVariable("FOUNDRY_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(foundryToken))
        {
            return foundryToken.Trim();
        }

        return null;
    }

    private static bool TryValidateRepository(string repository, out string normalized)
    {
        normalized = repository.Trim().Trim('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 &&
               parts.All(part => part.Length > 0 && !part.Contains("..", StringComparison.Ordinal));
    }

    private async Task LogGitHubFailureAsync(
        HttpResponseMessage response,
        string repository,
        string resource,
        CancellationToken cancellationToken)
    {
        var body = string.Empty;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 300)
            {
                body = body[..300];
            }
        }

        _logger.LogWarning(
            "GitHub {Resource} query failed for {Repository}: {StatusCode} {Body}",
            resource,
            repository,
            (int)response.StatusCode,
            body);
    }

    private sealed class PullRequestDto
    {
        [JsonPropertyName("number")]
        public int Number { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; init; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; init; }

        [JsonPropertyName("user")]
        public GitHubUserDto? User { get; init; }
    }

    private sealed class GitHubUserDto
    {
        [JsonPropertyName("login")]
        public string? Login { get; init; }
    }

    private sealed class DependabotAlertDto
    {
        [JsonPropertyName("number")]
        public int? Number { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; init; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; init; }

        [JsonPropertyName("dependency")]
        public AlertDependencyDto? Dependency { get; init; }

        [JsonPropertyName("security_advisory")]
        public AlertAdvisoryDto? SecurityAdvisory { get; init; }

        [JsonPropertyName("security_vulnerability")]
        public AlertVulnerabilityDto? SecurityVulnerability { get; init; }
    }

    private sealed class AlertDependencyDto
    {
        [JsonPropertyName("package")]
        public AlertPackageDto? Package { get; init; }
    }

    private sealed class AlertPackageDto
    {
        [JsonPropertyName("ecosystem")]
        public string? Ecosystem { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class AlertAdvisoryDto
    {
        [JsonPropertyName("severity")]
        public string? Severity { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }
    }

    private sealed class AlertVulnerabilityDto
    {
        [JsonPropertyName("vulnerable_version_range")]
        public string? VulnerableVersionRange { get; init; }

        [JsonPropertyName("first_patched_version")]
        public AlertPatchedVersionDto? FirstPatchedVersion { get; init; }
    }

    private sealed class AlertPatchedVersionDto
    {
        [JsonPropertyName("identifier")]
        public string? Identifier { get; init; }
    }
}
