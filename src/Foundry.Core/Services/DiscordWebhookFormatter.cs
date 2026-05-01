using Foundry.Models;

namespace Foundry.Services;

/// <summary>
/// Builds Discord webhook embed payloads from <see cref="FoundryNotification"/> records.
/// Pure static; no I/O. Separated so it can be unit-tested without HTTP.
/// </summary>
public static class DiscordWebhookFormatter
{
    public static DiscordWebhookPayload BuildPayload(FoundryNotification notification)
    {
        var embed = new DiscordEmbed
        {
            Title = notification.Title,
            Description = string.IsNullOrWhiteSpace(notification.Body) ? null : notification.Body,
            Color = CategoryToColor(notification.Category),
            Fields = BuildFields(notification),
            Footer = string.IsNullOrWhiteSpace(notification.SourceUrl)
                ? null
                : new DiscordEmbedFooter { Text = notification.SourceUrl },
        };
        return new DiscordWebhookPayload { Embeds = [embed] };
    }

    public static int CategoryToColor(string category) => category switch
    {
        DependencyNotificationCategory.Info => 3447003,
        DependencyNotificationCategory.NeedsReview => 15844367,
        DependencyNotificationCategory.Risky => 15105570,
        DependencyNotificationCategory.Blocked => 10038562,
        _ => 8421504,
    };

    private static List<DiscordEmbedField>? BuildFields(FoundryNotification notification)
    {
        var fields = new List<DiscordEmbedField>(3);
        if (!string.IsNullOrWhiteSpace(notification.PackageName))
            fields.Add(new DiscordEmbedField { Name = "Package", Value = notification.PackageName, Inline = true });
        if (!string.IsNullOrWhiteSpace(notification.Ecosystem))
            fields.Add(new DiscordEmbedField { Name = "Ecosystem", Value = notification.Ecosystem, Inline = true });
        if (!string.IsNullOrWhiteSpace(notification.Severity))
            fields.Add(new DiscordEmbedField { Name = "Type", Value = notification.Severity, Inline = true });
        return fields.Count > 0 ? fields : null;
    }
}

public sealed class DiscordWebhookPayload
{
    public List<DiscordEmbed> Embeds { get; init; } = new();
}

public sealed class DiscordEmbed
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int Color { get; init; }
    public List<DiscordEmbedField>? Fields { get; init; }
    public DiscordEmbedFooter? Footer { get; init; }
}

public sealed class DiscordEmbedField
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public bool Inline { get; init; }
}

public sealed class DiscordEmbedFooter
{
    public string Text { get; init; } = string.Empty;
}
