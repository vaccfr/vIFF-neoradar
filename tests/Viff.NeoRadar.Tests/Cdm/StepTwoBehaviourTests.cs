using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Focus;
using NeoRadar.PluginSdk.Window;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Panel;
using Viff.NeoRadar.Strips;
using Viff.NeoRadar.Tests.Fakes;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Cdm;

public sealed class StepTwoBehaviourTests
{
    private const string Me = "LFPG_DEL";

    private readonly FakeViffApi _api = new();
    private readonly CdmStore _store = new();
    private readonly AirportControls _controls = new(new FakeSettings());
    private readonly EventSlots _slots = new();
    private readonly ManualClock _clock = new(TestData.Noon);

    public StepTwoBehaviourTests()
    {
        _store.SetCapacity("LFPG", new AirportCapacity("LFPG", 30, 12, true), TestData.Noon);
    }

    [Fact]
    public async Task Sequencer_IgnoresACtotTheMasterSwitchedOff()
    {
        Load(TestData.Departure("SLOT", "1210") with { Ctot = "1250" });
        _controls.SetCtotIgnored("SLOT", ignored: true);

        await Master().RunAsync("LFPG", TrafficSnapshot.Empty, TestData.Noon, CancellationToken.None);

        Assert.Equal("122000", _api.Writes.Single().Ttot);
    }

    [Fact]
    public async Task Sequencer_PutsEventSlotFlightsFirst()
    {
        Load(TestData.Departure("EARLY", "1210"), TestData.Departure("EVENT", "1211") with { Cid = "1234567" });
        _slots.Replace(EventSlots.Parse("1234567,1300"));

        await Master().RunAsync("LFPG", TrafficSnapshot.Empty, TestData.Noon, CancellationToken.None);

        Dictionary<string, CdmDataUpdate> writes = _api.Writes.ToDictionary(w => w.Callsign);
        Assert.Equal("122100", writes["EVENT"].Ttot);
        Assert.Equal("122300", writes["EARLY"].Ttot);
    }

    [Fact]
    public void Actions_SetTsacFromTsatMarkItPendingOnReadyTobtAndToggleAutoStartup()
    {
        Load(TestData.Departure("AFR1", "1210").WithCdm(c => c with { Tobt = "121000", Tsat = "121500", Ttot = "122700" }));
        _store.SetMaster("LFPG", Me);
        FakeControllerDataApi client = new();
        StripWriter strips = new(new StripStore(client), client, new FakeLogger());
        JobScheduler scheduler = new(_clock, new FakeLogger());
        FlightActions actions = new(_api, _store, new ConnectionGate(FakeFsd.LiveAtc(Me)), scheduler, new Notifier(new FakeChat(), new FakeFeedback()),
            _clock, () => { }, strips, _controls, _slots, readySetTsac: () => true);

        actions.SetTsac("AFR1", null);
        Assert.Equal("1215", strips.Current("AFR1")[StripData.Tsac]);

        actions.ReadyTobt("AFR1");
        Assert.True(scheduler.WaitForIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal(StripData.TsacPending, strips.Current("AFR1")[StripData.Tsac]);

        actions.ToggleAutoStartup("AFR1");
        Assert.Equal(StripData.AutoStartupArmed, strips.Current("AFR1")[StripData.AutoStartup]);
        actions.ToggleAutoStartup("AFR1");
        Assert.Equal(string.Empty, strips.Current("AFR1")[StripData.AutoStartup]);

        actions.SetCtotIgnored("AFR1", ignored: true);
        Assert.True(_controls.IsCtotIgnored("AFR1"));
    }

    [Fact]
    public void Actions_LeaveIgnoringACtotToTheMaster()
    {
        Load(TestData.Departure("AFR1", "1210") with { Ctot = "1230" });
        _store.SetMaster("LFPG", "LFPG_TWR");
        FakeControllerDataApi client = new();
        FlightActions actions = new(_api, _store, new ConnectionGate(FakeFsd.LiveAtc(Me)), new JobScheduler(_clock, new FakeLogger()),
            new Notifier(new FakeChat(), new FakeFeedback()), _clock, () => { }, new StripWriter(new StripStore(client), client, new FakeLogger()), _controls, _slots);

        actions.SetCtotIgnored("AFR1", ignored: true);

        Assert.False(_controls.IsCtotIgnored("AFR1"));
    }

    [Fact]
    public async Task AtfcmList_ShowsYourDeparturesOnlyAndSelectsTheAircraftOnDoubleClick()
    {
        _api.Relevant.Add(new RelevantFlight("SWR2549", "LSGG", "LFPG", "1600", "1600", "1605", "LF-FF_LMHJ-E", "SRM", true, false, false));
        _api.Relevant.Add(new RelevantFlight("CCM200", "LFPO", "LFKC", "1815", "", "", "", "FLS-NRA", false, false, true));
        _api.Relevant.Add(new RelevantFlight("DLH1", "EDDF", "LFPG", "1600", "1600", "1610", "ED-REG", "SRM", true, false, false));
        RecordingWindows windows = new();
        RecordingFocus focus = new();
        JobScheduler scheduler = new(_clock, new FakeLogger());
        AtfcmList list = new(windows, _api, focus, new FakeSettings(), new ConnectionGate(FakeFsd.LiveAtc(Me)), scheduler,
            () => new[] { "LSGG", "LFPO" });

        list.Open();
        Assert.True(scheduler.WaitForIdle(TimeSpan.FromSeconds(5)));

        Assert.Equal(string.Empty, Assert.Single(_api.RelevantFilters));
        IReadOnlyList<TableRow> rows = windows.Rows!;
        Assert.Equal(new[] { "SWR2549", "CCM200" }, rows.Select(r => r.Id));
        Assert.NotNull(rows[1].Style);

        list.OnWindowEvent(new WindowEvent(windows.Handle, "flights", "activate", "SWR2549"));
        Assert.Equal("SWR2549", focus.Selected);

        list.OnWindowEvent(new WindowEvent(windows.Handle, "filter", "submit", "lsgg"));
        Assert.True(scheduler.WaitForIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal("LSGG", _api.RelevantFilters.Last());
    }

    private MasterSequencing Master() => new(_api, _store, _controls, new FakeLogger(), _ => { }, _slots);

    private void Load(params DepartureRecord[] records)
    {
        _api.Departures["LFPG"] = records.ToList();
        _store.ReplaceDepartures("LFPG", records, TestData.Noon);
    }

    private sealed class RecordingFocus : IFocusApi
    {
        public string? Selected { get; private set; }

        public string? GetSelectedAircraft() => Selected;

        public string GetSelectedPlanCallsign() => Selected ?? string.Empty;

        public bool SelectAircraft(string callsign)
        {
            Selected = callsign;
            return true;
        }
    }

    private sealed class RecordingWindows : IWindowInterface
    {
        public WindowHandle Handle { get; } = new(9);

        public IReadOnlyList<TableRow>? Rows { get; private set; }

        public WindowHandle OpenWindow(WindowDefinition definition) => Handle;

        public bool SetTableRows(WindowHandle window, string elementId, IReadOnlyList<TableRow> rows, int selectedIndex)
        {
            Rows = rows;
            return true;
        }

        public bool UpdateElement(WindowHandle window, string elementId, string text) => true;

        public bool CloseWindow(WindowHandle window) => true;

        public bool ReplaceChildren(WindowHandle window, string elementId, IReadOnlyList<WindowElement> children) => true;

        public bool SetElementVisible(WindowHandle window, string elementId, bool visible) => true;

        public bool SetElementEnabled(WindowHandle window, string elementId, bool enabled) => true;

        public bool SetElementStyle(WindowHandle window, string elementId, ElementStyle style) => true;

        public bool AppendChild(WindowHandle window, string parentId, WindowElement child) => true;

        public bool RemoveChild(WindowHandle window, string elementId) => true;

        public bool SetSelectedIndex(WindowHandle window, string elementId, int index) => true;

        public bool SetChecked(WindowHandle window, string elementId, bool @checked) => true;

        public bool SetOptions(WindowHandle window, string elementId, IReadOnlyList<string> options, int selectedIndex) => true;

        public bool SetTableSelection(WindowHandle window, string elementId, int index) => true;
    }
}
