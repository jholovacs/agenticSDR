using System.Globalization;
using System.Text.Json;

namespace AgenticSdr.Host;

/// <summary>
/// Asks RepeaterBook for a callsign near a frequency when a location is configured.
/// Failure leaves the band-plan label in place. This is a name lookup, not a decode of the audio.
/// </summary>
public sealed class RepeaterLookup(HttpClient http, ILogger<RepeaterLookup> log)
{
    public async Task<string?> TryFindAsync(double frequencyHz, double latitude, double longitude, CancellationToken cancellationToken)
    {
        try
        {
            string url =
                "https://www.repeaterbook.com/api/export.php?func=prox" +
                $"&lat={latitude.ToString(CultureInfo.InvariantCulture)}" +
                $"&lon={longitude.ToString(CultureInfo.InvariantCulture)}&dist=30";
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body) || body.TrimStart().StartsWith('<'))
                return null;
            return MatchCallsign(body, frequencyHz);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogInformation(ex, "Repeater lookup failed.");
            return null;
        }
    }

    public static string? MatchCallsign(string json, double frequencyHz)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
            array = root;
        else if (!TryArray(root, out array))
            return null;

        string? best = null;
        double bestDelta = 15_000;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (!TryFrequency(item, out double hz))
                continue;
            double delta = Math.Abs(hz - frequencyHz);
            if (delta >= bestDelta || !TryCall(item, out string? call) || string.IsNullOrWhiteSpace(call))
                continue;
            best = call;
            bestDelta = delta;
        }

        return best;
    }

    private static bool TryArray(JsonElement root, out JsonElement array)
    {
        foreach (string name in new[] { "results", "Results", "data" })
        {
            if (root.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array)
                return true;
        }

        array = default;
        return false;
    }

    private static bool TryFrequency(JsonElement item, out double hz)
    {
        foreach (string name in new[] { "Frequency", "frequency", "Output", "output", "freq" })
        {
            if (!item.TryGetProperty(name, out JsonElement value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
            {
                hz = number < 10_000 ? number * 1_000_000 : number;
                return true;
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                hz = parsed < 10_000 ? parsed * 1_000_000 : parsed;
                return true;
            }
        }

        hz = 0;
        return false;
    }

    private static bool TryCall(JsonElement item, out string? call)
    {
        foreach (string name in new[] { "Callsign", "callsign", "Call", "call" })
        {
            if (item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                call = value.GetString();
                return !string.IsNullOrWhiteSpace(call);
            }
        }

        call = null;
        return false;
    }
}
