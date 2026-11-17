namespace AgenticSdr.Shared;

/// <summary>
/// Local Agentic SDR apps the operator can open in the browser.
/// Each process listens on its own loopback port; the shared nav links them together.
/// </summary>
public sealed record DeskApp(
    string Id,
    string Name,
    string Url,
    string Kind,
    bool Available,
    string Blurb);

public static class DeskCatalog
{
    public const string ReceiverId = "receiver";
    public const string CwId = "cw";

    public static IReadOnlyList<DeskApp> All { get; } =
    [
        new(ReceiverId, "Receiver", "http://127.0.0.1:5088/", "receive", true,
            "Explore and Camp — receive only"),
        new(CwId, "CW", "http://127.0.0.1:5091/", "mode", true,
            "Morse desk — listen + experimental transmit"),
        new("ft8", "FT8", "", "mode", false, "Mode library ready — desk follows CW"),
        new("js8", "JS8", "", "mode", false, "Mode library ready — desk follows CW"),
        new("olivia", "Olivia", "", "mode", false, "Mode library ready — desk follows CW"),
        new("jtty", "JTTY", "", "mode", false, "Mode library ready — desk follows CW"),
        new("packet", "Packet", "", "mode", false, "Mode library ready — desk follows CW"),
        new("ribbit", "Ribbit", "", "mode", false, "Mode library ready — desk follows CW")
    ];

    public static object Payload(string currentId) => new
    {
        current = currentId,
        apps = All.Select(app => new
        {
            id = app.Id,
            name = app.Name,
            url = app.Url,
            kind = app.Kind,
            available = app.Available,
            blurb = app.Blurb,
            current = string.Equals(app.Id, currentId, StringComparison.OrdinalIgnoreCase)
        })
    };
}
