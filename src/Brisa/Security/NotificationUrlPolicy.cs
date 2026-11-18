using System.Diagnostics;

namespace Brisa.Security;

/// <summary>
/// The only way a feed item can send the user to a web page. A signed feed is trusted for its
/// text, not for a free pass to launch anything: the link is checked when the manifest is
/// verified and again right before it is opened.
/// </summary>
public static class NotificationUrlPolicy
{
    public const int MaxLength = 512;

    public static bool TryValidate(string? raw, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxLength) return false;
        // Printable ASCII only: no whitespace, control characters, backslashes or IDN text.
        foreach (var c in raw) if (c < 0x21 || c > 0x7E || c == '\\') return false;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)) return false;
        if (!IsAllowed(parsed)) return false;
        uri = parsed;
        return true;
    }

    public static bool IsAllowed(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)) return false;
        if (uri.Port != 443) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        // An empty user name still leaves "@" in the address ("https://@host/"), so look for it directly.
        var rest = uri.AbsoluteUri.AsSpan("https://".Length);
        var end = rest.IndexOfAny('/', '?', '#');
        if ((end >= 0 ? rest[..end] : rest).Contains('@')) return false;
        if (uri.HostNameType != UriHostNameType.Dns) return false;
        var host = uri.Host;
        if (host.Length == 0 || host.Length > 253 || !host.Contains('.')) return false;
        foreach (var c in host)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.')) return false;
        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            // Punycode hides a non-ASCII name behind ASCII letters.
            if (label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Opens a link in the default browser after checking it again. False if refused or failed.</summary>
    public static bool TryOpen(Uri? uri, Action<ProcessStartInfo>? launch = null)
    {
        if (!IsAllowed(uri)) return false;
        try
        {
            var info = new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true };
            if (launch is null) Process.Start(info);
            else launch(info);
            return true;
        }
        catch { return false; }
    }
}
