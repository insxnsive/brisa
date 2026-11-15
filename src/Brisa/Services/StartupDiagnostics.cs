namespace Brisa.Services;

internal static class StartupDiagnostics
{
    internal static readonly IReadOnlySet<string> Codes = new HashSet<string>(StringComparer.Ordinal)
    {
        "INIT_DATA_NOT_PRIVATE", "INIT_DATA_MIGRATION_FAILED", "INIT_MIGRATION_INSPECTION_UNAVAILABLE",
        "INIT_DATA_DIR_INVALID", "INIT_UNKNOWN"
    };

    internal static bool IsKnownCode(string? code) => code is not null && Codes.Contains(code);

    internal static string Render(string? code, IReadOnlyList<string> stderr)
    {
        var lines = new List<string>
        {
            $"Brisa log. Generated {DateTime.UtcNow:O}. Contains no account names, passwords, file paths or IP addresses.",
            "Native backend: unavailable",
            $"Startup code: {(IsKnownCode(code) ? code : "INIT_UNKNOWN")}",
            "Stderr tail (safe codes):"
        };
        var unclassified = 0;
        foreach (var line in stderr)
        {
            string? stderrCode = null;
            try
            {
                var frame = NdjsonProtocol.ParseResponse(line);
                if (frame.Id == "" && !frame.Ok && IsKnownCode(frame.Code)) stderrCode = frame.Code;
            }
            catch (InvalidDataException) { }
            if (stderrCode is null) unclassified++;
            lines.Add(stderrCode ?? "STDERR_UNCLASSIFIED");
        }
        lines.Insert(3, $"Startup stderr: {stderr.Count} retained lines, {unclassified} unclassified.");
        if (stderr.Count == 0) lines.Add("none recorded");
        return string.Join('\n', lines);
    }
}
