using System;
using System.Collections.Generic;
using System.Linq;
using NeoRadar.PluginSdk.ControllerData;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Strips;
using Viff.NeoRadar.Tests.Fakes;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Strips;

public sealed class StripSyncTests
{
    private static readonly AirportView s_lfpg = new("LFPG", new AirportCapacity("LFPG", 60, 18, true), "LFPG_DEL");
    private static readonly HashSet<string> s_mastered = new(StringComparer.OrdinalIgnoreCase) { "LFPG" };

    private readonly FakeControllerDataApi _client = new();
    private readonly StripStore _store;
    private readonly StripWriter _writer;
    private readonly StripSync _sync;
    private readonly FakeChat _chat = new();

    public StripSyncTests()
    {
        _store = new StripStore(_client);
        _writer = new StripWriter(_store, _client, new FakeLogger());
        _sync = new StripSync(_store, _writer, _client, new Notifier(_chat, new FakeFeedback()));
    }

    [Fact]
    public void Writer_WritesOneStripPerTickControllerChangesFirst()
    {
        _writer.Change("BACKGROUND", s => s.With(StripData.Tsat, "120000"), urgent: false);
        _writer.Change("ACTION", s => s.With(StripData.Tsac, "1205"), urgent: true);

        Assert.True(_writer.Tick());
        Assert.Equal("ACTION", _client.StripWrites.Single().Callsign);
        Assert.True(_writer.Tick());
        Assert.False(_writer.Tick());
        Assert.Equal(2, _client.StripWrites.Count);
    }

    [Fact]
    public void Writer_FoldsChangesAndSkipsWhatTheStripAlreadyHolds()
    {
        _client.Boxes["AFR1"] = new[] { "/1205//////////", "", "", "", "", "", "", "", "" };
        _store.Refresh("AFR1");

        _writer.Change("AFR1", s => s.With(StripData.Tsac, "1205"), urgent: true);
        Assert.Equal(0, _writer.Waiting);

        _writer.Change("AFR1", s => s.With(StripData.Ctoc, "1230"), urgent: false);
        _writer.Change("AFR1", s => s.With(StripData.AutoStartup, "1"), urgent: false);
        _writer.Tick();

        StripData written = StripData.Parse(_client.StripWrites.Single().Text);
        Assert.Equal(("1205", "1230", "1"), (written[StripData.Tsac], written[StripData.Ctoc], written[StripData.AutoStartup]));
    }

    [Fact]
    public void Writer_GivesUpOnAStripTheClientKeepsRefusing()
    {
        _client.RefuseStripWrites = true;
        _writer.Change("AFR1", s => s.With(StripData.Tsac, "1205"), urgent: true);

        for (int i = 0; i < 10; i++)
        {
            _writer.Tick();
        }

        Assert.Equal(5, _client.StripWrites.Count);
        Assert.Equal(0, _writer.Waiting);
    }

    [Fact]
    public void Mirror_CopiesVIffTimesKeepsControllerFieldsAndFillsAPendingTsac()
    {
        _client.Boxes["AFR1"] = new[] { $"/{StripData.TsacPending}///////1230///", "", "", "", "", "", "", "", "" };
        _store.Refresh("AFR1");
        FlightView flight = Flight("AFR1", s_lfpg, c => c with { Tobt = "120000", Tsat = "120500", Ttot = "122300", Asrt = "1158", Deice = "STAND" });

        _sync.Mirror(new[] { flight }, s_mastered);
        _writer.Tick();

        StripData box = StripData.Parse(_client.StripWrites.Single().Text);
        Assert.Equal("1158", box[StripData.Asrt]);
        Assert.Equal("1200", box[StripData.Tobt]);
        Assert.Equal("120500", box[StripData.Tsat]);
        Assert.Equal("STND", box[StripData.DeIce]);
        Assert.Equal("1205", box[StripData.Tsac]);
        Assert.Equal("1230", box[StripData.Ctoc]);
    }

    [Fact]
    public void Mirror_LeavesAirportsThisClientDoesNotMaster()
    {
        AirportView lfpo = new("LFPO", new AirportCapacity("LFPO", 38, 12, true), "LFPO_TWR");

        _sync.Mirror(new[] { Flight("AFR2", lfpo, c => c with { Tsat = "120500" }) }, s_mastered);

        Assert.Equal(0, _writer.Waiting);
    }

    [Theory]
    [InlineData(true, GroundStatus.None, "", 0, true)]
    [InlineData(false, GroundStatus.None, "", 0, false)]
    [InlineData(true, GroundStatus.Push, "", 0, false)]
    [InlineData(true, GroundStatus.None, "LFPG_GND", 0, false)]
    [InlineData(true, GroundStatus.None, "", 10, false)]
    [InlineData(true, GroundStatus.None, "", -6, true)]
    [InlineData(true, GroundStatus.None, "", -7, false)]
    public void AutoStartup_SetsStupOnlyForAClearedFlightInItsTsatWindow(bool cleared, GroundStatus status, string owner, int minutesToTsat, bool expected)
    {
        DateTimeOffset now = TestData.Noon;
        string tsat = CdmTime.ToHhmmss(now.AddMinutes(minutesToTsat));
        _client.Boxes["AFR1"] = new[] { "//////////1/", "", "", "", "", "", "", "", "" };
        _store.Refresh("AFR1");
        _client.Models["AFR1"] = FakeControllerDataApi.Model("AFR1", cleared, status, owner);

        _sync.RunAutoStartup(new[] { Flight("AFR1", s_lfpg, c => c with { Tsat = tsat }) }, s_mastered, now);

        Assert.Equal(expected, _client.GroundStatusWrites.Contains(("AFR1", GroundStatus.Start)));
        if (expected)
        {
            _writer.Tick();
            Assert.Equal(StripData.AutoStartupDone, StripData.Parse(_client.StripWrites.Last().Text)[StripData.AutoStartup]);
            Assert.Single(_chat.Messages);
        }
    }

    [Fact]
    public void AutoStartup_FiresOnlyOnce()
    {
        _client.Boxes["AFR1"] = new[] { "//////////1/", "", "", "", "", "", "", "", "" };
        _store.Refresh("AFR1");
        _client.Models["AFR1"] = FakeControllerDataApi.Model("AFR1", cleared: true);
        FlightView flight = Flight("AFR1", s_lfpg, c => c with { Tsat = CdmTime.ToHhmmss(TestData.Noon) });

        _sync.RunAutoStartup(new[] { flight }, s_mastered, TestData.Noon);
        _client.Models["AFR1"] = FakeControllerDataApi.Model("AFR1", cleared: true);
        _sync.RunAutoStartup(new[] { flight }, s_mastered, TestData.Noon.AddSeconds(1));

        Assert.Single(_client.GroundStatusWrites);
    }

    private static FlightView Flight(string callsign, AirportView airport, Func<CdmData, CdmData> cdm) =>
        new(TestData.Departure(callsign, "1200", airport.Icao).WithCdm(cdm), airport, null, string.Empty);
}
