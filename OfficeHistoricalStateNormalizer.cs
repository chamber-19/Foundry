using DailyDesk.Models;

namespace DailyDesk.Services;

public static class OfficeHistoricalStateNormalizer
{
    private static readonly string[] LegacyBaselineModels =
    [
        "qwen3:14b",
        "ALIENTELLIGENCE/electricalengineerv2:latest",
        "qwen2.5-coder:14b",
        "gemma3:12b",
    ];

    public static bool NormalizeBaselineAssertions(
        OperatorMemoryState state,
        string unifiedModel
    )
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(unifiedModel))
        {
            return false;
        }

        var changed = false;

        foreach (var thread in state.DeskThreads)
        {
            foreach (var message in thread.Messages.Where(static item => item.IsAssistant))
            {
                var normalizedContent = RewriteBaselineAssertion(message.Content, unifiedModel);
                if (!string.Equals(normalizedContent, message.Content, StringComparison.Ordinal))
                {
                    message.Content = normalizedContent;
                    changed = true;
                }
            }
        }

        foreach (var activity in state.Activities.Where(item =>
                     item.EventType.Equals("desk_chat", StringComparison.OrdinalIgnoreCase)))
        {
            var normalizedSummary = RewriteBaselineAssertion(activity.Summary, unifiedModel);
            if (!string.Equals(normalizedSummary, activity.Summary, StringComparison.Ordinal))
            {
                activity.Summary = normalizedSummary;
                changed = true;
            }
        }

        return changed;
    }

    public static string RewriteBaselineAssertion(string content, string unifiedModel)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(unifiedModel))
        {
            return content;
        }

        if (!LooksLikeBaselineAssertion(content))
        {
            return content;
        }

        var rewritten = content;
        foreach (var legacyModel in LegacyBaselineModels)
        {
            if (legacyModel.Equals(unifiedModel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rewritten = rewritten.Replace(
                $"`{legacyModel}`",
                $"`{unifiedModel}`",
                StringComparison.OrdinalIgnoreCase
            );
            rewritten = rewritten.Replace(
                legacyModel,
                unifiedModel,
                StringComparison.OrdinalIgnoreCase
            );
        }

        return rewritten;
    }

    private static bool LooksLikeBaselineAssertion(string content)
    {
        return content.Contains("Office baseline model", StringComparison.OrdinalIgnoreCase)
            && (
                content.Contains("for all roles", StringComparison.OrdinalIgnoreCase)
                || content.Contains("latest research integration", StringComparison.OrdinalIgnoreCase)
            );
    }
}
