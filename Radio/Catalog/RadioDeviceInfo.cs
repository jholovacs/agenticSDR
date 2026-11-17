namespace AgenticSdr.Radio;

/// <summary>
/// One radio the program can see, before it is opened.
/// Index is the position in the combined list. Driver is "rtlsdr" for the built-in path.
/// DriverIndex is that dongle's own number. OpenArgs is the SoapySDR string used for every other radio.
/// </summary>
public sealed record RadioDeviceInfo(
    int Index,
    string Name,
    string? Manufacturer,
    string? Product,
    string? Serial,
    string Driver = "rtlsdr",
    int DriverIndex = 0,
    string? OpenArgs = null);
