using System.Net.Http.Headers;
using System.Text.Json;
using AgenticSdr.Dsp;
using AgenticSdr.Host;

namespace AgenticSdr.Station;

/// <summary>
/// A Maidenhead square turned into coordinates, or the square HamDB publishes for a call sign.
/// This is a location hint. It does not check a license and it does not transmit.
/// </summary>
public sealed record GridPlace(
    string Grid,
    double Latitude,
    double Longitude,
    string Coordinates,
    string? Place,
    string? Licensee,
    string Source)
{
    public string Sentence =>
        Licensee is null
            ? $"{Grid} is {Coordinates}" + (Place is null ? "." : $", {Place}.")
            : $"{Licensee} is listed at {Grid}, {Coordinates}" + (Place is null ? "." : $", {Place}.");
}

public static class StationGrid
{
    static readonly HttpClient Http = Create();

    /// <summary>Blank means no grid. Anything else has to be a 4- or 6-character square.</summary>
    public static GridPlace? Optional(string? grid)
    {
        if (string.IsNullOrWhiteSpace(grid))
            return null;
        return Require(grid);
    }

    public static GridPlace Require(string grid)
    {
        string text = grid.Trim().ToUpperInvariant();
        if (!Maidenhead.TryParse(text, out double latitude, out double longitude))
            throw new ArgumentException("Enter a Maidenhead grid such as FN42 or FN42kp.");
        return new GridPlace(
            text,
            latitude,
            longitude,
            Format(latitude, longitude),
            Maidenhead.Describe(text),
            null,
            "grid");
    }

    /// <summary>A typed grid wins. An empty grid looks up the call sign's published square.</summary>
    public static async Task<GridPlace> LookupAsync(string? grid, string? callSign, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(grid))
            return Require(grid);
        if (string.IsNullOrWhiteSpace(callSign))
            throw new ArgumentException("Enter a grid such as FN42, or a call sign to look up.");
        return await LookupCallAsync(callSign, cancellationToken).ConfigureAwait(false);
    }

    public static GridPlace FromHamDb(string callSign, string json)
    {
        if (!CallSign.TryNormalize(callSign, out string display, out string baseCall))
            throw new ArgumentException("Enter a US amateur call sign, such as W1AW or KA1ABC. A portable indicator like W1AW/7 is allowed.");

        CallsignInfo? info;
        try
        {
            info = CallsignLookup.ParseHamDb(baseCall, json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("HamDB returned something this desk could not read.");
        }

        if (info is null || !info.Found || string.IsNullOrWhiteSpace(info.Grid))
            throw new InvalidOperationException($"HamDB has no grid square for {display}.");

        GridPlace place;
        try
        {
            place = Require(info.Grid);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"HamDB listed {info.Grid.Trim()} for {display}, which is not a 4- or 6-character grid.");
        }

        return place with { Licensee = string.IsNullOrWhiteSpace(info.Label) ? display : $"{display} ({info.Label})", Source = "hamdb" };
    }

    public static string Format(double latitude, double longitude)
    {
        string ns = latitude >= 0 ? "N" : "S";
        string ew = longitude >= 0 ? "E" : "W";
        return FormattableString.Invariant($"{Math.Abs(latitude):0.00}° {ns}, {Math.Abs(longitude):0.00}° {ew}");
    }

    static async Task<GridPlace> LookupCallAsync(string callSign, CancellationToken cancellationToken)
    {
        if (!CallSign.TryNormalize(callSign, out _, out string baseCall))
            throw new ArgumentException("Enter a US amateur call sign, such as W1AW or KA1ABC. A portable indicator like W1AW/7 is allowed.");

        string url = $"https://api.hamdb.org/{Uri.EscapeDataString(baseCall)}/json/agenticsdr";
        try
        {
            string body = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return FromHamDb(callSign, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException("HamDB could not be reached. Enter the grid square yourself.");
        }
    }

    static HttpClient Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AgenticSDR", "1"));
        return http;
    }
}
