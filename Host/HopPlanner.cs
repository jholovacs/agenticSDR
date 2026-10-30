using AgenticSdr.Radio;

namespace AgenticSdr.Host;

/// <summary>
/// Frequencies to visit while scanning a band. Each hop overlaps the next so a signal hidden by the spike at the center of one capture is visible in a neighbor.
/// A band narrower than one capture is visited once, tuned beside the middle so the center spike is not sitting on the band.
/// </summary>
public static class HopPlanner
{
    public static IReadOnlyList<double> Centers(double startHz, double endHz, double sampleRateHz, double stepHz)
    {
        if (endHz <= startHz)
            throw new ArgumentException("Band end must be above the start.");

        double half = sampleRateHz / 2;
        if (endHz - startHz <= sampleRateHz)
        {
            double mid = (startHz + endHz) / 2;
            double room = half - (endHz - startHz) / 2;
            // A band this narrow is visited once. Shift the tune so the spike at the center of the capture falls outside the band.
            double shift = Math.Min(100_000, room * 0.5);
            return shift >= 5_000 ? [mid + shift] : [mid];
        }

        double first = startHz + half;
        double last = endHz - half;
        var centers = new List<double>();
        for (double cursor = first; cursor < last; cursor += stepHz)
            centers.Add(cursor);
        if (centers.Count == 0 || last - centers[^1] > 1_000)
            centers.Add(last);
        return centers;
    }
}

/// <summary>
/// Reads a Maidenhead grid, such as FN42, into a latitude and longitude.
/// Hams use these four- or six-character squares instead of a street address. An empty or malformed grid returns false.
/// </summary>
public static class Maidenhead
{
    public static bool TryParse(string? grid, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (string.IsNullOrWhiteSpace(grid))
            return false;

        string text = grid.Trim().ToUpperInvariant();
        if (text.Length is not (4 or 6))
            return false;
        if (text[0] is < 'A' or > 'R' || text[1] is < 'A' or > 'R')
            return false;
        if (!char.IsDigit(text[2]) || !char.IsDigit(text[3]))
            return false;
        if (text.Length == 6 && (text[4] is < 'A' or > 'X' || text[5] is < 'A' or > 'X'))
            return false;

        longitude = (text[0] - 'A') * 20 - 180;
        latitude = (text[1] - 'A') * 10 - 90;
        longitude += (text[2] - '0') * 2;
        latitude += text[3] - '0';
        if (text.Length == 6)
        {
            longitude += (text[4] - 'A') * (2.0 / 24);
            latitude += (text[5] - 'A') * (1.0 / 24);
            longitude += 2.0 / 48;
            latitude += 1.0 / 48;
        }
        else
        {
            longitude += 1;
            latitude += 0.5;
        }

        return true;
    }
}
