using System.Collections.Generic;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Cdm;

public sealed class EventSlotsAndStatusesTests
{
    private const string SlotsFile = """
        # Add info here for slot controlled events
        # format: [CID],[CTOT]
        9999999,RYR2889,1705
        8888888,1700
        7777777,VLG11P,LEBL,LEPA,1703
        bad line
        6666666,2460
        """;

    [Fact]
    public void Parse_ReadsTheThreeFormatsAndSkipsTheRest()
    {
        IReadOnlyList<EventSlot> slots = EventSlots.Parse(SlotsFile);

        Assert.Equal(3, slots.Count);
        Assert.Equal(new EventSlot("7777777", "VLG11P", "LEBL", "LEPA", "1703"), slots[2]);
    }

    [Theory]
    [InlineData("9999999", "RYR2889", "EIDW", "LEPA", "1705")]
    [InlineData("9999999", "RYR1234", "EIDW", "LEPA", "")]
    [InlineData("8888888", "ANY1", "LFPG", "EGLL", "1700")]
    [InlineData("7777777", "VLG99X", "LEBL", "LEPA", "1703")]
    [InlineData("7777777", "VLG99X", "LEBL", "LEMD", "")]
    [InlineData("", "RYR2889", "EIDW", "LEPA", "")]
    public void Find_MatchesOnCidThenCallsignOrRoute(string cid, string callsign, string departure, string destination, string expected)
    {
        EventSlots slots = new();
        slots.Replace(EventSlots.Parse(SlotsFile));

        Assert.Equal(expected, slots.Find(cid, callsign, departure, destination));
    }

    [Fact]
    public void OnTime_RoundsToWholeMinutes()
    {
        NetworkStatuses statuses = new();
        statuses.Replace(
            new Dictionary<string, string> { ["AFR1"] = "comply" },
            new Dictionary<string, string> { ["AFR1"] = "+12.6", ["AFR2"] = "-4.4", ["AFR3"] = "nonsense" });

        Assert.Equal("COMPLY", statuses.StatusOf("AFR1"));
        Assert.Equal(13, statuses.OnTimeOf("AFR1"));
        Assert.Equal(-4, statuses.OnTimeOf("AFR2"));
        Assert.Null(statuses.OnTimeOf("AFR3"));
        Assert.Null(statuses.OnTimeOf("NONE"));
    }

    [Fact]
    public void ViffJson_ReadsTheAtfcmListAndCallsignMaps()
    {
        const string relevant = """
            [{"callsign":"SWR2549","departure":"LSGG","arrival":"LFPG","eobt":"1600","tobt":"1600","ctot":"1605",
              "atfcmStatus":"SRM","isCdm":false,"atfcmData":{"mostPenalisingRegulation":"LF-FF_LMHJ-E","informed":true,"isRea":false}}]
            """;

        RelevantFlight flight = Assert.Single(ViffJson.ParseRelevant(relevant)!);
        Assert.Equal(("SWR2549", "1605", "LF-FF_LMHJ-E", "SRM", true), (flight.Callsign, flight.Ctot, flight.Regulation, flight.Status, flight.Informed));

        IReadOnlyDictionary<string, string> map = ViffJson.ParseCallsignMap("""[{"callsign":"AFR1","onTime":"-4.8"}]""", "onTime")!;
        Assert.Equal("-4.8", map["afr1"]);
    }
}
