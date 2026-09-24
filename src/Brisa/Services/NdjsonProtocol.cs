using System.Text.Json;
using Brisa.Models;

namespace Brisa.Services;

public static class NdjsonProtocol
{
    public const int MaxResponseCharacters = 1_048_576;
    public static string SerializeRequest(string id, string command, object payload)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Request id and command are required.");
        var line = JsonSerializer.Serialize(new BackendRequest(id, command, payload), new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
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
}
