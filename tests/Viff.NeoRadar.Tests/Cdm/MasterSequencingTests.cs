using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.ControllerData;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Tests.Fakes;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Cdm;

public sealed class MasterSequencingTests
{
    private readonly FakeViffApi _api = new();
    private readonly CdmStore _store = new();
    private readonly AirportControls _controls = new(new FakeSettings());
    private readonly List<string> _notices = new();
    private readonly MasterSequencing _master;

    public MasterSequencingTests()
    {
        _master = new MasterSequencing(_api, _store, _controls, new FakeLogger(), _notices.Add);
        _store.SetCapacity("LFPG", new AirportCapacity("LFPG", 30, 12, true), TestData.Noon);
    }

    [Fact]
    public async Task RunAsync_SeedsTobtFromEobtAndWritesTheSequence()
    {
        Load(TestData.Departure("AFR1", "1210"), TestData.Departure("AFR2", "1210"));

        await RunAsync(TestData.Noon);

        Dictionary<string, CdmDataUpdate> writes = _api.Writes.ToDictionary(w => w.Callsign);
        Assert.Equal(("121000", "121000", "122000"), (writes["AFR1"].Tobt, writes["AFR1"].Tsat, writes["AFR1"].Ttot));
        Assert.Equal(("121000", "121200", "122200"), (writes["AFR2"].Tobt, writes["AFR2"].Tsat, writes["AFR2"].Ttot));
        Assert.Contains(("AFR1", "OBT/1210/10"), _api.Dpis);
        Assert.Contains(("AFR2", "OBT/1212/10"), _api.Dpis);
    }

    [Fact]
    public async Task RunAsync_WritesNothingTheSecondTimeWhenNothingChanged()
    {
        Load(TestData.Departure("AFR1", "1210"));
        await RunAsync(TestData.Noon);
        _api.Writes.Clear();
        _api.Dpis.Clear();

        await RunAsync(TestData.Noon.AddSeconds(15));

        Assert.Empty(_api.Writes);
        Assert.Empty(_api.Dpis);
    }

    [Fact]
    public async Task RunAsync_GivesNoTsatBeyondThirtyFiveMinutesBeforeTobt()
    {
        Load(TestData.Departure("LATER", "1240"));

        await RunAsync(TestData.Noon);

        CdmDataUpdate write = Assert.Single(_api.Writes);
        Assert.Equal(("124000", string.Empty), (write.Tobt, write.Tsat));
    }

    [Fact]
    public async Task RunAsync_AppliesATobtRequestAndClearsIt()
    {
        Load(TestData.Departure("AFR1", "1210").WithCdm(c => c with { Tobt = "121000", ReqTobt = "1215", ReqTobtType = "PILOT" }));

        await RunAsync(TestData.Noon);

        CdmDataUpdate write = Assert.Single(_api.Writes);
        Assert.Equal(("121500", "121500"), (write.Tobt, write.Tsat));
        Assert.Contains(("AFR1", "REQTOBT/NULL/NULL"), _api.Dpis);
        Assert.Equal("P", Assert.Single(_store.Flights()).TobtSetBy);
    }

    [Fact]
    public async Task RunAsync_AppliesAStartUpRequest()
    {
        Load(TestData.Departure("AFR1", "1210").WithCdm(c => c with { Tobt = "121000", ReqAsrt = "1158" }));

        await RunAsync(TestData.Noon);

        Assert.Equal("1158", Assert.Single(_api.Writes).Asrt);
        Assert.Contains(("AFR1", "REQASRT/NULL"), _api.Dpis);
    }

    [Fact]
    public async Task RunAsync_RemovesAFlightFromTheSequenceSixMinutesAfterItsTsat()
    {
        Load(TestData.Departure("SLOW", "1140").WithCdm(c => c with { Tobt = "114000", Tsat = "115300", Ttot = "120300" }));

        await RunAsync(TestData.Noon);

        CdmDataUpdate write = Assert.Single(_api.Writes);
        Assert.Equal((string.Empty, string.Empty), (write.Tsat, write.Ttot));

        _api.Writes.Clear();
        await RunAsync(TestData.Noon.AddSeconds(15));
        Assert.Empty(_api.Writes);
    }

    [Fact]
    public async Task RunAsync_KeepsAReadyFlightPastItsTsat()
    {
        Load(TestData.Departure("WAITING", "1140").WithCdm(c => c with { Tobt = "114000", Tsat = "115300", Ttot = "120300", Asrt = "1150" }));

        await RunAsync(TestData.Noon);

        Assert.Equal("120000", Assert.Single(_api.Writes).Tsat);
    }

    [Fact]
    public async Task RunAsync_LeavesStartedFlightsAlone()
    {
        Load(TestData.Departure("PUSHED", "1150").WithCdm(c => c with { Tobt = "115000", Tsat = "115500", Ttot = "120500" }));
        TrafficSnapshot traffic = new(new Dictionary<string, TrafficEntry>
        {
            ["PUSHED"] = new(GroundStatus.Push, "LFPG", "27L", "OPALE2A", "M", "1150", string.Empty),
        });

        await _master.RunAsync("LFPG", traffic, TestData.Noon, CancellationToken.None);

        CdmDataUpdate write = Assert.Single(_api.Writes);
        Assert.Equal(("115500", "120500", "27L/OPALE2A"), (write.Tsat, write.Ttot, write.DepInfo));
    }

    [Fact]
    public async Task RunAsync_AddsDeIcingTimeToTheTaxiTime()
    {
        _controls.SetDeIcePads("LFPG", "REM1=6");
        Load(TestData.Departure("ICED", "1210").WithCdm(c => c with { Tobt = "121000", Deice = "REMOTE/REM1" }));
        TrafficSnapshot traffic = new(new Dictionary<string, TrafficEntry>
        {
            ["ICED"] = new(GroundStatus.None, "LFPG", "27L", string.Empty, "H", "1210", string.Empty),
        });

        await _master.RunAsync("LFPG", traffic, TestData.Noon, CancellationToken.None);

        Assert.Equal("123800", Assert.Single(_api.Writes).Ttot);
    }

    [Fact]
    public async Task RunAsync_UsesTheLvoRateWhenLvoIsOn()
    {
        _controls.SetRates("LFPG", 0, 10);
        _controls.ToggleLvo("LFPG");
        Load(TestData.Departure("AFR1", "1210"), TestData.Departure("AFR2", "1210"));

        await RunAsync(TestData.Noon);

        Assert.Equal("122600", _api.Writes.Single(w => w.Callsign == "AFR2").Ttot);
    }

    [Fact]
    public async Task RunAsync_DoesNothingAtANonCdmAirport()
    {
        _store.SetCapacity("LFPG", new AirportCapacity("LFPG", 30, 12, false), TestData.Noon);
        Load(TestData.Departure("AFR1", "1210"));

        await RunAsync(TestData.Noon);

        Assert.Empty(_api.Writes);
        Assert.Single(_notices);
    }

    [Fact]
    public async Task RunAsync_HoldsTheTakeOffToACtotFromTheNetworkList()
    {
        Load(TestData.Departure("AFR1", "1210"), TestData.Departure("AFR2", "1210"));
        _store.SetNetworkCtots(new[] { new RestrictedFlight("AFR1", "1240", "LSGG", string.Empty, string.Empty) });

        await RunAsync(TestData.Noon);

        Dictionary<string, CdmDataUpdate> writes = _api.Writes.ToDictionary(w => w.Callsign);
        Assert.Equal(("123000", "124000", "1240"), (writes["AFR1"].Tsat, writes["AFR1"].Ttot, writes["AFR1"].Ctot));
        Assert.Equal("122000", writes["AFR2"].Ttot);
        Assert.DoesNotContain(_api.Dpis, d => d.Callsign == "AFR1" && d.Value.StartsWith("OBT/", StringComparison.Ordinal));
    }

    private void Load(params DepartureRecord[] records) => _store.ReplaceDepartures("LFPG", records, TestData.Noon);

    private Task RunAsync(DateTimeOffset now) => _master.RunAsync("LFPG", TrafficSnapshot.Empty, now, CancellationToken.None);
}
