using DailyDesk.Models;

namespace DailyDesk.Services;

/// <summary>
/// Coordinates research operations — research jobs, watchlist execution, and
/// knowledge enrichment.
/// Encapsulates the domain logic that would otherwise be buried inside
/// <see cref="OfficeBrokerOrchestrator"/>, enabling isolated unit testing of each
/// step without constructing the full orchestrator graph.
/// </summary>
public sealed class ResearchCoordinator
{
    private readonly OperatorMemoryStore _operatorMemoryStore;

    public ResearchCoordinator(OperatorMemoryStore operatorMemoryStore)
    {
        _operatorMemoryStore = operatorMemoryStore;
    }

    // -------------------------------------------------------------------------
    // Isolated watchlist logic
    // -------------------------------------------------------------------------

    /// <summary>
    /// Locates a <see cref="ResearchWatchlist"/> by its <paramref name="id"/>
    /// using case-insensitive comparison, or returns <c>null</c> when not found.
    /// </summary>
    public static ResearchWatchlist? FindWatchlist(
        IReadOnlyList<ResearchWatchlist> watchlists,
        string id
    ) =>
        watchlists.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// Validates that <paramref name="watchlistId"/> is a non-empty string.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="watchlistId"/> is <c>null</c> or whitespace.
    /// </exception>
    public static void ValidateWatchlistId(string? watchlistId)
    {
        if (string.IsNullOrWhiteSpace(watchlistId))
        {
            throw new ArgumentException(
                "Watchlist id is required.",
                nameof(watchlistId)
            );
        }
    }

    /// <summary>
    /// Ensures that <paramref name="watchlist"/> exists and is enabled for execution.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="watchlist"/> is <c>null</c> (not found) or disabled.
    /// </exception>
    public static void EnsureWatchlistCanRun(ResearchWatchlist? watchlist, string watchlistId)
    {
        if (watchlist is null)
        {
            throw new InvalidOperationException($"Watchlist '{watchlistId}' was not found.");
        }

        if (!watchlist.IsEnabled)
        {
            throw new InvalidOperationException(
                $"Watchlist '{watchlist.Topic}' is disabled and cannot be run."
            );
        }
    }

    /// <summary>
    /// Returns a new list of watchlists with <c>LastRunAt</c> updated for the
    /// watchlist that matches <paramref name="watchlistId"/>.
    /// Other entries are returned unchanged.
    /// </summary>
    public static IReadOnlyList<ResearchWatchlist> UpdateWatchlistLastRunAt(
        IReadOnlyList<ResearchWatchlist> watchlists,
        string watchlistId,
        DateTimeOffset runAt
    ) =>
        watchlists
            .Select(item => new ResearchWatchlist
            {
                Id = item.Id,
                Topic = item.Topic,
                Query = item.Query,
                Frequency = item.Frequency,
                PreferredPerspective = item.PreferredPerspective,
                SaveToKnowledgeDefault = item.SaveToKnowledgeDefault,
                IsEnabled = item.IsEnabled,
                LastRunAt = item.Id.Equals(watchlistId, StringComparison.OrdinalIgnoreCase)
                    ? runAt
                    : item.LastRunAt,
            })
            .ToList();

    // -------------------------------------------------------------------------
    // Backend service integration
    // -------------------------------------------------------------------------

    /// <summary>
    /// Persists the updated watchlist collection to <see cref="OperatorMemoryStore"/>
    /// and returns the updated operator memory state.
    /// </summary>
    public Task<OperatorMemoryState> SaveWatchlistsAsync(
        IReadOnlyList<ResearchWatchlist> watchlists,
        CancellationToken cancellationToken = default
    ) => _operatorMemoryStore.SaveWatchlistsAsync(watchlists, cancellationToken);

    /// <summary>
    /// Loads the current operator memory state from <see cref="OperatorMemoryStore"/>.
    /// </summary>
    public Task<OperatorMemoryState> LoadMemoryStateAsync(
        CancellationToken cancellationToken = default
    ) => _operatorMemoryStore.LoadAsync(cancellationToken);
}
