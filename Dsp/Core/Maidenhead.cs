namespace AgenticSdr.Dsp;

/// <summary>
/// Reads a Maidenhead grid, such as FN42, into a latitude and longitude,
/// and names the region that square roughly covers.
/// Hams use these four- or six-character squares instead of a street address.
/// </summary>
public static class Maidenhead
{
    private readonly record struct Region(string Name, double MinLat, double MaxLat, double MinLon, double MaxLon);

    /// <summary>
    /// Named places from most local to broad. Describe picks the smallest box that contains the grid center.
    /// </summary>
    private static readonly Region[] Regions =
    [
        // North America — metros and regions
        new("Mexico City", 18.8, 20.2, -100.0, -98.5),
        new("Los Angeles", 33.2, 34.8, -119.0, -117.2),
        new("San Francisco Bay Area", 37.0, 38.5, -123.0, -121.5),
        new("San Diego", 32.3, 33.4, -117.6, -116.5),
        new("Phoenix", 32.8, 34.0, -112.8, -111.3),
        new("Denver", 39.2, 40.4, -105.5, -104.2),
        new("Dallas–Fort Worth", 32.2, 33.5, -97.8, -96.2),
        new("Houston", 29.2, 30.4, -96.0, -94.8),
        new("Chicago", 41.3, 42.4, -88.5, -87.2),
        new("Detroit", 41.8, 42.9, -83.8, -82.5),
        new("Atlanta", 33.2, 34.4, -85.0, -83.8),
        new("Miami", 25.2, 26.5, -80.8, -79.8),
        new("Washington, D.C. area", 38.4, 39.4, -77.8, -76.5),
        new("Philadelphia", 39.5, 40.5, -75.8, -74.7),
        new("New York City", 40.4, 41.1, -74.5, -73.5),
        new("Boston", 42.0, 42.8, -71.6, -70.5),
        new("southern New England", 41.0, 42.6, -73.0, -69.8),
        new("upstate New York", 42.0, 45.0, -79.8, -73.2),
        new("Pacific Northwest", 44.5, 49.0, -125.0, -116.5),
        new("southern California", 32.4, 35.0, -121.0, -114.5),
        new("northern California", 37.5, 42.0, -124.5, -119.5),
        new("Texas", 25.8, 36.5, -106.7, -93.5),
        new("Florida", 24.4, 31.0, -87.7, -79.8),
        new("Great Lakes", 41.0, 49.5, -93.0, -76.0),
        new("New England", 41.0, 47.5, -73.5, -66.8),
        new("mid-Atlantic United States", 36.5, 41.5, -80.5, -73.8),
        new("southeastern United States", 24.5, 36.5, -92.0, -75.0),
        new("Midwest United States", 36.5, 49.5, -104.0, -80.5),
        new("Mountain West United States", 31.0, 49.0, -115.0, -102.0),
        new("Alaska", 51.0, 72.0, -170.0, -129.0),
        new("Hawaii", 18.5, 22.5, -160.5, -154.5),
        new("Ontario", 41.5, 56.5, -95.0, -74.0),
        new("Quebec", 44.5, 62.5, -80.0, -57.0),
        new("British Columbia", 48.0, 60.0, -139.0, -114.0),
        new("prairie Canada", 48.5, 60.0, -120.0, -95.0),
        new("Maritime Canada", 43.0, 52.0, -67.0, -52.0),
        new("Canada", 41.5, 83.0, -141.0, -52.0),
        new("contiguous United States", 24.4, 49.4, -125.0, -66.8),
        new("northern Mexico", 24.0, 32.5, -117.0, -97.0),
        new("southern Mexico", 14.5, 24.0, -106.0, -86.5),
        new("Mexico", 14.5, 32.7, -118.5, -86.5),
        new("Central America", 7.0, 18.5, -92.5, -77.0),
        new("Caribbean", 10.0, 27.5, -85.0, -59.0),
        new("Colombia", -4.5, 13.0, -79.5, -66.5),
        new("Venezuela", 0.5, 12.5, -73.5, -59.5),
        new("Brazil", -34.0, 5.5, -74.0, -34.5),
        new("Argentina", -55.5, -21.5, -73.5, -53.0),
        new("Chile", -56.0, -17.5, -76.0, -66.0),
        new("South America", -56.0, 13.0, -82.0, -34.0),

        // Europe
        new("London", 51.0, 52.0, -1.0, 1.0),
        new("Paris", 48.4, 49.3, 1.8, 3.0),
        new("Berlin", 52.0, 53.0, 12.8, 14.0),
        new("Rome", 41.5, 42.3, 12.0, 13.0),
        new("Madrid", 40.0, 41.0, -4.2, -3.2),
        new("Moscow", 55.2, 56.3, 36.8, 38.5),
        new("southern England", 50.0, 52.5, -6.0, 2.0),
        new("Scotland", 54.5, 61.0, -8.0, -0.5),
        new("Ireland", 51.2, 55.5, -10.5, -5.5),
        new("Iberian Peninsula", 35.8, 44.0, -10.0, 4.5),
        new("France", 41.2, 51.5, -5.5, 8.5),
        new("Germany", 47.2, 55.2, 5.5, 15.5),
        new("Italy", 36.5, 47.2, 6.5, 19.0),
        new("Scandinavia", 54.5, 71.5, 4.0, 32.0),
        new("eastern Europe", 44.0, 60.0, 15.0, 40.0),
        new("Balkans", 34.5, 48.0, 13.0, 30.0),
        new("western Europe", 35.5, 60.0, -11.0, 15.0),
        new("Europe", 34.5, 72.0, -25.0, 45.0),

        // Africa / Middle East
        new("Cairo", 29.5, 30.5, 30.8, 31.8),
        new("South Africa", -35.0, -22.0, 16.0, 33.0),
        new("North Africa", 15.0, 38.0, -18.0, 40.0),
        new("West Africa", 4.0, 18.0, -18.0, 15.0),
        new("East Africa", -12.0, 18.0, 28.0, 52.0),
        new("Africa", -35.0, 38.0, -18.0, 52.0),
        new("Israel", 29.4, 33.4, 34.2, 35.9),
        new("Arabian Peninsula", 12.0, 32.5, 34.0, 60.0),
        new("Turkey", 35.8, 42.5, 26.0, 45.0),
        new("Middle East", 12.0, 42.5, 26.0, 63.0),

        // Asia
        new("Beijing", 39.4, 40.6, 115.8, 117.2),
        new("Shanghai", 30.8, 31.8, 120.8, 122.0),
        new("Hong Kong", 22.0, 22.7, 113.8, 114.5),
        new("Tokyo", 35.3, 36.0, 139.3, 140.2),
        new("Osaka", 34.3, 35.0, 135.2, 136.0),
        new("Seoul", 37.2, 37.9, 126.6, 127.4),
        new("Taipei", 24.8, 25.4, 121.3, 122.0),
        new("Singapore", 1.1, 1.5, 103.6, 104.1),
        new("Bangkok", 13.4, 14.2, 100.2, 101.0),
        new("Mumbai", 18.7, 19.5, 72.6, 73.2),
        new("Delhi", 28.3, 29.0, 76.8, 77.5),
        new("central China", 28.0, 36.0, 105.0, 118.0),
        new("northern China", 36.0, 45.0, 105.0, 125.0),
        new("southern China", 18.0, 28.0, 105.0, 122.0),
        new("western China", 28.0, 45.0, 75.0, 105.0),
        new("China", 18.0, 54.0, 73.0, 135.0),
        new("Japan", 30.0, 46.0, 128.0, 146.0),
        new("South Korea", 33.0, 39.0, 124.5, 130.0),
        new("Taiwan", 21.8, 25.5, 119.5, 122.5),
        new("Southeast Asia", -11.0, 23.5, 92.0, 141.0),
        new("India", 6.5, 36.0, 68.0, 97.5),
        new("Central Asia", 35.0, 55.0, 46.0, 87.0),
        new("Siberia", 50.0, 77.0, 60.0, 180.0),
        new("Russia", 41.0, 82.0, 27.0, 180.0),
        new("Asia", -11.0, 77.0, 26.0, 180.0),

        // Oceania / Pacific
        new("Sydney", -34.3, -33.4, 150.6, 151.5),
        new("Melbourne", -38.3, -37.5, 144.6, 145.4),
        new("Auckland", -37.3, -36.5, 174.4, 175.2),
        new("eastern Australia", -39.0, -10.5, 140.0, 154.0),
        new("western Australia", -36.0, -13.5, 112.0, 129.0),
        new("Australia", -44.0, -10.0, 112.0, 154.0),
        new("New Zealand", -47.5, -34.0, 166.0, 179.0),
        new("Pacific islands", -25.0, 25.0, 130.0, -130.0), // wrapped; handled specially below

        // Oceans / polar (broad fallbacks)
        new("North Atlantic", 20.0, 65.0, -80.0, -10.0),
        new("South Atlantic", -60.0, 0.0, -70.0, 20.0),
        new("North Pacific", 0.0, 60.0, 140.0, -100.0),
        new("South Pacific", -60.0, 0.0, 140.0, -70.0),
        new("Indian Ocean", -60.0, 25.0, 20.0, 120.0),
        new("Arctic", 66.0, 90.0, -180.0, 180.0),
        new("Antarctica", -90.0, -60.0, -180.0, 180.0),
    ];

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

    /// <summary>
    /// Plain place name for a Maidenhead square, such as “upstate New York” or “central China”.
    /// Returns null when the grid cannot be parsed.
    /// </summary>
    public static string? Describe(string? grid)
    {
        if (!TryParse(grid, out double latitude, out double longitude))
            return null;

        string? best = null;
        double bestArea = double.MaxValue;
        foreach (Region region in Regions)
        {
            if (!Contains(region, latitude, longitude))
                continue;
            double area = Area(region);
            if (area >= bestArea)
                continue;
            bestArea = area;
            best = region.Name;
        }

        return best;
    }

    private static bool Contains(Region region, double latitude, double longitude)
    {
        if (latitude < region.MinLat || latitude > region.MaxLat)
            return false;

        if (region.MinLon <= region.MaxLon)
            return longitude >= region.MinLon && longitude <= region.MaxLon;

        // Box that crosses the antimeridian (for example Pacific islands).
        return longitude >= region.MinLon || longitude <= region.MaxLon;
    }

    private static double Area(Region region)
    {
        double height = region.MaxLat - region.MinLat;
        double width = region.MinLon <= region.MaxLon
            ? region.MaxLon - region.MinLon
            : (180 - region.MinLon) + (region.MaxLon + 180);
        return height * width;
    }
}
