using System.Text.Json;
using Brisa.Models;

namespace Brisa.Services;

public static class NdjsonProtocol
{
    public const int MaxResponseCharacters = 1_048_576;
    // Built once: new options per request would rebuild serializer metadata on every call, including status ticks and progress polls.
    private static readonly JsonSerializerOptions RequestOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    public static string SerializeRequest(string id, string command, object payload)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Request id and command are required.");
        var line = JsonSerializer.Serialize(new BackendRequest(id, command, payload), RequestOptions);
        if (line.Contains('\n') || line.Contains('\r')) throw new InvalidDataException("Invalid backend request frame.");
        return line;
    }
    public static BackendEnvelope ParseResponse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxResponseCharacters || line.Contains('\n') || line.Contains('\r'))
            throw new InvalidDataException("Invalid backend response frame.");
        BackendEnvelope? response;
        try { response = JsonSerializer.Deserialize(line, NativeJsonContext.Default.BackendEnvelope); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid backend response JSON.", ex); }
        if (response is null || string.IsNullOrWhiteSpace(response.Id) || response.Result.ValueKind is JsonValueKind.Undefined)
            throw new InvalidDataException("Incomplete backend response.");
        return response;
    }

    /// <summary>
    /// Recovers the request id from a frame that failed validation, so one bad response can
    /// fail the request it belongs to instead of poisoning the whole transport. Bounded JSON
    /// only: an oversized or truncated frame is never re-parsed, and an ambiguous or
    /// duplicated id returns false so nothing is guessed.
    /// </summary>
    public static bool TryRecoverRequestId(string line, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxResponseCharacters || line.Contains('\n') || line.Contains('\r')) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind is not JsonValueKind.Object) return false;
            string? found = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("id")) continue;
                if (found is not null) return false;
                if (property.Value.ValueKind is not JsonValueKind.String) return false;
                found = property.Value.GetString();
            }
            if (string.IsNullOrWhiteSpace(found)) return false;
            id = found!;
            return true;
        }
        catch (JsonException) { return false; }
    }
}
