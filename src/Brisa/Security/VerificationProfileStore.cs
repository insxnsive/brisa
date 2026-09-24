using System.IO;

namespace Brisa.Security;

/// <summary>Owns only GUID-named, disposable verification profiles.</summary>
public static class VerificationProfileStore
{
    public static string Root => Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "Brisa", "verification");

    public static string Create()
    {
        Directory.CreateDirectory(Root);
        var profile = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        return profile;
    }

    public static void Cleanup(string? root = null)
    {
        root ??= Root;
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            TryDelete(directory);
        }
    }

    public static async Task DeleteWithRetryAsync(string profile)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (TryDelete(profile)) return;
            await Task.Delay(500);
        }
        // A locked profile remains in our dedicated directory and is swept on
        // the next startup. Never delete another application's WebView profile.
    }

    private static bool TryDelete(string profile)
    {
        try { if (Directory.Exists(profile)) Directory.Delete(profile, true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
