using System;
using System.Linq;
using NeoRadar.PluginSdk.Feedback;
using NeoRadar.PluginSdk.Fsd;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Tests.Fakes;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Cdm;

public sealed class FlightActionsTests
{
    private const string Me = "LFPG_DEL";

    private readonly FakeViffApi _api = new();
    private readonly CdmStore _store = new();
    private readonly FakeFsd _fsd = FakeFsd.LiveAtc(Me);
    private readonly FakeFeedback _feedback = new();
    private readonly ManualClock _clock = new(TestData.Noon);
    private readonly JobScheduler _scheduler;
    private readonly FlightActions _actions;

    public FlightActionsTests()
    {
        _scheduler = new JobScheduler(_clock, new FakeLogger());
        _actions = new FlightActions(_api, _store, new ConnectionGate(_fsd), _scheduler, new Notifier(new FakeChat(), _feedback), _clock, () => { });
        _store.SetCapacity("LFPG", new AirportCapacity("LFPG", 30, 12, true), TestData.Noon);
        _api.Departures["LFPG"] = new() { TestData.Departure("AFR1", "1210").WithCdm(c => c with { Tobt = "121000", Tsat = "121500", Ttot = "122500" }) };
        Poll();
    }

    [Fact]
    public void ReadyTobt_AsMasterWritesTobtAndRdyAndClearsTheTsatForResequencing()
    {
        _store.SetMaster("LFPG", Me);

        _actions.ReadyTobt("AFR1");
        Drain();

        CdmDataUpdate write = Assert.Single(_api.Writes);
        Assert.Equal(("120000", string.Empty, "1200"), (write.Tobt, write.Tsat, write.Asrt));
        Assert.Empty(_api.Dpis);
        Assert.Equal("121000", _store.Find("AFR1")!.Cdm.Tobt);

        Poll();
        Assert.Equal(("120000", "A"), (_store.Find("AFR1")!.Cdm.Tobt, Assert.Single(_store.Flights()).TobtSetBy));
    }

    [Fact]
    public void ReadyTobt_UnderAnotherMasterSendsARequestAndReaWithACtot()
    {
        _store.SetMaster("LFPG", "LFPG_TWR");
        _store.Update("AFR1", r => r with { Ctot = "1240" });

        _actions.ReadyTobt("AFR1");
        Drain();

        Assert.Empty(_api.Writes);
        Assert.Equal(new[] { ("AFR1", "REQTOBT/1200/ATC"), ("AFR1", "REA/1") }, _api.Dpis);
    }

    [Fact]
    public void EditTobt_AtANonCdmAirportSendsTheNewEobt()
    {
        _store.SetCapacity("LFPG", new AirportCapacity("LFPG", 30, 12, false), TestData.Noon);

        _actions.EditTobt("AFR1", "1230");
        Drain();

        Assert.Equal(("AFR1", "EOBT/1230"), Assert.Single(_api.Dpis));
    }

    [Fact]
    public void EditTobt_RefusesWhatIsNotATime()
    {
        _actions.EditTobt("AFR1", "12:30");
        Drain();

        Assert.Empty(_api.Writes);
        Assert.Contains(_feedback.Toasts, t => t.Kind == FeedbackKind.Warning);
    }

    [Fact]
    public void ToggleReady_UnderAnotherMasterRequestsStartUpButCannotClearIt()
    {
        _store.SetMaster("LFPG", "LFPG_TWR");

        _actions.ToggleReady("AFR1", withRea: true);
        Drain();
        Assert.Equal(new[] { ("AFR1", "REQASRT/1200"), ("AFR1", "REA/1") }, _api.Dpis);

        _store.Update("AFR1", r => r.WithCdm(c => c with { Asrt = "1200" }));
        _api.Dpis.Clear();
        _actions.ToggleReady("AFR1", withRea: false);
        Drain();
        Assert.Empty(_api.Dpis);
    }

    [Fact]
    public void ToggleReady_WithNoMasterWritesAndClearsTheAsrtDirectly()
    {
        _actions.ToggleReady("AFR1", withRea: false);
        Drain();
        Assert.Equal("1200", Assert.Single(_api.Writes).Asrt);

        Poll();
        _actions.ToggleReady("AFR1", withRea: false);
        Drain();
        Assert.Equal(string.Empty, _api.Writes.Last().Asrt);
    }

    [Fact]
    public void SetManualCtot_StoresItWithTheManualReason()
    {
        _actions.SetManualCtot("AFR1", "1245");
        Drain();

        CdmDataUpdate write = Assert.Single(_api.Writes);
        Assert.Equal(("1245", CdmDataUpdate.ManualReason), (write.Ctot, write.Reason));
        Poll();
        Assert.True(_store.Find("AFR1")!.HasManualCtot);
    }

    [Fact]
    public void Actions_AreRefusedOutsideTheLiveNetwork()
    {
        _fsd.Connection = _fsd.Connection! with { ServerType = ServerType.Sweatbox };

        _actions.ReadyTobt("AFR1");
        Drain();

        Assert.Empty(_api.Writes);
        Assert.Empty(_api.Dpis);
    }

    // Reads vIFF again, as the next sync cycle does.
    private void Poll() => _store.ReplaceDepartures("LFPG", _api.Departures["LFPG"], TestData.Noon);

    private void Drain() => Assert.True(_scheduler.WaitForIdle(TimeSpan.FromSeconds(5)));
}
