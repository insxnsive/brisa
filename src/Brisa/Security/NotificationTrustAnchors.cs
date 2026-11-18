using Brisa.Models;

namespace Brisa.Security;

/// <summary>
/// Public material only. The feed is signed offline; these constants let the app check that
/// signature and nothing else. Rotating a key means shipping a new release with a higher epoch.
/// </summary>
public static class NotificationTrustAnchors
{
    public const string FeedUri = "https://brisa-notify.insxnsive.works/v1/notifications.json";

    public static IReadOnlyList<NotificationPublicKey> ProductionKeys { get; } =
    [
        new NotificationPublicKey(
            "brisa-2026-k1",
            1,
            Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEA81c4Qy7srHgHtIMuXiuUkMaAajAjnlD66i2Gahpc4u9UiNWVF0i6YqMpJtfI0gUv+3JAitsejBKukMvEWeFig=="),
            Enabled: true),
    ];

    public static IReadOnlyDictionary<string, NotificationPublicKey> Production { get; } =
        ProductionKeys.ToDictionary(key => key.Kid, StringComparer.Ordinal);
}
