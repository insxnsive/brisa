using System.Text.Json;

namespace Brisa.Security;

public sealed record VerificationChallenge(Uri Url, string Challenge, IReadOnlyList<string> Methods);
public sealed record VerificationAnswer(string Token, string Method);

public static class VerificationPolicy
{
    private static readonly HashSet<string> Methods = ["captcha", "ownership-email", "ownership-sms"];
    public const int MaxTokenLength = 16_384;
    public const int MaxMessageLength = 32_768;

    public static bool TryParseChallenge(string raw, out VerificationChallenge? challenge)
    {
        challenge = null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var query = ParseQuery(uri.Query);
        if (query is null) return false;

        if (uri.Host.Equals("verify.proton.me", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/")
        {
            if (query.Keys.Any(k => k is not ("token" or "methods" or "embed" or "vpn")) || query.Count != 4
                || !One(query, "token", out var token) || !One(query, "methods", out var rawMethods)
                || !One(query, "embed", out var embed) || embed != "1" || !One(query, "vpn", out var vpn) || vpn != "1") return false;
            var offered = rawMethods.Split(',', StringSplitOptions.None);
            if (!ValidChallenge(token) || offered.Length == 0 || offered.Distinct(StringComparer.Ordinal).Count() != offered.Length
                || offered.Any(m => !Methods.Contains(m) || m == "captcha")) return false;
            challenge = new(uri, token, offered);
            return true;
        }

        if (!uri.Host.Equals("vpn-api.proton.me", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath != "/core/v4/captcha"
            || query.Count != 1 || !One(query, "Token", out var captchaToken) || !ValidChallenge(captchaToken)) return false;
        challenge = new(uri, captchaToken, ["captcha"]);
        return true;
    }

    public static bool IsExactNavigation(string raw, VerificationChallenge challenge) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri == challenge.Url;

    public static bool TryParseMessage(string serialized, VerificationChallenge expected, string source, out VerificationAnswer? answer)
    {
        answer = null;
        if (serialized.Length is 0 or > MaxMessageLength || !IsExactNavigation(source, expected)) return false;
        try
        {
            using var document = JsonDocument.Parse(serialized);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeNode)) return false;
            var type = typeNode.GetString();
            string? token;
            string method;
            if (type == "HUMAN_VERIFICATION_SUCCESS")
            {
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                    || !payload.TryGetProperty("token", out var tokenNode) || !payload.TryGetProperty("type", out var methodNode)) return false;
                token = tokenNode.GetString(); method = methodNode.GetString() ?? "";
            }
            else if (type is "pm_captcha" or "proton_captcha")
            {
                if (!root.TryGetProperty("token", out var tokenNode)) return false;
                token = tokenNode.GetString(); method = "captcha";
            }
            else return false;
            if (token is null || token.Length is 0 or > MaxTokenLength || !expected.Methods.Contains(method, StringComparer.Ordinal)) return false;
            if (method == "captcha" && (!token.StartsWith(expected.Challenge + ":", StringComparison.Ordinal) || token.Length <= expected.Challenge.Length + 1)) return false;
            answer = new(token, method);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool ValidChallenge(string value) => value.Length is >= 3 and <= 4096 && value == value.Trim();
    private static bool One(Dictionary<string, List<string>> query, string key, out string value)
    {
        value = "";
        if (!query.TryGetValue(key, out var values) || values.Count != 1) return false;
        value = values[0]; return true;
    }
    private static Dictionary<string, List<string>>? ParseQuery(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            try
            {
                var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
                var value = Uri.UnescapeDataString((parts.Length > 1 ? parts[1] : "").Replace('+', ' '));
                if (!result.TryGetValue(key, out var list)) result[key] = list = [];
                list.Add(value);
            }
            catch { return null; }
        }
        return result;
    }
}
