using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Brisa;
using Brisa.Localization;
using Brisa.Models;
using Brisa.Security;
using Brisa.Services;

/// <summary>
/// Verifier, URL policy, feed client and service checks. Every key here is an ephemeral P-256 key
/// made for the test; the production trust anchor is only checked for shape.
/// </summary>
static class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Kid = "test-key-1";

    public static (string Name, Action Run)[] All() =>
    [
        ("notifications: a genuine envelope verifies and keeps every field", () =>
        {
            using var s = new Signer();
            var item = Item(extra: ",\"minVersion\":\"1.0.0\",\"maxVersion\":\"1.0.3\",\"actionUrl\":\"https://github.com/insxnsive/brisa/releases\",\"actionLabel\":\"Release notes\",\"i18n\":{\"pt-BR\":{\"title\":\"Brisa 1.0.4 chegou\",\"body\":\"Inicializacao mais rapida.\",\"actionLabel\":\"Notas\"}}");
            var result = s.Verifier.Verify(s.Sign(Manifest(item, seq: 4)), NotificationHighWater.None);
            Check(result.Accepted && result.Manifest is { Seq: 4, Epoch: 1 }, "accepted " + result.Reason);
            var parsed = result.Manifest!.Items.Single();
            Check(parsed.Kind == NotificationKind.Release && parsed.Title == "Brisa 1.0.4 is out" && parsed.MinVersion == "1.0.0" && parsed.MaxVersion == "1.0.3", "fields");
            Check(parsed.ActionUrl?.AbsoluteUri == "https://github.com/insxnsive/brisa/releases" && parsed.ActionLabel == "Release notes", "action");
            Check(parsed.TitlePtBr == "Brisa 1.0.4 chegou" && parsed.ActionLabelPtBr == "Notas", "pt-BR");
        }),
        ("notifications: an empty signed feed is valid", () =>
        {
            using var s = new Signer();
            Check(s.Verifier.Verify(s.Sign(Manifest("")), NotificationHighWater.None).Manifest?.Items.Count == 0, "empty");
        }),
        ("notifications: flipping one bit of the manifest is rejected", () =>
        {
            using var s = new Signer();
            var bytes = s.Sign(Manifest(Item()));
            var at = Encoding.UTF8.GetString(bytes).IndexOf("Brisa 1.0.4", StringComparison.Ordinal);
            bytes[at] ^= 0x01;
            Check(s.Verifier.Verify(bytes, NotificationHighWater.None).Reason == "BAD_SIGNATURE", "tamper");
        }),
        ("notifications: a swapped key id fails and an unknown one is refused", () =>
        {
            using var s = new Signer(1, Kid, "test-key-2");
            var text = Encoding.UTF8.GetString(s.Sign(Manifest(Item())));
            var swapped = Encoding.UTF8.GetBytes(text.Replace("\"kid\":\"test-key-1\"", "\"kid\":\"test-key-2\""));
            Check(s.Verifier.Verify(swapped, NotificationHighWater.None).Reason == "BAD_SIGNATURE", "kid swap");
            var unknown = Encoding.UTF8.GetBytes(text.Replace("\"kid\":\"test-key-1\"", "\"kid\":\"other-key-9\""));
            Check(s.Verifier.Verify(unknown, NotificationHighWater.None).Reason == "UNKNOWN_KID", "unknown kid");
            var disabled = new NotificationVerifier(new Dictionary<string, NotificationPublicKey> { [Kid] = new(Kid, 1, s.Key.ExportSubjectPublicKeyInfo(), Enabled: false) });
            Check(disabled.Verify(s.Sign(Manifest(Item())), NotificationHighWater.None).Reason == "UNKNOWN_KID", "disabled kid");
        }),
        ("notifications: only a 64 byte IEEE P1363 signature is accepted", () =>
        {
            using var s = new Signer();
            var manifest = Manifest(Item());
            var preimage = Encoding.UTF8.GetBytes("brisa-notifications-v1\n" + Kid + "\n" + manifest);
            string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string With(string sig) => "{\"schemaVersion\":1,\"alg\":\"ES256\",\"kid\":\"" + Kid + "\",\"sig\":\"" + sig + "\",\"manifest\":" + manifest + "}";
            var der = s.Key.SignData(preimage, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            Check(s.Verifier.Verify(Encoding.UTF8.GetBytes(With(B64(der))), NotificationHighWater.None).Reason == "BAD_SIG_FORMAT", "DER signature");
            var good = s.Key.SignData(preimage, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            Check(s.Verifier.Verify(Encoding.UTF8.GetBytes(With(B64(good))), NotificationHighWater.None).Accepted, "P1363 control");
            Check(s.Verifier.Verify(Encoding.UTF8.GetBytes(With(B64(good[..63]))), NotificationHighWater.None).Reason == "BAD_SIG_FORMAT", "short signature");
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var foreign = other.SignData(preimage, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            Check(s.Verifier.Verify(Encoding.UTF8.GetBytes(With(B64(foreign))), NotificationHighWater.None).Reason == "BAD_SIGNATURE", "foreign key");
            // The same bytes spelled with different trailing bits are a second spelling of one signature.
            const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            var sig = B64(good);
            var alternate = sig[..^1] + Alphabet[Alphabet.IndexOf(sig[^1]) + 1];
            Check(s.Verifier.Verify(Encoding.UTF8.GetBytes(With(alternate)), NotificationHighWater.None).Reason == "BAD_SIG_FORMAT", "non-canonical base64url");
        }),
        ("notifications: duplicate JSON keys are rejected at every level", () =>
        {
            using var s = new Signer();
            var top = Manifest(Item()).Replace("\"seq\":1,", "\"seq\":1,\"seq\":2,");
            Check(s.Verifier.Verify(s.Sign(top), NotificationHighWater.None).Reason == "DUPLICATE_KEY", "manifest duplicate");
            var nested = Manifest(Item(extra: ",\"title\":\"second title\""));
            Check(s.Verifier.Verify(s.Sign(nested), NotificationHighWater.None).Reason == "DUPLICATE_KEY", "item duplicate");
            var escaped = Manifest(Item()).Replace("\"seq\":1,", "\"seq\":1,\"s\\u0065q\":2,");
            Check(s.Verifier.Verify(s.Sign(escaped), NotificationHighWater.None).Reason == "DUPLICATE_KEY", "escaped duplicate");
            var envelope = Encoding.UTF8.GetString(s.Sign(Manifest(Item()))).Replace("\"alg\":\"ES256\",", "\"alg\":\"ES256\",\"alg\":\"ES256\",");
            Check(s.Verifier.Verify(Encoding.UTF8.GetBytes(envelope), NotificationHighWater.None).Reason == "DUPLICATE_KEY", "envelope duplicate");
        }),
        ("notifications: anything over 65536 bytes is rejected, exactly 65536 is not", () =>
        {
            using var s = new Signer();
            var bytes = s.Sign(Manifest(Item()));
            byte[] Padded(int length) { var padded = new byte[length]; Array.Fill(padded, (byte)' '); bytes.CopyTo(padded, 0); return padded; }
            Check(s.Verifier.Verify(Padded(65_536), NotificationHighWater.None).Accepted, "65536 bytes");
            Check(s.Verifier.Verify(Padded(65_537), NotificationHighWater.None).Reason == "TOO_LARGE", "65537 bytes");
            Check(s.Verifier.Verify([], NotificationHighWater.None).Reason == "TOO_LARGE", "empty");
            var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();
            Check(s.Verifier.Verify(bom, NotificationHighWater.None).Reason == "BOM", "BOM");
            Check(s.Verifier.Verify(bytes.Concat(Encoding.UTF8.GetBytes("{}")).ToArray(), NotificationHighWater.None).Accepted == false, "trailing value");
            Check(!s.Verifier.Verify(Encoding.UTF8.GetBytes("not json"), NotificationHighWater.None).Accepted, "garbage");
        }),
        ("notifications: rollback and epoch downgrade are rejected, an idempotent refresh is not", () =>
        {
            using var s = new Signer();
            var four = s.Sign(Manifest(Item(), seq: 4));
            var high = new NotificationHighWater(1, 4, Now);
            Check(s.Verifier.Verify(s.Sign(Manifest(Item(), seq: 3)), high).Reason == "ROLLBACK_SEQ", "seq 3 after 4");
            Check(s.Verifier.Verify(four, high).Accepted, "same seq");
            Check(s.Verifier.Verify(s.Sign(Manifest(Item(), seq: 5)), high).Accepted, "higher seq");
            Check(s.Verifier.Verify(four, new NotificationHighWater(2, 1, Now)).Reason == "STALE_EPOCH", "epoch downgrade");
            using var rotated = new Signer(2);
            Check(rotated.Verifier.Verify(rotated.Sign(Manifest(Item(), seq: 1, epoch: 2)), new NotificationHighWater(1, 9, Now)).Accepted, "new epoch may restart seq");
            Check(s.Verifier.Verify(s.Sign(Manifest(Item(), epoch: 2)), NotificationHighWater.None).Reason == "BAD_EPOCH", "epoch must match the pinned key");
        }),
        ("notifications: expiry follows the signed clock floor, not the PC clock", () =>
        {
            using var s = new Signer();
            var manifest = s.Verifier.Verify(s.Sign(Manifest(Item(expires: Now.AddHours(1)))), NotificationHighWater.None).Manifest!;
            var fresh = new NotificationHighWater(1, 1, Now);
            Check(NotificationVerifier.SelectActive(manifest, fresh, Now, "1.0.3").Count == 1, "active now");
            Check(NotificationVerifier.SelectActive(manifest, fresh, Now.AddDays(-10), "1.0.3").Count == 1, "clock rolled back stays correct");
            Check(NotificationVerifier.SelectActive(manifest, fresh, Now.AddMinutes(-10), "1.0.3").Count == 1, "slow clock does not hide a new item");
            Check(NotificationVerifier.SelectActive(manifest, fresh, Now.AddHours(2), "1.0.3").Count == 0, "expired");
            Check(NotificationVerifier.SelectActive(manifest, fresh, Now.AddHours(1).AddMinutes(10), "1.0.3").Count == 1, "15 minute tolerance");
            var later = new NotificationHighWater(1, 1, Now.AddHours(2));
            Check(NotificationVerifier.SelectActive(manifest, later, Now.AddDays(-30), "1.0.3").Count == 0, "rolling the clock back cannot revive it");
        }),
        ("notifications: version bounds filter items and an unreadable version sees only unbounded ones", () =>
        {
            using var s = new Signer();
            var items = Item("min-only-item", extra: ",\"minVersion\":\"1.1.0\"") + "," + Item("max-only-item", extra: ",\"maxVersion\":\"1.0.3\"") + "," + Item("open-item");
            var manifest = s.Verifier.Verify(s.Sign(Manifest(items)), NotificationHighWater.None).Manifest!;
            var high = NotificationHighWater.None;
            string[] Ids(string? v) => NotificationVerifier.SelectActive(manifest, high, Now, v).Select(i => i.Id).OrderBy(x => x).ToArray();
            Check(string.Join(",", Ids("1.0.3")) == "max-only-item,open-item", "1.0.3");
            Check(string.Join(",", Ids("1.0.4")) == "open-item", "1.0.4");
            Check(string.Join(",", Ids("1.1.0")) == "min-only-item,open-item", "1.1.0");
            Check(string.Join(",", Ids("1.1.0-beta.1")) == "open-item", "a prerelease sits below min 1.1.0 and above max 1.0.3");
            Check(string.Join(",", Ids("not-a-version")) == "open-item" && string.Join(",", Ids(null)) == "open-item", "unreadable");
            Check(!s.Verifier.Verify(s.Sign(Manifest(Item(extra: ",\"minVersion\":\"2.0.0\",\"maxVersion\":\"1.0.0\""))), NotificationHighWater.None).Accepted, "min above max");
            Check(!s.Verifier.Verify(s.Sign(Manifest(Item(extra: ",\"minVersion\":\"v1.0\""))), NotificationHighWater.None).Accepted, "bad version text");
        }),
        ("notifications: unsafe action URLs are refused by the policy", () =>
        {
            foreach (var url in new[]
            {
                "http://example.com/x", "javascript:alert(1)", "file:///C:/Windows/win.ini", "ms-settings:network", "https://user@example.com/x",
                "https://github.com@evil.com/x", "https://@evil.com/x", "https://:@evil.com/x", "https://127.0.0.1/x", "https://[::1]/x", "https://localhost/x", "https://10.0.0.1/x",
                "https://xn--e1afmkfd.xn--p1ai/", "https://\u043F\u0440\u0438\u043C\u0435\u0440.\u0440\u0444/", "https://example.com:8443/x", "https://exa mple.com/",
                "https://example.com\\evil", "//example.com/x", "/relative/path", "", "https://example.com/" + new string('a', 520),
            })
                Check(!NotificationUrlPolicy.TryValidate(url, out _), "should refuse: " + url);
            Check(NotificationUrlPolicy.TryValidate("https://github.com/insxnsive/brisa/releases", out var ok) && ok!.Host == "github.com", "plain https is fine");
            Check(NotificationUrlPolicy.TryValidate("https://example.com:443/x", out _), "explicit 443");
        }),
        ("notifications: an unsafe URL rejects the whole feed and opening re-checks the link", () =>
        {
            using var s = new Signer();
            var bad = Item("bad-link-item", extra: ",\"actionUrl\":\"http://example.com/x\",\"actionLabel\":\"Go\"");
            var result = s.Verifier.Verify(s.Sign(Manifest(Item("good-item") + "," + bad)), NotificationHighWater.None);
            Check(!result.Accepted && result.Manifest is null && result.Reason == "BAD_ITEM", "atomic rejection");
            var launched = new List<ProcessStartInfo>();
            Check(!NotificationUrlPolicy.TryOpen(new Uri("http://example.com/x"), launched.Add) && launched.Count == 0, "http is not launched");
            Check(!NotificationUrlPolicy.TryOpen(new Uri("ms-settings:network"), launched.Add) && launched.Count == 0, "ms-settings is not launched");
            Check(!NotificationUrlPolicy.TryOpen(null, launched.Add), "null is not launched");
            Check(NotificationUrlPolicy.TryOpen(new Uri("https://github.com/insxnsive/brisa"), launched.Add) && launched.Count == 1, "https is launched");
            Check(launched[0].UseShellExecute && launched[0].FileName == "https://github.com/insxnsive/brisa", "shell execute of the checked URL");
        }),
        ("notifications: control characters, bidi overrides and lone surrogates are refused", () =>
        {
            using var s = new Signer();
            bool Accepts(string item) => s.Verifier.Verify(s.Sign(Manifest(item)), NotificationHighWater.None).Accepted;
            Check(Accepts(Item(title: "Plain title")), "control");
            foreach (var bad in new[] { "\u202E", "\u202A", "\u202D", "\u2066", "\u2069", "\u200E", "\u061C", "\u0007", "\u0085", "\u2028", "\r", "\n" })
                Check(!Accepts(Item(title: "Pay" + bad + "ment")), "title must refuse U+" + ((int)bad[0]).ToString("X4"));
            Check(!Accepts(Item(body: "line\rbreak")), "body refuses carriage return");
            Check(Accepts(Item(body: "one\ntwo\nthree\nfour")), "body allows three newlines");
            Check(!Accepts(Item(body: "1\n2\n3\n4\n5")), "body refuses four newlines");
            Check(!Accepts(Item(body: "evil \u202E txt.exe")), "body refuses bidi");
            Check(!Accepts(Item(extra: ",\"actionUrl\":\"https://example.com/a\",\"actionLabel\":\"Go\\u202E\"")), "label refuses bidi");
            Check(!Accepts(Item(extra: ",\"i18n\":{\"pt-BR\":{\"title\":\"Titulo\\u202E\"}}")), "translation refuses bidi");
            Check(!s.Verifier.Verify(s.Sign(Manifest(Item().Replace("Brisa 1.0.4 is out", "bad \\ud800 surrogate"))), NotificationHighWater.None).Accepted, "lone surrogate");
            Check(!Accepts(Item(title: new string('a', 101))) && Accepts(Item(title: new string('a', 100))), "title limit is 100");
            Check(!Accepts(Item(body: new string('a', 401))) && Accepts(Item(body: new string('a', 400))), "body limit is 400");
            Check(!Accepts(Item(title: "   ")), "blank title");
            // Padding is refused, not trimmed away, and the character check sees the raw text.
            Check(!Accepts(Item(title: " leading")) && !Accepts(Item(title: "trailing ")) && !Accepts(Item(title: "tab\t")), "title padding");
            Check(!Accepts(Item(title: "carriage\r")) && !Accepts(Item(title: "bidi\u202E ")), "padding cannot hide a forbidden character");
            Check(!Accepts(Item(body: "trailing newline\n")) && !Accepts(Item(body: "\nleading newline")) && !Accepts(Item(body: " padded body")), "body padding");
            Check(!Accepts(Item(extra: ",\"actionUrl\":\"https://example.com/a\",\"actionLabel\":\" Go \"")), "label padding");
            Check(Accepts(Item(title: "Clean title", body: "Clean body.")), "clean text passes");
            Check(Accepts(Item(body: "inner\nnewlines\nare fine")), "newlines inside the body are fine");
        }),
        ("notifications: schema rules reject a bad id, kind, dates, count or extra key", () =>
        {
            using var s = new Signer();
            bool Accepts(string manifest) => s.Verifier.Verify(s.Sign(manifest), NotificationHighWater.None).Accepted;
            Check(!Accepts(Manifest(Item("update-1234"))) && !Accepts(Manifest(Item("sys-notice-1"))), "reserved id prefixes");
            Check(!Accepts(Manifest(Item("Bad_Id"))) && !Accepts(Manifest(Item("ab"))), "id shape");
            Check(!Accepts(Manifest(Item("same-id-1") + "," + Item("same-id-1"))), "duplicate id");
            Check(!Accepts(Manifest(Item(kind: "alert"))) && !Accepts(Manifest(Item(kind: "Release"))), "kind enum");
            Check(!Accepts(Manifest(Item(expires: Now.AddHours(-2)))), "expires before published");
            Check(Accepts(Manifest(Item(published: Now, expires: Now.AddDays(90)))) && !Accepts(Manifest(Item(published: Now, expires: Now.AddDays(90).AddSeconds(1)))), "90 day cap");
            Check(!Accepts(Manifest(string.Join(",", Enumerable.Range(0, 21).Select(i => Item("item-" + i.ToString("D3")))))), "21 items");
            Check(Accepts(Manifest(string.Join(",", Enumerable.Range(0, 20).Select(i => Item("item-" + i.ToString("D3")))))), "20 items");
            Check(!Accepts(Manifest(Item(extra: ",\"html\":\"<b>x</b>\""))), "extra item key");
            Check(!Accepts(Manifest(Item(), extra: ",\"extra\":1")), "extra manifest key");
            Check(!Accepts(Manifest(Item(extra: ",\"actionLabel\":\"Go\""))), "label without a URL");
            Check(!Accepts(Manifest(Item(extra: ",\"i18n\":{\"fr\":{\"title\":\"x\"}}"))), "unknown language");
            Check(!Accepts(Manifest(Item()).Replace("\"publishedAt\":\"" + T(Now) + "\"", "\"publishedAt\":\"2026-11-20 12:00:00\"")), "timestamp shape");
            Check(!Accepts(Manifest(Item(), seq: 0)) && !Accepts(Manifest(Item(), seq: 9_007_199_254_740_992)), "seq range");
            var envelope = Encoding.UTF8.GetString(s.Sign(Manifest(Item()))).Replace("\"schemaVersion\":1,\"alg\"", "\"schemaVersion\":1,\"x\":0,\"alg\"");
            Check(!s.Verifier.Verify(Encoding.UTF8.GetBytes(envelope), NotificationHighWater.None).Accepted, "extra envelope key");
            Check(!s.Verifier.Verify(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(s.Sign(Manifest(Item()))).Replace("ES256", "ES384")), NotificationHighWater.None).Accepted, "algorithm");
        }),
        ("notifications: the production trust anchor is pinned and well formed", () =>
        {
            Check(NotificationTrustAnchors.FeedUri == "https://brisa-notify.insxnsive.works/v1/notifications.json", "feed uri");
            var key = NotificationTrustAnchors.ProductionKeys.Single();
            Check(key is { Kid: "brisa-2026-k1", Epoch: 1, Enabled: true } && key.SpkiBytes.Length == 91, "kid, epoch and size");
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key.SpkiBytes, out var read);
            Check(read == 91 && ecdsa.KeySize == 256 && ecdsa.ExportParameters(false).Curve.Oid?.Value == "1.2.840.10045.3.1.7", "P-256");
            Check(NotificationUrlPolicy.TryValidate(NotificationTrustAnchors.FeedUri, out _), "the feed address passes its own URL policy");
            using var s = new Signer();
            Check(NotificationVerifier.Production.Verify(s.Sign(Manifest(Item())), NotificationHighWater.None).Reason == "UNKNOWN_KID", "a test key is not trusted in production");
        }),
        ("notifications: the sample fixture verifies with its own key and never with the production key", () =>
        {
            using var fixture = NotificationFixtures.Create("sample", Now)!;
            var envelope = fixture.Client.FetchAsync(null, default).GetAwaiter().GetResult();
            Check(envelope is { StatusCode: 200, BodyBytes: not null }, "fixture serves bytes");
            var result = fixture.Verifier.Verify(envelope.BodyBytes!, NotificationHighWater.None);
            Check(result.Accepted && result.Manifest!.Items.Count >= 3 && result.Manifest.Items.All(i => i.TitlePtBr is not null), "sample verifies " + result.Reason);
            Check(NotificationVerifier.Production.Verify(envelope.BodyBytes!, NotificationHighWater.None).Reason == "UNKNOWN_KID", "not production");
            Check(fixture.Client.FetchAsync("\"fixture\"", default).GetAwaiter().GetResult().StatusCode == 304, "conditional request");
            Check(NotificationFixtures.Create(null, Now) is null && NotificationFixtures.Create("C:\\definitely\\missing.json", Now) is null, "no source and a missing file give nothing");
        }),
        ("notifications: the state file is written atomically and survives a second save", () => WithDirectory(dir =>
        {
            var store = new NotificationStateStore(dir);
            store.Save(new NotificationStateData(1, 1, 2, null, "\"a\"", ["x"], null));
            store.Save(new NotificationStateData(1, 1, 3, null, "\"b\"", ["x", "y"], null));
            var loaded = store.Load();
            Check(loaded.HighWaterSeq == 3 && loaded.ReadIds!.Count == 2 && loaded.Etag == "\"b\"", "round trip");
            Check(Directory.GetFiles(dir, "*.tmp").Length == 0 && File.ReadAllText(store.Path).Contains("\"highWaterEpoch\""), "no temp file, camelCase names");
            File.WriteAllText(store.Path, "{ not json");
            Check(store.Load() == new NotificationStateData() || store.Load().HighWaterSeq == 0, "a damaged file starts empty");
            File.WriteAllText(store.Path, "{\"schemaVersion\":9,\"highWaterSeq\":5}");
            Check(store.Load().HighWaterSeq == 0, "an unknown schema starts empty");
        })),
        ("notifications: a fetched feed becomes a snapshot, is saved, and reloads verified", () => WithDirectory(dir =>
        {
            using var s = new Signer();
            var client = new ScriptedClient(s.Sign(Manifest(Item("first-item", published: Now.AddHours(-3)) + "," + Item("second-item", published: Now.AddHours(-1)), seq: 7)));
            using var service = Service(dir, client, s);
            var changes = 0;
            service.SnapshotChanged += (_, _) => changes++;
            service.LoadState();
            service.PollOnceAsync(default).GetAwaiter().GetResult();
            var snapshot = service.Snapshot;
            Check(snapshot.Items.Select(i => i.Id).SequenceEqual(["second-item", "first-item"]) && snapshot.UnreadApiCount == 2 && snapshot.BadgeCount == 2, "newest first, both unread");
            Check(changes >= 1 && client.Seen.Single() is null, "first request carries no validator");
            service.FlushAsync().GetAwaiter().GetResult();
            var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, NotificationStateStore.FileName))).RootElement;
            Check(saved.GetProperty("highWaterSeq").GetInt64() == 7 && saved.GetProperty("etag").GetString() == "\"e1\"" && saved.GetProperty("cachedEnvelopeJson").GetString()!.Contains("first-item"), "state saved");
            using var reloaded = Service(dir, new ScriptedClient(), s);
            Check(reloaded.Snapshot.Items.Count == 0, "construction reads nothing");
            reloaded.LoadState();
            Check(reloaded.Snapshot.Items.Count == 2, "reload re-verifies and restores");
            reloaded.PollOnceAsync(default).GetAwaiter().GetResult();
            Check(((ScriptedClient)ReflectClient(reloaded)).Seen.Single() == "\"e1\"", "later requests send If-None-Match");
        })),
        ("notifications: a tampered state file is not trusted but read marks and the high water survive", () => WithDirectory(dir =>
        {
            using var s = new Signer();
            using (var first = Service(dir, new ScriptedClient(s.Sign(Manifest(Item("kept-item"), seq: 5))), s))
            {
                first.LoadState(); first.PollOnceAsync(default).GetAwaiter().GetResult();
                first.MarkRead("kept-item");
                first.FlushAsync().GetAwaiter().GetResult();
            }
            var path = Path.Combine(dir, NotificationStateStore.FileName);
            var original = File.ReadAllText(path);
            // Edit the cached feed text without a valid signature behind it.
            File.WriteAllText(path, original.Replace("Brisa 1.0.4 is out", "Free gift cards inside"));
            using var tampered = Service(dir, new ScriptedClient(), s);
            tampered.LoadState();
            tampered.FlushAsync().GetAwaiter().GetResult();
            Check(tampered.Snapshot.Items.Count == 0, "tampered cache shows nothing");
            var after = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            Check(after.GetProperty("highWaterSeq").GetInt64() == 5 && after.GetProperty("readIds").GetArrayLength() == 1, "high water and read marks kept");
            Check(after.GetProperty("cachedEnvelopeJson").ValueKind == JsonValueKind.Null && after.GetProperty("etag").ValueKind == JsonValueKind.Null, "bad cache and validator dropped");
            using var wrongKey = new Signer();
            File.WriteAllText(path, original);
            using var untrusted = Service(dir, new ScriptedClient(), wrongKey);
            untrusted.LoadState();
            Check(untrusted.Snapshot.Items.Count == 0, "a cache signed by another key shows nothing");
            File.WriteAllText(path, "garbage");
            using var broken = Service(dir, new ScriptedClient(), s);
            broken.LoadState();
            Check(broken.Snapshot.Items.Count == 0 && broken.Snapshot.BadgeCount == 0, "corrupt state fails closed");
        })),
        ("notifications: an older feed never replaces a newer one", () => WithDirectory(dir =>
        {
            using var s = new Signer();
            var client = new ScriptedClient(s.Sign(Manifest(Item("newer-item"), seq: 4)), s.Sign(Manifest(Item("older-item"), seq: 3)), s.Sign(Manifest(Item("same-item"), seq: 4)));
            using var service = Service(dir, client, s);
            service.LoadState();
            service.PollOnceAsync(default).GetAwaiter().GetResult();
            service.PollOnceAsync(default).GetAwaiter().GetResult();
            Check(service.Snapshot.Items.Single().Id == "newer-item", "seq 3 after 4 is ignored");
            service.PollOnceAsync(default).GetAwaiter().GetResult();
            Check(service.Snapshot.Items.Single().Id == "same-item", "seq 4 again is accepted as a refresh");
        })),
        ("notifications: a failed or invalid fetch keeps what is already shown and says nothing", () => WithDirectory(dir =>
        {
            using var s = new Signer();
            var client = new ScriptedClient(s.Sign(Manifest(Item("steady-item"), seq: 2)), Encoding.UTF8.GetBytes("<html>captive portal</html>"));
            using var service = Service(dir, client, s);
            service.LoadState();
            service.PollOnceAsync(default).GetAwaiter().GetResult();
            service.PollOnceAsync(default).GetAwaiter().GetResult();
            Check(service.Snapshot.Items.Single().Id == "steady-item", "invalid body ignored");
            client.Throw = true;
            service.PollOnceAsync(default).GetAwaiter().GetResult();
            Check(service.Snapshot.Items.Count == 1, "client exception ignored");
            client.Throw = false;
            client.Status = 503;
            client.Queue.Enqueue(s.Sign(Manifest(Item("never-shown"), seq: 9)));
            service.PollOnceAsync(default).GetAwaiter().GetResult();
            Check(service.Snapshot.Items.Single().Id == "steady-item", "server error ignored");
        })),
        ("notifications: read marks, mark all and the 200 entry cap", () => WithDirectory(dir =>
        {
            using var s = new Signer();
            using var service = Service(dir, new ScriptedClient(s.Sign(Manifest(Item("alpha-item") + "," + Item("bravo-item")))), s);
            service.LoadState();
            service.PollOnceAsync(default).GetAwaiter().GetResult();
            service.MarkRead("alpha-item");
            service.MarkRead("not-in-the-feed");
            Check(service.Snapshot.UnreadApiCount == 1 && service.Snapshot.Items.Single(i => i.Id == "alpha-item").IsUnread == false, "one read");
            service.MarkAllRead();
            Check(service.Snapshot.UnreadApiCount == 0 && service.Snapshot.BadgeCount == 0, "all read");
            service.FlushAsync().GetAwaiter().GetResult();
            var path = Path.Combine(dir, NotificationStateStore.FileName);
            var state = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            Check(state.GetProperty("readIds").GetArrayLength() == 2, "foreign id was not stored");
            var crowded = new NotificationStateStore(dir).Load() with { ReadIds = Enumerable.Range(0, 250).Select(i => "old-" + i).ToList() };
            new NotificationStateStore(dir).Save(crowded);
            using var trimmed = Service(dir, new ScriptedClient(), s);
            trimmed.LoadState();
            trimmed.MarkAllRead();
            trimmed.FlushAsync().GetAwaiter().GetResult();
            var ids = new NotificationStateStore(dir).Load().ReadIds!;
            Check(ids.Count == NotificationService.MaxReadIds && ids.Contains("alpha-item") && ids.Contains("bravo-item") && !ids.Contains("old-51") && ids.Contains("old-52"), "oldest marks are dropped first");
        })),
        ("notifications: the update card merges in and adds one to the badge only when ready", () => WithDirectory(dir =>
        {
            using var s = new Signer();
            var updates = new FakeUpdates();
            using var service = Service(dir, new ScriptedClient(s.Sign(Manifest(Item("only-item")))), s, updates);
            service.LoadState();
            service.PollOnceAsync(default).GetAwaiter().GetResult();
            Check(service.Snapshot.PinnedUpdate is null && service.Snapshot.BadgeCount == 1, "no update");
            var raised = 0;
            service.SnapshotChanged += (_, _) => raised++;
            updates.Set(new UpdateStatus(UpdatePhase.Downloading, "UpdateDownloading", "1.0.4", 40));
            Check(service.Snapshot.PinnedUpdate is { Phase: UpdatePhase.Downloading, Progress: 40, CanInstallAndRestart: false } && service.Snapshot.BadgeCount == 1 && raised == 1, "downloading is pinned without a badge");
            updates.Set(new UpdateStatus(UpdatePhase.Ready, "UpdateReadyVersion", "1.0.4"));
            Check(service.Snapshot.PinnedUpdate is { Phase: UpdatePhase.Ready, Version: "1.0.4", CanInstallAndRestart: true } && service.Snapshot.UnreadApiCount == 1 && service.Snapshot.BadgeCount == 2, "ready adds one");
            service.MarkAllRead();
            Check(service.Snapshot.BadgeCount == 1, "mark all read leaves the pending update");
            updates.Set(new UpdateStatus(UpdatePhase.Current, "UpdateCurrent"));
            Check(service.Snapshot.PinnedUpdate is null && service.Snapshot.BadgeCount == 0, "gone when current");
        })),
        ("notifications: rows stay in New while the popover is open and settle when it closes", () => WithDirectory(dir =>
        {
            using var s = new Signer();
            using var service = Service(dir, new ScriptedClient(s.Sign(Manifest(Item("one-item") + "," + Item("two-item") + "," + Item("old-item")))), s);
            service.LoadState();
            service.PollOnceAsync(default).GetAwaiter().GetResult();
            service.MarkRead("old-item");
            service.BeginPopoverSession();
            Check(service.Snapshot.Items.Where(i => i.IsSessionFresh).Select(i => i.Id).OrderBy(x => x).SequenceEqual(["one-item", "two-item"]), "fresh set is what was unread on open");
            service.MarkRead("one-item");
            var row = service.Snapshot.Items.Single(i => i.Id == "one-item");
            Check(!row.IsUnread && row.IsSessionFresh && service.Snapshot.BadgeCount == 1, "read but still in place, badge counts down");
            service.MarkAllRead();
            Check(service.Snapshot.Items.Count(i => i.IsSessionFresh) == 2 && service.Snapshot.BadgeCount == 0, "mark all keeps the rows put");
            service.EndPopoverSession();
            Check(service.Snapshot.Items.All(i => !i.IsSessionFresh), "closing clears the fresh set");
        })),
        ("notifications: opening the popover refreshes at most once a minute and not before startup", () => WithDirectory(dir =>
        {
            using var s = new Signer();
            var clock = Now;
            var client = new ScriptedClient(s.Sign(Manifest(Item("only-item"))));
            using var service = new NotificationService(client, new NotificationStateStore(dir), null, s.Verifier, () => clock, "1.0.3", TimeSpan.Zero, TimeSpan.FromHours(1));
            service.BeginPopoverSession(); service.EndPopoverSession();
            Thread.Sleep(100);
            Check(client.Calls == 0, "no request before the startup delay has passed");
            service.LoadState();
            service.Start();
            SpinUntil(() => client.Calls >= 1, "the startup poll");
            var calls = client.Calls;
            clock = clock.AddSeconds(30);
            service.BeginPopoverSession(); service.EndPopoverSession();
            Thread.Sleep(150);
            Check(client.Calls == calls, "opened within a minute: no request");
            clock = clock.AddSeconds(61);
            service.BeginPopoverSession(); service.EndPopoverSession();
            SpinUntil(() => client.Calls == calls + 1, "the refresh after a minute");
        })),
        ("notifications: the language switch re-projects text without another request", () => WithDirectory(dir =>
        {
            var loc = LocalizationManager.Current;
            var saved = loc.Preference;
            try
            {
                loc.Apply(LocalizationManager.English);
                using var s = new Signer();
                var item = Item("bilingual-item", extra: ",\"actionUrl\":\"https://example.com/a\",\"actionLabel\":\"Read\",\"i18n\":{\"pt-BR\":{\"title\":\"Titulo em portugues\",\"actionLabel\":\"Ler\"}}") + "," + Item("english-only");
                var client = new ScriptedClient(s.Sign(Manifest(item)));
                using var service = Service(dir, client, s);
                service.LoadState();
                service.PollOnceAsync(default).GetAwaiter().GetResult();
                var raised = 0;
                service.SnapshotChanged += (_, _) => raised++;
                var en = service.Snapshot.Items.Single(i => i.Id == "bilingual-item");
                Check(en.Title == "Brisa 1.0.4 is out" && en.ActionLabel == "Read", "english");
                loc.Apply(LocalizationManager.PortugueseBrazil);
                var pt = service.Snapshot.Items.Single(i => i.Id == "bilingual-item");
                Check(pt.Title == "Titulo em portugues" && pt.Body == "Faster startup." && pt.ActionLabel == "Ler" && raised >= 1, "pt-BR with english fallback for missing fields");
                Check(service.Snapshot.Items.Single(i => i.Id == "english-only").Title == "Brisa 1.0.4 is out", "untranslated item falls back");
                loc.Apply(LocalizationManager.English);
                Check(service.Snapshot.Items.Single(i => i.Id == "bilingual-item").Title == "Brisa 1.0.4 is out" && client.Calls == 1, "back to english, still one request");
            }
            finally { loc.Apply(saved); }
        })),
        ("notifications: start loads, waits, polls and disposes cleanly", () => WithDirectory(dir =>
        {
            using var fixture = NotificationFixtures.Create("sample", Now)!;
            using var service = new NotificationService(fixture.Client, new NotificationStateStore(dir), null, fixture.Verifier, () => Now, "1.0.3", TimeSpan.Zero, TimeSpan.FromHours(1));
            Check(service.Snapshot.Items.Count == 0, "empty until started");
            service.Start();
            service.Start();
            SpinUntil(() => service.Snapshot.Items.Count >= 3, "the fixture feed");
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            service.Dispose();
            service.MarkRead("sample-security");
            Check(File.Exists(Path.Combine(dir, NotificationStateStore.FileName)), "state was saved before shutdown");
        })),
        ("notifications: the null client is silent and opens nothing", () =>
        {
            using var client = new NullNotificationFeedClient();
            var result = client.FetchAsync("\"x\"", default).GetAwaiter().GetResult();
            Check(result is { StatusCode: 0, BodyBytes: null }, "empty result");
        }),
        ("notifications: the HTTP handler refuses redirects and cookies and pins TLS", () =>
        {
            using var handler = HttpNotificationFeedClient.CreateHandler();
            Check(!handler.AllowAutoRedirect && !handler.UseCookies, "no redirects, no cookies");
            Check(handler.ConnectTimeout == TimeSpan.FromSeconds(8) && HttpNotificationFeedClient.RequestTimeout == TimeSpan.FromSeconds(12), "8 s connect, 12 s total");
            Check(handler.SslOptions.EnabledSslProtocols == (SslProtocols.Tls12 | SslProtocols.Tls13), "TLS 1.2 and 1.3 only");
            Check(handler.AutomaticDecompression == (DecompressionMethods.GZip | DecompressionMethods.Brotli), "gzip and brotli");
        }),
        ("notifications: the HTTP client sends one plain GET to the pinned address", () =>
        {
            var seen = new List<string>();
            var handler = new StubHandler((request, _) =>
            {
                seen.Add(request.Method + " " + request.RequestUri + " accept=" + request.Headers.Accept + " ua=" + request.Headers.UserAgent
                    + " inm=" + string.Join(",", request.Headers.TryGetValues("If-None-Match", out var v) ? v : []) + " cookie=" + request.Headers.Contains("Cookie")
                    + " auth=" + (request.Headers.Authorization is not null));
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")) };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc\"");
                return Task.FromResult(response);
            });
            using var client = new HttpNotificationFeedClient(handler, TimeSpan.FromSeconds(5));
            var result = client.FetchAsync("\"old\"", default).GetAwaiter().GetResult();
            Check(result.StatusCode == 200 && result.ETag == "\"abc\"" && result.BodyBytes!.Length == 2, "ok");
            Check(seen.Single() == "GET " + NotificationTrustAnchors.FeedUri + " accept=application/json ua=Brisa/" + AppVersion.Current + " inm=\"old\" cookie=False auth=False", seen.Single());
            client.FetchAsync("bad\r\nheader: x", default).GetAwaiter().GetResult();
            Check(seen[1].Contains("inm= "), "an unsafe validator is not sent");
            client.FetchAsync(null, default).GetAwaiter().GetResult();
            Check(seen[2].Contains("inm= "), "no validator without a cache");
            client.FetchAsync("W/\"e1\"", default).GetAwaiter().GetResult();
            Check(seen[3].Contains("inm=\"e1\" cookie"), "a weak tag is sent back as a plain tag: " + seen[3]);
            client.FetchAsync("W/", default).GetAwaiter().GetResult();
            Check(seen[4].Contains("inm= "), "a bare W/ sends nothing");
        }),
        ("notifications: the HTTP client stops at 65536 bytes and gives up quietly", () =>
        {
            FeedFetchResult Fetch(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null)
            {
                using var client = new HttpNotificationFeedClient(new StubHandler(respond), timeout ?? TimeSpan.FromSeconds(5));
                return client.FetchAsync(null, default).GetAwaiter().GetResult();
            }
            Task<HttpResponseMessage> Respond(HttpContent content, HttpStatusCode code = HttpStatusCode.OK) => Task.FromResult(new HttpResponseMessage(code) { Content = content });
            Check(Fetch((_, _) => Respond(new StreamContent(new ZeroStream(65_536)))).BodyBytes?.Length == 65_536, "exactly the limit is accepted");
            Check(Fetch((_, _) => Respond(new StreamContent(new ZeroStream(65_537)))).BodyBytes is null, "one byte over is dropped");
            var endless = new ZeroStream(long.MaxValue);
            Check(Fetch((_, _) => Respond(new StreamContent(endless))).BodyBytes is null && endless.Delivered <= 65_537 + 8192, "an endless stream is cut at the limit, delivered " + endless.Delivered);
            Check(Fetch((_, _) => Respond(new ByteArrayContent(new byte[70_000]))).BodyBytes is null, "declared length over the limit");
            Check(Fetch((_, _) => Respond(new ByteArrayContent([]), HttpStatusCode.Found)) is { StatusCode: 302, BodyBytes: null }, "a redirect is returned, not followed");
            Check(Fetch((_, _) => Respond(new ByteArrayContent([]), HttpStatusCode.ServiceUnavailable)) is { StatusCode: 503, BodyBytes: null }, "server error");
            Check(Fetch((_, _) => Respond(new ByteArrayContent([]), HttpStatusCode.NotModified)).StatusCode == 304, "not modified");
            Check(Fetch((_, _) => throw new HttpRequestException("offline")) is { StatusCode: 0, BodyBytes: null }, "network failure");
            var watch = Stopwatch.StartNew();
            var stalled = Fetch(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); }, TimeSpan.FromMilliseconds(200));
            Check(stalled is { StatusCode: 0, BodyBytes: null } && watch.Elapsed < TimeSpan.FromSeconds(5), "a stalled server times out");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            using var client = new HttpNotificationFeedClient(new StubHandler((_, token) => Task.FromCanceled<HttpResponseMessage>(token)), TimeSpan.FromSeconds(5));
            var threw = false;
            try { client.FetchAsync(null, cancelled.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { threw = true; }
            Check(threw, "the caller's own cancellation still propagates");
        }),
    ];

    // ---- helpers -------------------------------------------------------------------------

    private static void Check(bool ok, string what) { if (!ok) throw new InvalidOperationException(what); }

    private static string T(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string J(string s) => JsonSerializer.Serialize(s);

    private static string Item(string id = "release-notes-1", string kind = "release", string title = "Brisa 1.0.4 is out", string body = "Faster startup.",
        DateTimeOffset? published = null, DateTimeOffset? expires = null, string? extra = null) =>
        "{\"id\":" + J(id) + ",\"kind\":" + J(kind) + ",\"title\":" + J(title) + ",\"body\":" + J(body)
        + ",\"publishedAt\":\"" + T(published ?? Now.AddHours(-1)) + "\",\"expiresAt\":\"" + T(expires ?? Now.AddDays(30)) + "\"" + extra + "}";

    private static string Manifest(string items, long seq = 1, int epoch = 1, DateTimeOffset? published = null, string? extra = null) =>
        "{\"schemaVersion\":1,\"epoch\":" + epoch + ",\"seq\":" + seq + ",\"publishedAt\":\"" + T(published ?? Now) + "\",\"items\":[" + items + "]" + extra + "}";

    private static void WithDirectory(Action<string> run)
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "brisa-notify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { run(dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void SpinUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for " + what);
            Thread.Sleep(10);
        }
    }

    private static NotificationService Service(string dir, ScriptedClient client, Signer signer, FakeUpdates? updates = null)
    {
        var service = new NotificationService(client, new NotificationStateStore(dir), updates, signer.Verifier, () => Now, "1.0.3", TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        Clients.AddOrUpdate(service, client);
        return service;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NotificationService, ScriptedClient> Clients = new();

    private static INotificationFeedClient ReflectClient(NotificationService service) => Clients.TryGetValue(service, out var client) ? client : throw new InvalidOperationException();

    private sealed class Signer : IDisposable
    {
        public Signer(int epoch = 1, params string[] kids)
        {
            Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var spki = Key.ExportSubjectPublicKeyInfo();
            Verifier = new NotificationVerifier((kids.Length == 0 ? [Kid] : kids).ToDictionary(kid => kid, kid => new NotificationPublicKey(kid, epoch, spki)));
        }

        public ECDsa Key { get; }

        public NotificationVerifier Verifier { get; }

        public byte[] Sign(string manifest, string? kid = null) => Encoding.UTF8.GetBytes(NotificationFixtures.SignEnvelope(manifest, kid ?? Kid, Key));

        public void Dispose() => Key.Dispose();
    }

    private sealed class ScriptedClient : INotificationFeedClient
    {
        public ScriptedClient(params byte[][] bodies) { foreach (var body in bodies) Queue.Enqueue(body); }

        public Queue<byte[]> Queue { get; } = new();

        public List<string?> Seen { get; } = [];

        public int Calls { get; private set; }

        public bool Throw { get; set; }

        public int Status { get; set; } = 200;

        public Task<FeedFetchResult> FetchAsync(string? ifNoneMatch, CancellationToken cancellationToken)
        {
            Calls++;
            Seen.Add(ifNoneMatch);
            if (Throw) throw new HttpRequestException("scripted failure");
            if (Status != 200) return Task.FromResult(new FeedFetchResult(Status, null, null));
            return Task.FromResult(Queue.Count == 0 ? new FeedFetchResult(304, null, null) : new FeedFetchResult(200, Queue.Dequeue(), "\"e1\""));
        }

        public void Dispose() { }
    }

    private sealed class FakeUpdates : IUpdateService
    {
        public UpdateStatus Status { get; private set; } = new(UpdatePhase.Idle, "UpdateIdle");

        public bool HasPendingUpdate => Status.Phase == UpdatePhase.Ready;

        public event EventHandler<UpdateStatus>? StatusChanged;

        public void Set(UpdateStatus status) { Status = status; StatusChanged?.Invoke(this, status); }

        public void Start() { }

        public Task CheckNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void PrepareApply(bool restart) { }

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    /// <summary>A body of zeros that counts how much of it was actually read.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public long Delivered => _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Fill(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Fill(Span<byte> buffer)
        {
            var take = (int)Math.Min(buffer.Length, length - _position);
            buffer[..take].Clear();
            _position += take;
            return take;
        }
    }
}
