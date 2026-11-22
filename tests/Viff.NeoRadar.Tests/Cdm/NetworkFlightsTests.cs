using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Airport;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Tags;
using Viff.NeoRadar.Tests.Fakes;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Cdm;

/// <summary>
/// Flights departing from airports the controller does not watch: their CTOTs, and their TSATs where the airport has a master.
/// </summary>
public sealed class NetworkFlightsTests
{
    private readonly FakeViffApi _api = new();
    private readonly CdmStore _store = new();
    private readonly FakeAirportApi _airports = new();
    private readonly FakeFlightplanApi _flightplans = new();
    private readonly ManualClock _clock = new(TestData.Noon);
    private readonly CdmSync _sync;

    public NetworkFlightsTests()
    {
        AirportControls controls = new(new FakeSettings());
        MasterSequencing master = new(_api, _store, controls, new FakeLogger(), _ => { });
        _sync = new CdmSync(_api, _store, controls, master, new ConnectionGate(FakeFsd.LiveAtc("LFPG_DEL")), _airports, _flightplans,
            new FakeControllerDataApi(), _clock);

        _airports.Configurations.Add(new AirportConfig("LFPG", Array.Empty<string>(), new[] { "27L" }, AirportStatus.Active));
        _api.Capacities["LFPG"] = new AirportCapacity("LFPG", 60, 18, true);
        _api.Masters.Add(new MasterAirport("LSGG", "LSGG_DEL"));

        _flightplans.Add("AFR1", "LFPG", "LSGG", "1210");
        _flightplans.Add("SWR2549", "LSGG", "LFPG", "1205");
        _flightplans.Add("DLH9", "EDDM", "LFPG", "1215");
        _flightplans.Add("BAW1", "EGLL", "LFPG", "1100");

        _api.Departures["LFPG"] = new List<DepartureRecord> { TestData.Departure("AFR1", "1210") };
        _api.Departures["LSGG"] = new List<DepartureRecord>
        {
            TestData.Departure("SWR2549", "1205", "LSGG").WithCdm(c => c with { Tobt = "120500", Tsat = "121000", Ttot = "122000" }),
            TestData.Departure("EZY1", "1100", "LSGG") with { Atot = "1112" },
        };
        _api.Restricted.Add(new RestrictedFlight("AFR1", "1240", "LSGG", string.Empty, "COMPLY"));
        _api.Restricted.Add(new RestrictedFlight("DLH9", "1250", "LF-FF_LMHJ-E", string.Empty, string.Empty));
        _api.Restricted.Add(new RestrictedFlight("BAW1", "1130", "LFPG", "1128", "AIRB"));
        _api.Restricted.Add(new RestrictedFlight("KLM5", "1300", "EHAM", string.Empty, string.Empty));
    }

    [Fact]
    public async Task RunAsync_ReadsOtherMasteredAirportsTheClientHasTrafficFromAndNoOthers()
    {
        await _sync.RunAsync(CancellationToken.None);

        Assert.Contains("LSGG", _api.DepartureReads);
        Assert.DoesNotContain("EDDM", _api.DepartureReads);
        Assert.DoesNotContain("EGLL", _api.DepartureReads);
    }

    [Fact]
    public async Task RunAsync_ReadsAnOtherAirportAtMostOnceAMinute()
    {
        await _sync.RunAsync(CancellationToken.None);
        _clock.Now = _clock.Now.AddSeconds(15);
        await _sync.RunAsync(CancellationToken.None);
        Assert.Single(_api.DepartureReads, "LSGG");

        _clock.Now = _clock.Now.AddMinutes(1);
        await _sync.RunAsync(CancellationToken.None);
        Assert.Equal(2, _api.DepartureReads.Count(a => a == "LSGG"));
    }

    [Fact]
    public async Task NetworkFlights_ShowsOtherAirportsTsatsAndEveryCtotTheClientHolds()
    {
        await _sync.RunAsync(CancellationToken.None);

        Dictionary<string, FlightView> flights = _store.NetworkFlights(_sync.LatestTraffic).ToDictionary(f => f.Record.Callsign);

        Assert.Equal(new[] { "DLH9", "SWR2549" }, flights.Keys.OrderBy(k => k));
        Assert.Equal("121000", flights["SWR2549"].Record.Cdm.Tsat);
        Assert.True(flights["SWR2549"].Airport.IsCdm);
        Assert.Equal(("EDDM", "1250", "LF-FF_LMHJ-E"), (flights["DLH9"].Record.Departure, flights["DLH9"].Record.Ctot, flights["DLH9"].Record.Regulation));
        Assert.All(flights.Values, f => Assert.False(f.Watched));
    }

    [Fact]
    public async Task NetworkCtot_FillsAWatchedDepartureTheAirportFeedLeftWithout()
    {
        await _sync.RunAsync(CancellationToken.None);

        DepartureRecord afr = _store.Find("AFR1")!;
        Assert.Equal(("1240", "LSGG"), (afr.EffectiveCtot, afr.Regulation));
        Assert.Equal("1240", Assert.Single(_store.Flights()).Record.Ctot);
    }

    [Fact]
    public async Task Format_ShowsNoRdyOrMsgOnAFlightFromElsewhere()
    {
        await _sync.RunAsync(CancellationToken.None);
        FlightView swiss = _store.NetworkFlights(_sync.LatestTraffic).Single(f => f.Record.Callsign == "SWR2549");

        FlightTags tags = CdmTagFormatter.Format(swiss, TestData.Noon, flashOn: true);

        Assert.Equal(string.Empty, tags.Items[CdmItem.Ready].Text);
        Assert.Equal(string.Empty, tags.Items[CdmItem.Message].Text);
        Assert.Equal("1210", tags.Items[CdmItem.Tsat].Text);
    }
}
