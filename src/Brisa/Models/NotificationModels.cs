namespace Brisa.Models;

public enum NotificationKind { Announcement, Maintenance, Release, Security }

public sealed record NotificationPublicKey(string Kid, int Epoch, byte[] SpkiBytes, bool Enabled = true);

public sealed record VerifiedNotificationItem(
    string Id,
    NotificationKind Kind,
    string Title,
    string Body,
    DateTimeOffset PublishedAt,
    DateTimeOffset ExpiresAt,
    string? MinVersion,
    string? MaxVersion,
    Uri? ActionUrl,
    string? ActionLabel,
    string? TitlePtBr = null,
    string? BodyPtBr = null,
    string? ActionLabelPtBr = null);

public sealed record NotificationDisplayItem(
    string Id,
    NotificationKind Kind,
    string Title,
    string Body,
    DateTimeOffset PublishedAt,
    Uri? ActionUrl,
    string? ActionLabel,
    bool IsUnread,
    bool IsSessionFresh);

public sealed record PinnedUpdateItem(
    Services.UpdatePhase Phase,
    string? Version,
    int? Progress,
    string StatusMessage,
    bool CanInstallAndRestart);

public sealed record NotificationSnapshot(
    int UnreadApiCount,
    int BadgeCount,
    PinnedUpdateItem? PinnedUpdate,
    IReadOnlyList<NotificationDisplayItem> Items)
{
    public static NotificationSnapshot Empty { get; } = new(0, 0, null, Array.Empty<NotificationDisplayItem>());
}

/// <summary>Highest signed position this install has accepted. Clock-independent rollback guard.</summary>
public sealed record NotificationHighWater(int Epoch, long Seq, DateTimeOffset? PublishedAtUtc)
{
    public static NotificationHighWater None { get; } = new(0, 0, null);
}

public sealed record VerifiedManifest(int Epoch, long Seq, DateTimeOffset PublishedAt, IReadOnlyList<VerifiedNotificationItem> Items);

public sealed record NotificationVerification(bool Accepted, string? Reason, VerifiedManifest? Manifest)
{
    public static NotificationVerification Reject(string reason) => new(false, reason, null);
}
