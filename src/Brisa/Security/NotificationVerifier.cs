using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Brisa.Models;

namespace Brisa.Security;

/// <summary>
/// Accepts a feed only if every check passes, in this order: size, JSON shape with no duplicate
/// keys, a trusted key id, an ECDSA P-256 signature over the raw manifest bytes, then the
/// manifest itself. One bad item rejects the whole feed. Nothing is partially accepted.
/// </summary>
public sealed class NotificationVerifier
{
    public const int MaxEnvelopeBytes = 65_536;
    public const int MaxManifestBytes = 63_488;
    public const int MaxItems = 20;
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(15);
    private const string Domain = "brisa-notifications-v1\n";
    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    private static readonly Regex KidPattern = new(@"^[a-z0-9][a-z0-9-]{2,31}\z", RegexOptions.CultureInvariant);
    private static readonly Regex SigPattern = new(@"^[A-Za-z0-9_-]{86}\z", RegexOptions.CultureInvariant);
    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9-]{2,62}\z", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:alpha|beta|rc)\.(0|[1-9]\d*))?\z",
        RegexOptions.CultureInvariant);
    private static readonly string[] EnvelopeKeys = ["schemaVersion", "alg", "kid", "sig", "manifest"];
    private static readonly string[] ManifestKeys = ["schemaVersion", "epoch", "seq", "publishedAt", "items"];
    private static readonly string[] ItemKeys =
        ["id", "kind", "title", "body", "publishedAt", "expiresAt", "minVersion", "maxVersion", "actionUrl", "actionLabel", "i18n"];
    private static readonly string[] I18nKeys = ["title", "body", "actionLabel"];
    private const string Timestamp = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private readonly IReadOnlyDictionary<string, NotificationPublicKey> _keys;

    public NotificationVerifier(IReadOnlyDictionary<string, NotificationPublicKey> trustedKeys) => _keys = trustedKeys;

    public static NotificationVerifier Production { get; } = new(NotificationTrustAnchors.Production);

    public NotificationVerification Verify(ReadOnlySpan<byte> envelope, NotificationHighWater highWater)
    {
        try { return VerifyCore(envelope.ToArray(), highWater); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException
            or CryptographicException or ArgumentException or OverflowException)
        {
            return NotificationVerification.Reject("BAD_JSON");
        }
    }

    private NotificationVerification VerifyCore(byte[] bytes, NotificationHighWater highWater)
    {
        if (bytes.Length == 0 || bytes.Length > MaxEnvelopeBytes) return NotificationVerification.Reject("TOO_LARGE");
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return NotificationVerification.Reject("BOM");
        if (!HasNoDuplicateKeys(bytes)) return NotificationVerification.Reject("DUPLICATE_KEY");

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            MaxDepth = 8,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !HasExactKeys(root, EnvelopeKeys)) return NotificationVerification.Reject("BAD_ENVELOPE");
        if (!IsInt(root.GetProperty("schemaVersion"), out var schema) || schema != 1) return NotificationVerification.Reject("BAD_SCHEMA");
        if (!IsString(root.GetProperty("alg"), out var alg) || alg != "ES256") return NotificationVerification.Reject("BAD_ALG");
        if (!IsString(root.GetProperty("kid"), out var kid) || !KidPattern.IsMatch(kid)) return NotificationVerification.Reject("BAD_KID");
        if (!IsString(root.GetProperty("sig"), out var sig) || !SigPattern.IsMatch(sig)) return NotificationVerification.Reject("BAD_SIG_FORMAT");
        var manifestElement = root.GetProperty("manifest");
        if (manifestElement.ValueKind != JsonValueKind.Object) return NotificationVerification.Reject("BAD_ENVELOPE");

        if (!_keys.TryGetValue(kid, out var key) || !key.Enabled) return NotificationVerification.Reject("UNKNOWN_KID");
        var signature = DecodeBase64Url(sig);
        if (signature is null || signature.Length != 64) return NotificationVerification.Reject("BAD_SIG_FORMAT");

        // The signature covers the exact bytes on the wire, not a re-serialized copy.
        var rawManifest = manifestElement.GetRawText();
        if (rawManifest.Length < 2 || rawManifest[0] != '{' || rawManifest[^1] != '}') return NotificationVerification.Reject("BAD_ENVELOPE");
        if (Encoding.UTF8.GetByteCount(rawManifest) > MaxManifestBytes) return NotificationVerification.Reject("TOO_LARGE");
        var preimage = Encoding.UTF8.GetBytes(Domain + kid + "\n" + rawManifest);

        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportSubjectPublicKeyInfo(key.SpkiBytes, out var read);
            if (read != key.SpkiBytes.Length || ecdsa.KeySize != 256) return NotificationVerification.Reject("BAD_KEY");
            if (ecdsa.ExportParameters(false).Curve.Oid?.Value != NistP256Oid) return NotificationVerification.Reject("BAD_KEY");
            if (!ecdsa.VerifyData(preimage, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return NotificationVerification.Reject("BAD_SIGNATURE");
        }

        return ReadManifest(manifestElement, key, highWater);
    }

    private static NotificationVerification ReadManifest(JsonElement manifest, NotificationPublicKey key, NotificationHighWater highWater)
    {
        if (!HasExactKeys(manifest, ManifestKeys)) return NotificationVerification.Reject("BAD_MANIFEST");
        if (!IsInt(manifest.GetProperty("schemaVersion"), out var schema) || schema != 1) return NotificationVerification.Reject("BAD_SCHEMA");
        if (!IsInt(manifest.GetProperty("epoch"), out var epoch) || epoch < 1 || epoch > 1000 || epoch != key.Epoch)
            return NotificationVerification.Reject("BAD_EPOCH");
        if (manifest.GetProperty("seq").ValueKind != JsonValueKind.Number || !manifest.GetProperty("seq").TryGetInt64(out var seq)
            || seq < 1 || seq > 9_007_199_254_740_991L) return NotificationVerification.Reject("BAD_SEQ");
        if (!IsTimestamp(manifest.GetProperty("publishedAt"), out var publishedAt)) return NotificationVerification.Reject("BAD_TIME");

        if (epoch < highWater.Epoch) return NotificationVerification.Reject("STALE_EPOCH");
        if (epoch == highWater.Epoch && seq < highWater.Seq) return NotificationVerification.Reject("ROLLBACK_SEQ");

        var itemsElement = manifest.GetProperty("items");
        if (itemsElement.ValueKind != JsonValueKind.Array || itemsElement.GetArrayLength() > MaxItems) return NotificationVerification.Reject("BAD_ITEMS");
        var items = new List<VerifiedNotificationItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in itemsElement.EnumerateArray())
        {
            var item = ReadItem(element);
            if (item is null || !ids.Add(item.Id)) return NotificationVerification.Reject("BAD_ITEM");
            items.Add(item);
        }
        return new NotificationVerification(true, null, new VerifiedManifest(epoch, seq, publishedAt, items));
    }

    private static VerifiedNotificationItem? ReadItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !HasOnlyKeys(item, ItemKeys)) return null;
        if (!TryString(item, "id", out var id) || !IdPattern.IsMatch(id)
            || id.StartsWith("update-", StringComparison.Ordinal) || id.StartsWith("sys-", StringComparison.Ordinal)) return null;
        if (!TryString(item, "kind", out var kindText) || !TryKind(kindText, out var kind)) return null;
        if (!TryText(item, "title", 100, multiline: false, out var title)) return null;
        if (!TryText(item, "body", 400, multiline: true, out var body)) return null;
        if (!item.TryGetProperty("publishedAt", out var pubElement) || !IsTimestamp(pubElement, out var published)) return null;
        if (!item.TryGetProperty("expiresAt", out var expElement) || !IsTimestamp(expElement, out var expires)) return null;
        if (expires <= published || expires > published.AddDays(90)) return null;

        string? min = null, max = null;
        if (item.TryGetProperty("minVersion", out var minElement) && !TryVersion(minElement, out min)) return null;
        if (item.TryGetProperty("maxVersion", out var maxElement) && !TryVersion(maxElement, out max)) return null;
        if (min is not null && max is not null
            && Velopack.SemanticVersion.TryParse(min, out var low) && Velopack.SemanticVersion.TryParse(max, out var high) && low > high) return null;

        Uri? action = null;
        if (item.TryGetProperty("actionUrl", out var urlElement))
        {
            if (!IsString(urlElement, out var rawUrl) || !NotificationUrlPolicy.TryValidate(rawUrl, out action)) return null;
        }
        string? label = null;
        if (item.TryGetProperty("actionLabel", out _))
        {
            if (action is null || !TryText(item, "actionLabel", 40, multiline: false, out label)) return null;
        }

        string? titlePt = null, bodyPt = null, labelPt = null;
        if (item.TryGetProperty("i18n", out var i18n))
        {
            if (i18n.ValueKind != JsonValueKind.Object || !HasOnlyKeys(i18n, ["pt-BR"])) return null;
            if (i18n.TryGetProperty("pt-BR", out var pt))
            {
                if (pt.ValueKind != JsonValueKind.Object || !HasOnlyKeys(pt, I18nKeys)) return null;
                if (pt.TryGetProperty("title", out _) && !TryText(pt, "title", 100, false, out titlePt)) return null;
                if (pt.TryGetProperty("body", out _) && !TryText(pt, "body", 400, true, out bodyPt)) return null;
                if (pt.TryGetProperty("actionLabel", out _) && (action is null || !TryText(pt, "actionLabel", 40, false, out labelPt))) return null;
            }
        }
        return new VerifiedNotificationItem(id, kind, title, body, published, expires, min, max, action, label, titlePt, bodyPt, labelPt);
    }

    /// <summary>Items still worth showing, using the signed clock floor so a wrong PC clock cannot revive them.</summary>
    public static IReadOnlyList<VerifiedNotificationItem> SelectActive(
        VerifiedManifest manifest, NotificationHighWater highWater, DateTimeOffset utcNow, string? appVersion)
    {
        var signedFloor = highWater.PublishedAtUtc is { } high && high > manifest.PublishedAt ? high : manifest.PublishedAt;
        var wallAdjusted = utcNow - ClockSkewTolerance;
        var effectiveNow = wallAdjusted > signedFloor ? wallAdjusted : signedFloor;
        var current = appVersion is not null && Velopack.SemanticVersion.TryParse(appVersion, out var parsed) ? parsed : null;
        var active = new List<VerifiedNotificationItem>();
        foreach (var item in manifest.Items)
        {
            if (effectiveNow >= item.ExpiresAt) continue;
            if (!InVersionRange(item, current)) continue;
            active.Add(item);
        }
        return active;
    }

    private static bool InVersionRange(VerifiedNotificationItem item, Velopack.SemanticVersion? current)
    {
        if (item.MinVersion is null && item.MaxVersion is null) return true;
        // An unreadable app version only sees items with no version bounds.
        if (current is null) return false;
        if (item.MinVersion is not null && Velopack.SemanticVersion.TryParse(item.MinVersion, out var min) && current < min) return false;
        if (item.MaxVersion is not null && Velopack.SemanticVersion.TryParse(item.MaxVersion, out var max) && current > max) return false;
        return true;
    }

    public static bool HasNoDuplicateKeys(byte[] json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        var scopes = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: scopes.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                case JsonTokenType.StartArray: scopes.Push(null); break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray: scopes.Pop(); break;
                case JsonTokenType.PropertyName:
                    if (scopes.Peek() is { } names && !names.Add(reader.GetString()!)) return false;
                    break;
            }
        }
        return true;
    }

    public static bool ContainsForbiddenCodepoint(string text, bool allowNewline)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return true;
                i++;
                continue;
            }
            if (char.IsLowSurrogate(c)) return true;
            if (c == '\n') { if (!allowNewline) return true; continue; }
            var code = (int)c;
            if (code <= 0x1F || code is >= 0x7F and <= 0x9F) return true;
            if (code is 0x061C or 0x200E or 0x200F or 0x2028 or 0x2029 or 0xFEFF) return true;
            if (code is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069) return true;
        }
        return false;
    }

    private static bool TryText(JsonElement owner, string name, int maxScalars, bool multiline, out string value)
    {
        value = "";
        if (!owner.TryGetProperty(name, out var element) || !IsString(element, out var raw)) return false;
        // Padding is refused, not repaired: the text that was signed is the text that is shown.
        // The character check runs on the raw string, so nothing slips out by being trimmed away.
        if (raw.Trim() != raw || ContainsForbiddenCodepoint(raw, multiline)) return false;
        var length = 0;
        foreach (var _ in raw.EnumerateRunes()) length++;
        if (length < 1 || length > maxScalars) return false;
        if (multiline && raw.Count(c => c == '\n') > 3) return false;
        value = raw;
        return true;
    }

    private static bool TryVersion(JsonElement element, out string? value)
    {
        value = null;
        if (!IsString(element, out var text) || !VersionPattern.IsMatch(text) || !Velopack.SemanticVersion.TryParse(text, out _)) return false;
        value = text;
        return true;
    }

    private static bool TryKind(string text, out NotificationKind kind)
    {
        kind = text switch
        {
            "announcement" => NotificationKind.Announcement,
            "maintenance" => NotificationKind.Maintenance,
            "release" => NotificationKind.Release,
            "security" => NotificationKind.Security,
            _ => (NotificationKind)(-1),
        };
        return (int)kind >= 0;
    }

    private static bool TryString(JsonElement owner, string name, out string value)
    {
        value = "";
        return owner.TryGetProperty(name, out var element) && IsString(element, out value);
    }

    private static bool IsString(JsonElement element, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? "";
        return true;
    }

    private static bool IsInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool IsTimestamp(JsonElement element, out DateTimeOffset value)
    {
        value = default;
        return IsString(element, out var text) && text.Length == 20
            && DateTimeOffset.TryParseExact(text, Timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    private static bool HasExactKeys(JsonElement element, string[] expected)
    {
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(expected, property.Name) < 0) return false;
            count++;
        }
        return count == expected.Length;
    }

    private static bool HasOnlyKeys(JsonElement element, string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
            if (Array.IndexOf(allowed, property.Name) < 0) return false;
        return true;
    }

    private static byte[]? DecodeBase64Url(string text)
    {
        var standard = text.Replace('-', '+').Replace('_', '/') + "==";
        var bytes = Convert.FromBase64String(standard);
        // Reject non-canonical encodings so one signature has exactly one spelling.
        var again = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return again == text ? bytes : null;
    }
}
