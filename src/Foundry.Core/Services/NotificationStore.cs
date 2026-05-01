using Foundry.Models;
using LiteDB;

namespace Foundry.Services;

public sealed class NotificationStore
{
    private readonly FoundryDatabase _database;

    public NotificationStore(FoundryDatabase database)
    {
        _database = database;
    }

    public NotificationUpsertResult Upsert(FoundryNotification incoming)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incoming.DedupeKey);

        var existing = _database.Notifications.FindOne(x => x.DedupeKey == incoming.DedupeKey);
        if (existing is null)
        {
            incoming.Id = string.IsNullOrWhiteSpace(incoming.Id)
                ? Guid.NewGuid().ToString("N")
                : incoming.Id;
            incoming.CreatedAt = DateTimeOffset.UtcNow;
            incoming.UpdatedAt = incoming.CreatedAt;
            _database.Notifications.Insert(incoming);
            return new NotificationUpsertResult
            {
                Notification = incoming,
                Created = true,
                Updated = false,
            };
        }

        var changed =
            !StringComparer.Ordinal.Equals(existing.Title, incoming.Title) ||
            !StringComparer.Ordinal.Equals(existing.Body, incoming.Body) ||
            !StringComparer.Ordinal.Equals(existing.Category, incoming.Category) ||
            !StringComparer.Ordinal.Equals(existing.Severity, incoming.Severity) ||
            !StringComparer.Ordinal.Equals(existing.SourceUrl, incoming.SourceUrl) ||
            incoming.EventUpdatedAt.ToUnixTimeMilliseconds() > existing.EventUpdatedAt.ToUnixTimeMilliseconds();

        if (!changed)
        {
            return new NotificationUpsertResult
            {
                Notification = existing,
                Created = false,
                Updated = false,
            };
        }

        existing.Title = incoming.Title;
        existing.Body = incoming.Body;
        existing.Category = incoming.Category;
        existing.Severity = incoming.Severity;
        existing.Source = incoming.Source;
        existing.SourceUrl = incoming.SourceUrl;
        existing.Repository = incoming.Repository;
        existing.EventUpdatedAt = incoming.EventUpdatedAt;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        existing.DeliveredAt = null;
        existing.DeliveredTo = null;

        _database.Notifications.Update(existing);
        return new NotificationUpsertResult
        {
            Notification = existing,
            Created = false,
            Updated = true,
        };
    }

    public IReadOnlyList<FoundryNotification> List(bool pendingOnly = false, int limit = 50)
    {
        var safeLimit = Math.Clamp(limit, 1, 100);
        var query = pendingOnly
            ? _database.Notifications.Query().Where(x => x.DeliveredAt == null)
            : _database.Notifications.Query();

        return query
            .OrderByDescending(x => x.UpdatedAt)
            .Limit(safeLimit)
            .ToList();
    }

    public FoundryNotification? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _database.Notifications.FindById(new BsonValue(id));
    }

    public IReadOnlyList<FoundryNotification> GetPending() =>
        _database.Notifications
            .Query()
            .Where(x => x.DeliveredAt == null && x.DeliveryAttempts < 3)
            .OrderBy(x => x.CreatedAt)
            .ToList();

    public FoundryNotification? IncrementDeliveryAttempts(string id)
    {
        var notification = GetById(id);
        if (notification is null)
        {
            return null;
        }

        notification.DeliveryAttempts++;
        _database.Notifications.Update(notification);
        return notification;
    }

    public FoundryNotification? MarkDelivered(string id, string? deliveredTo = null)
    {
        var notification = GetById(id);
        if (notification is null)
        {
            return null;
        }

        notification.DeliveredAt = DateTimeOffset.UtcNow;
        notification.DeliveredTo = string.IsNullOrWhiteSpace(deliveredTo)
            ? null
            : deliveredTo.Trim();
        _database.Notifications.Update(notification);
        return notification;
    }
}
