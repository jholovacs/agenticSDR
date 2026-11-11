using AgenticSdr.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticSdr.Tests;

/// <summary>Checks callsign parsing, HamDB/DXCC JSON, cache enrichment, and DX labels without the network.</summary>
public class CallsignLookupTests
{
    [Theory]
    [InlineData("CQ K1ABC FN42", new[] { "K1ABC" })]
    [InlineData("K1ABC W9XYZ -12", new[] { "K1ABC", "W9XYZ" })]
    [InlineData("CQ DX JA1ABC", new[] { "JA1ABC" })]
    [InlineData("W1AW/P is calling", new[] { "W1AW" })]
    public void Digi_text_yields_callsigns_not_grids(string text, string[] expected)
    {
        Assert.Equal(expected, CallsignLookup.ExtractCallsigns(text));
    }

    [Fact]
    public void Hamdb_json_builds_a_us_card()
    {
        const string json = """
            {
              "hamdb":{
                "callsign":{
                  "call":"W1AW",
                  "fname":"",
                  "mi":"",
                  "name":"ARRL HQ OPERATORS CLUB",
                  "addr2":"NEWINGTON",
                  "state":"CT",
                  "country":"United States",
                  "grid":"FN31pr"
                },
                "messages":{ "status":"OK" }
              }
            }
            """;
        CallsignInfo? info = CallsignLookup.ParseHamDb("W1AW", json);
        Assert.NotNull(info);
        Assert.True(info!.Found);
        Assert.Equal("ARRL HQ OPERATORS CLUB", info.Name);
        Assert.Equal("NEWINGTON", info.City);
        Assert.Contains("ARRL HQ OPERATORS CLUB", info.Label, StringComparison.Ordinal);
        Assert.Contains("Newington", info.Label, StringComparison.Ordinal);
        Assert.Contains("CT", info.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Dxcc_json_labels_japan_without_a_callbook_login()
    {
        const string json = """
            {
              "callsign":"JA1ABC",
              "details":"Japan (Kanto)",
              "continent":"AS",
              "entity":{ "adif":339, "name":"Japan", "continent":"AS" }
            }
            """;
        CallsignInfo? info = CallsignLookup.ParseDxcc("JA1ABC", json, prior: null);
        Assert.NotNull(info);
        Assert.True(info!.Found);
        Assert.Equal("Japan", info.Country);
        Assert.Equal("AS", info.Continent);
        Assert.Equal("Japan", info.Label);
        Assert.Equal("Japan (Kanto)", info.Note);
    }

    [Fact]
    public void Dxcc_merges_onto_hamdb_when_name_is_known()
    {
        var prior = new CallsignInfo(
            "DL1ABC",
            true,
            "Klaus-Dietmar Brackmann",
            "Einbeck",
            null,
            "Germany",
            "JO41xt",
            null,
            null,
            "hamdb",
            DateTimeOffset.UtcNow);
        const string json = """
            {
              "callsign":"DL1ABC",
              "details":"Fed. Rep. of Germany",
              "continent":"EU",
              "entity":{ "name":"Fed. Rep. of Germany", "continent":"EU" }
            }
            """;
        CallsignInfo? merged = CallsignLookup.ParseDxcc("DL1ABC", json, prior);
        Assert.NotNull(merged);
        Assert.Equal("Klaus-Dietmar Brackmann", merged!.Name);
        Assert.Equal("EU", merged.Continent);
        Assert.Contains("hamdb", merged.Source, StringComparison.Ordinal);
        Assert.Contains("hamqth-dxcc", merged.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void EnrichText_uses_cache_without_blocking_on_the_network()
    {
        string path = Path.Combine(Path.GetTempPath(), "agentic-sdr-call-cache-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {
                  "JA1ABC": {
                    "call": "JA1ABC",
                    "found": true,
                    "country": "Japan",
                    "continent": "AS",
                    "dxccDetail": "Japan (Kanto)",
                    "source": "hamqth-dxcc",
                    "fetchedUtc": "2099-01-01T00:00:00+00:00"
                  }
                }
                """);
            var cached = new CallsignLookup(
                new HttpClient(),
                NullLogger<CallsignLookup>.Instance,
                path,
                enabled: true);
            Assert.Equal("Japan", cached.TryGet("JA1ABC")?.Label);
            string enriched = cached.EnrichText("JA1ABC is calling anyone (CQ).");
            Assert.Contains("JA1ABC (Japan)", enriched, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
