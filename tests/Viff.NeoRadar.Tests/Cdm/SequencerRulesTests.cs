using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.ControllerData;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Config;
using Viff.NeoRadar.Tests.Fakes;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Cdm;

public sealed class SequencerRulesTests
{
    private static readonly DateTimeOffset s_tobt = TestData.Noon.AddMinutes(10);

    [Fact]
    public void Build_SpacesOnlyTheSameOrDependentRunways()
    {
        SequenceInput a = Flight("A", "27L");
        SequenceInput b = Flight("B", "26R");

        Assert.All(Ttots(new SequenceRules { DefaultRate = 30 }, a, b).Values, t => Assert.Equal("1220", t));

        Dictionary<string, string> dependent = Ttots(new SequenceRules { DefaultRate = 30 },
            a with { Dependent = new[] { "26R" } }, b with { Dependent = new[] { "27L" } });
        Assert.Equal(("1220", "1222"), (dependent["A"], dependent["B"]));
    }

    [Fact]
    public void Build_UsesEachRunwaysOwnRate()
    {
        Dictionary<string, string> ttots = Ttots(new SequenceRules { DefaultRate = 30 },
            Flight("A", "27L") with { RatePerHour = 20 }, Flight("B", "27L") with { RatePerHour = 20 });

        Assert.Equal(("1220", "1223"), (ttots["A"], ttots["B"]));
    }

    [Fact]
    public void Build_KeepsSidIntervalsAcrossRunways()
    {
        SequenceRules rules = new() { DefaultRate = 30, SidInterval = (_, _) => TimeSpan.FromMinutes(5) };

        Dictionary<string, string> ttots = Ttots(rules, Flight("A", "27L"), Flight("B", "26R"));

        Assert.Equal(("1220", "1225"), (ttots["A"], ttots["B"]));
    }

    [Fact]
    public void Build_FillsTenMinuteBlocksUnderBmi()
    {
        Dictionary<string, string> ttots = Ttots(new SequenceRules { DefaultRate = 6, Bmi = true },
            Flight("A", "27L"), Flight("B", "27L"), Flight("C", "27L"));

        Assert.Equal(("1220", "1230", "1240"), (ttots["A"], ttots["B"], ttots["C"]));
    }

    [Fact]
    public void BlockCapacity_SpreadsTheRemainderOverTheHour() =>
        Assert.Equal(new[] { 7, 7, 6, 7, 7, 6 }, Enumerable.Range(0, 6).Select(i => Sequencer.BlockCapacity(40, i)));

    [Fact]
    public void Build_LetsFlightsStartEarlierUnderRunwayPressure()
    {
        SequenceInput[] flights = Enumerable.Range(1, 5).Select(i => Flight($"F{i}", "27L") with { PressureMinutes = 5 }).ToArray();

        Dictionary<string, SequenceSlot> slots = Sequencer.Build(flights, new SequenceRules { DefaultRate = 30 }, TestData.Noon)
            .ToDictionary(s => s.Callsign);

        Assert.Equal("1228", slots["F5"].Ttot.ToString("HHmm"));
        Assert.Equal("1213", slots["F5"].Tsat.ToString("HHmm"));
        Assert.Equal("1210", slots["F3"].Tsat.ToString("HHmm"));
    }

    private static SequenceInput Flight(string callsign, string runway) =>
        new(callsign, runway, s_tobt, TimeSpan.FromMinutes(10), null, false, null, null, false);

    private static Dictionary<string, string> Ttots(SequenceRules rules, params SequenceInput[] flights) =>
        Sequencer.Build(flights, rules, TestData.Noon).ToDictionary(s => s.Callsign, s => s.Ttot.ToString("HHmm"));
}

public sealed class MasterSequencingSetupTests
{
    private readonly FakeViffApi _api = new();
    private readonly CdmStore _store = new();
    private readonly AirportControls _controls = new(new FakeSettings());
    private CdmSetup _setup = CdmSetup.None;
    private readonly MasterSequencing _master;

    public MasterSequencingSetupTests()
    {
        _master = new MasterSequencing(_api, _store, _controls, new FakeLogger(), _ => { }, setup: () => _setup);
        _store.SetCapacity("LFPG", new AirportCapacity("LFPG", 30, 10, true), TestData.Noon);
    }

    [Fact]
    public async Task RunAsync_TakesTheRunwayRateFromRateTxt()
    {
        _setup = Setup(rates: "LFPG:A:*:*:D:27L:*:*:20_10");
        Load(TestData.Departure("AFR1", "1210"), TestData.Departure("AFR2", "1210"));

        await _master.RunAsync("LFPG", Traffic(("AFR1", null), ("AFR2", null)), TestData.Noon, CancellationToken.None);

        Assert.Equal("122300", _api.Writes.Single(w => w.Callsign == "AFR2").Ttot);
    }

    [Fact]
    public async Task RunAsync_ControllersRateOverridesRateTxt()
    {
        _setup = Setup(rates: "LFPG:A:*:*:D:27L:*:*:20_10");
        _controls.SetRates("LFPG", 60, 0);
        Load(TestData.Departure("AFR1", "1210"), TestData.Departure("AFR2", "1210"));

        await _master.RunAsync("LFPG", Traffic(("AFR1", null), ("AFR2", null)), TestData.Noon, CancellationToken.None);

        Assert.Equal("122100", _api.Writes.Single(w => w.Callsign == "AFR2").Ttot);
    }

    [Fact]
    public async Task RunAsync_TakesTheTaxiTimeFromTheZoneTheAircraftStandsIn()
    {
        _setup = Setup(zones: "LFPG:27L:49.00:2.50:49.01:2.50:49.01:2.51:49.00:2.51:20");
        Load(TestData.Departure("AFR1", "1210"));

        await _master.RunAsync("LFPG", Traffic(("AFR1", (49.005, 2.505))), TestData.Noon, CancellationToken.None);

        Assert.Equal("123000", Assert.Single(_api.Writes).Ttot);
    }

    [Fact]
    public async Task RunAsync_DoesNotSuspendWhenTheConfigNeverInvalidates()
    {
        _setup = Setup(xml: "<CDM><invalidateAtTsat mode=\"false\" /></CDM>");
        Load(TestData.Departure("SLOW", "1140").WithCdm(c => c with { Tobt = "114000", Tsat = "115300", Ttot = "120300" }));

        await _master.RunAsync("LFPG", Traffic(("SLOW", null)), TestData.Noon, CancellationToken.None);

        Assert.DoesNotContain(("SLOW", "SUSP"), _api.Dpis);
    }

    private static CdmSetup Setup(string xml = "<CDM />", string rates = "", string zones = "")
    {
        List<string> warnings = new();
        CdmSetup setup = new(CdmConfig.Parse(xml, warnings), RateTable.Parse(rates, warnings), TaxiZones.Parse(zones, warnings), SidIntervals.Empty, "test");
        Assert.Empty(warnings);
        return setup;
    }

    private static TrafficSnapshot Traffic(params (string Callsign, (double Lat, double Lon)? At)[] flights)
    {
        Dictionary<string, TrafficEntry> entries = flights.ToDictionary(
            f => f.Callsign,
            f => new TrafficEntry(GroundStatus.None, "LFPG", "27L", string.Empty, "M", "1210", string.Empty)
            {
                Latitude = f.At?.Lat,
                Longitude = f.At?.Lon,
            });
        return new TrafficSnapshot(entries, new Dictionary<string, (IReadOnlyList<string>, IReadOnlyList<string>)>
        {
            ["LFPG"] = (new[] { "27R" }, new[] { "27L" }),
        });
    }

    private void Load(params DepartureRecord[] records)
    {
        _api.Departures["LFPG"] = records.ToList();
        _store.ReplaceDepartures("LFPG", _api.Departures["LFPG"], TestData.Noon);
    }
}
