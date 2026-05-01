using Foundry.Models;
using Foundry.Services;
using Xunit;

namespace Foundry.Core.Tests;

public sealed class DiscordWebhookFormatterTests
{
    [Theory]
    [InlineData(DependencyNotificationCategory.Info, 3447003)]
    [InlineData(DependencyNotificationCategory.NeedsReview, 15844367)]
    [InlineData(DependencyNotificationCategory.Risky, 15105570)]
    [InlineData(DependencyNotificationCategory.Blocked, 10038562)]
    public void CategoryToColor_ReturnsCorrectColor(string category, int expected)
    {
        Assert.Equal(expected, DiscordWebhookFormatter.CategoryToColor(category));
    }

    [Fact]
    public void BuildPayload_PrNotification_CorrectEmbedShape()
    {
        var notification = new FoundryNotification
        {
            Title = "Dependabot PR: chamber-19/Foundry #42",
            Body = "Bump OllamaSharp from 5.4.25 to 5.4.26\nchamber-19/Foundry: patch update for OllamaSharp.",
            Category = DependencyNotificationCategory.Info,
            PackageName = "OllamaSharp",
            Ecosystem = "nuget",
            Severity = "patch",
            SourceUrl = "https://example.test/pr/42",
        };

        var payload = DiscordWebhookFormatter.BuildPayload(notification);

        Assert.Single(payload.Embeds);
        var embed = payload.Embeds[0];
        Assert.Equal(notification.Title, embed.Title);
        Assert.Equal(notification.Body, embed.Description);
        Assert.Equal(3447003, embed.Color);
        Assert.NotNull(embed.Footer);
        Assert.Equal("https://example.test/pr/42", embed.Footer.Text);
    }

    [Fact]
    public void BuildPayload_IncludesPackageEcosystemAndTypeFields()
    {
        var notification = new FoundryNotification
        {
            Category = DependencyNotificationCategory.Risky,
            PackageName = "OllamaSharp",
            Ecosystem = "nuget",
            Severity = "major",
        };

        var embed = DiscordWebhookFormatter.BuildPayload(notification).Embeds[0];

        Assert.NotNull(embed.Fields);
        Assert.Contains(embed.Fields, f => f.Name == "Package" && f.Value == "OllamaSharp" && f.Inline);
        Assert.Contains(embed.Fields, f => f.Name == "Ecosystem" && f.Value == "nuget" && f.Inline);
        Assert.Contains(embed.Fields, f => f.Name == "Type" && f.Value == "major" && f.Inline);
    }

    [Fact]
    public void BuildPayload_EmptyPackageAndEcosystem_NullFields()
    {
        var notification = new FoundryNotification
        {
            Category = DependencyNotificationCategory.Info,
            PackageName = string.Empty,
            Ecosystem = string.Empty,
            Severity = string.Empty,
        };

        var embed = DiscordWebhookFormatter.BuildPayload(notification).Embeds[0];

        Assert.Null(embed.Fields);
    }

    [Fact]
    public void BuildPayload_EmptySourceUrl_NullFooter()
    {
        var notification = new FoundryNotification
        {
            Category = DependencyNotificationCategory.Info,
            SourceUrl = string.Empty,
        };

        var embed = DiscordWebhookFormatter.BuildPayload(notification).Embeds[0];

        Assert.Null(embed.Footer);
    }

    [Fact]
    public void BuildPayload_UnknownCategory_UsesFallbackColor()
    {
        var notification = new FoundryNotification { Category = "not-a-real-category" };
        var embed = DiscordWebhookFormatter.BuildPayload(notification).Embeds[0];
        Assert.Equal(8421504, embed.Color);
    }
}
