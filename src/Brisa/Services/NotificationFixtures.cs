using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Brisa.Models;
using Brisa.Security;

namespace Brisa.Services;

/// <summary>
/// Isolated launches only (--ui-test and --smoke-test with --notifications-fixture). The feed is
/// signed in memory with a throwaway key that the matching verifier trusts, so the real trust
/// anchor is never involved and no socket is opened.
/// </summary>
public sealed class NotificationFixture : IDisposable
{
    private readonly ECDsa _key;

    internal NotificationFixture(ECDsa key, NotificationVerifier verifier, INotificationFeedClient client)
    {
        _key = key;
        Verifier = verifier;
        Client = client;
    }

    public NotificationVerifier Verifier { get; }

    public INotificationFeedClient Client { get; }

    public void Dispose() => _key.Dispose();
}

public static class NotificationFixtures
{
    public const string Kid = "fixture-k1";
    private const string Timestamp = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const int MaxFileBytes = 49_152;

    /// <summary>"sample" builds the built-in feed; anything else is a path to a manifest JSON file.</summary>
    public static NotificationFixture? Create(string? source, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        string manifest;
        if (source.Equals("sample", StringComparison.OrdinalIgnoreCase)) manifest = SampleManifest(now);
        else
        {
            var path = Path.GetFullPath(source);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            manifest = File.ReadAllText(path, Encoding.UTF8).Trim();
        }
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trusted = new NotificationPublicKey(Kid, 1, key.ExportSubjectPublicKeyInfo());
        var verifier = new NotificationVerifier(new Dictionary<string, NotificationPublicKey> { [Kid] = trusted });
        var envelope = Encoding.UTF8.GetBytes(SignEnvelope(manifest, Kid, key));
        return new NotificationFixture(key, verifier, new FixtureNotificationFeedClient(envelope));
    }

    /// <summary>Builds the exact wire format the publisher produces: signature over "domain\nkid\nmanifest".</summary>
    public static string SignEnvelope(string rawManifest, string kid, ECDsa key)
    {
        var preimage = Encoding.UTF8.GetBytes("brisa-notifications-v1\n" + kid + "\n" + rawManifest);
        var signature = key.SignData(preimage, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var sig = Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "{\"schemaVersion\":1,\"alg\":\"ES256\",\"kid\":" + JsonSerializer.Serialize(kid)
            + ",\"sig\":" + JsonSerializer.Serialize(sig) + ",\"manifest\":" + rawManifest + "}";
    }

    public static string SampleManifest(DateTimeOffset now)
    {
        string Stamp(TimeSpan offset) => now.UtcDateTime.Add(offset).ToString(Timestamp, CultureInfo.InvariantCulture);
        var items = new object[]
        {
            new Dictionary<string, object>
            {
                ["id"] = "sample-release-notes", ["kind"] = "release",
                ["title"] = "Brisa has a new release", ["body"] = "Faster startup and a clearer route check. Read what changed before you update.",
                ["publishedAt"] = Stamp(TimeSpan.FromHours(-2)), ["expiresAt"] = Stamp(TimeSpan.FromDays(30)),
                ["actionUrl"] = "https://github.com/insxnsive/brisa/releases", ["actionLabel"] = "Release notes",
                ["i18n"] = new Dictionary<string, object> { ["pt-BR"] = new Dictionary<string, string>
                    { ["title"] = "Há uma nova versão do Brisa", ["body"] = "Inicialização mais rápida e uma verificação de rota mais clara. Veja o que mudou antes de atualizar.", ["actionLabel"] = "Notas da versão" } },
            },
            new Dictionary<string, object>
            {
                ["id"] = "sample-maintenance", ["kind"] = "maintenance",
                ["title"] = "Route maintenance tonight", ["body"] = "One route will be offline for a short window at 04:00 UTC. Pick another route if you need Discord then.",
                ["publishedAt"] = Stamp(TimeSpan.FromDays(-1)), ["expiresAt"] = Stamp(TimeSpan.FromDays(7)),
                ["i18n"] = new Dictionary<string, object> { ["pt-BR"] = new Dictionary<string, string>
                    { ["title"] = "Manutenção de rota hoje à noite", ["body"] = "Uma rota ficará fora do ar por pouco tempo às 04:00 UTC. Escolha outra rota se precisar do Discord nesse horário." } },
            },
            new Dictionary<string, object>
            {
                ["id"] = "sample-security", ["kind"] = "security",
                ["title"] = "Install updates when they are ready", ["body"] = "Updates keep the route check accurate and fix problems found since the last release.",
                ["publishedAt"] = Stamp(TimeSpan.FromDays(-3)), ["expiresAt"] = Stamp(TimeSpan.FromDays(60)),
                ["i18n"] = new Dictionary<string, object> { ["pt-BR"] = new Dictionary<string, string>
                    { ["title"] = "Instale as atualizações quando estiverem prontas", ["body"] = "As atualizações mantêm a verificação de rota precisa e corrigem problemas encontrados desde a última versão." } },
            },
            new Dictionary<string, object>
            {
                ["id"] = "sample-announcement", ["kind"] = "announcement",
                ["title"] = "Discord route check improved", ["body"] = "The check now compares more than one source before it reports a route as verified.",
                ["publishedAt"] = Stamp(TimeSpan.FromDays(-12)), ["expiresAt"] = Stamp(TimeSpan.FromDays(60)),
                ["i18n"] = new Dictionary<string, object> { ["pt-BR"] = new Dictionary<string, string>
                    { ["title"] = "Verificação de rota do Discord melhorada", ["body"] = "A verificação agora compara mais de uma fonte antes de marcar uma rota como verificada." } },
            },
        };
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schemaVersion"] = 1, ["epoch"] = 1, ["seq"] = 1,
            ["publishedAt"] = Stamp(TimeSpan.Zero), ["items"] = items,
        });
    }
}
