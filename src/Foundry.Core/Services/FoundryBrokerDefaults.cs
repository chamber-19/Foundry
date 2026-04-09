namespace Foundry.Services;

public static class FoundryBrokerDefaults
{
    public const string Host = "127.0.0.1";
    public const int Port = 57420;
    public const string Scheme = "http";

    public static string BuildBaseUrl(string? host = null, int? port = null)
    {
        var resolvedHost = string.IsNullOrWhiteSpace(host) ? Host : host.Trim();
        var resolvedPort = port is null or <= 0 ? Port : port.Value;
        return $"{Scheme}://{resolvedHost}:{resolvedPort}";
    }
}

public sealed class FoundryBrokerRuntimeMetadata
{
    public string Host { get; init; } = FoundryBrokerDefaults.Host;
    public int Port { get; init; } = FoundryBrokerDefaults.Port;
    public string BaseUrl { get; init; } = FoundryBrokerDefaults.BuildBaseUrl();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public bool LoopbackOnly { get; init; } = true;
}
