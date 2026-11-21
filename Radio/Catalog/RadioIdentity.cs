namespace AgenticSdr.Radio;

/// <summary>
/// Stable identity for a USB radio across replugs and process restarts.
/// Prefers driver + serial; falls back to open-args or driver index when the stick has no serial.
/// </summary>
public static class RadioIdentity
{
    public static string Key(RadioDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        string driver = string.IsNullOrWhiteSpace(info.Driver) ? "unknown" : info.Driver.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(info.Serial))
            return $"{driver}:serial:{info.Serial.Trim()}";
        if (!string.IsNullOrWhiteSpace(info.OpenArgs))
            return $"{driver}:args:{info.OpenArgs.Trim()}";
        return $"{driver}:index:{info.DriverIndex}";
    }
}
