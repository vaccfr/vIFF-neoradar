using System;
using System.Collections.Generic;
using System.Linq;
using NeoRadar.PluginSdk.Airport;
using NeoRadar.PluginSdk.Window;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Panel;
using Viff.NeoRadar.Tests.Fakes;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Panel;

public sealed class AirportPanelTests
{
    private readonly FakeViffApi _api = new();
    private readonly CdmStore _store = new();
    private readonly AirportControls _controls = new(new FakeSettings());
    private readonly FakeWindows _windows = new();
    private readonly ManualClock _clock = new(TestData.Noon);
    private readonly JobScheduler _scheduler;
    private readonly AirportPanel _panel;

    public AirportPanelTests()
    {
        FakeAirportApi airports = new();
        foreach (string icao in new[] { "LFPG", "LFPO", "LFOB" })
        {
            airports.Configurations.Add(new AirportConfig(icao, Array.Empty<string>(), Array.Empty<string>(), AirportStatus.Active));
        }

        _store.SetCapacity("LFPG", new AirportCapacity("LFPG", 60, 18, true), TestData.Noon);
        _store.SetCapacity("LFPO", new AirportCapacity("LFPO", 38, 12, true), TestData.Noon);
        _store.SetCapacity("LFOB", new AirportCapacity("LFOB", 10, 8, false), TestData.Noon);
        _store.SetMaster("LFPG", "LFPG_DEL");

        ConnectionGate gate = new(FakeFsd.LiveAtc("LFPG_DEL"));
        MasterSequencing sequencing = new(_api, _store, _controls, new FakeLogger(), _ => { });
        CdmSync sync = new(_api, _store, _controls, sequencing, gate, airports, new FakeFlightplanApi(), new FakeControllerDataApi(), _clock);
        _scheduler = new JobScheduler(_clock, new FakeLogger());
        Notifier notifier = new(new FakeChat(), new FakeFeedback());
        MasterRole master = new(_api, _store, sync, gate, _scheduler, notifier, () => { });
        _panel = new AirportPanel(_windows, _store, sync, _controls, master, gate, notifier);
    }

    [Fact]
    public void Open_ShowsOneColouredChipPerCdmAirportOnly()
    {
        _panel.Open();

        IReadOnlyList<WindowElement> chips = _windows.Opened!.Elements!.Single().Children;
        Assert.Equal(new[] { "LFPG", "LFPO" }, chips.Select(c => c.Text));
        Assert.All(chips, c => Assert.True(c.Clickable));
        Assert.Equal((byte)30, chips[0].Style.BackgroundColor!.Value.R);
        Assert.Equal((byte)150, chips[1].Style.BackgroundColor!.Value.R);
        Assert.True(_windows.Opened.Width <= 300 && _windows.Opened.Height <= 80);
    }

    [Fact]
    public void LeftClick_ClaimsMasterAndRightClickSwitchesLvo()
    {
        _panel.Open();

        _panel.OnWindowEvent(new WindowEvent(_windows.Handle, "apt:LFPO", "click", "left"));
        Assert.True(_scheduler.WaitForIdle(TimeSpan.FromSeconds(5)));
        Assert.Contains(("LFPO", "LFPG_DEL", true), _api.MasterCalls);

        _panel.OnWindowEvent(new WindowEvent(_windows.Handle, "apt:LFPO", "click", "right"));
        Assert.True(_controls.IsLvo("LFPO"));
        Assert.Contains(_windows.Replaced.Last(), c => c.Text == "LFPO LVO");
    }

    [Fact]
    public void Refresh_RedrawsOnlyWhenSomethingChanged()
    {
        _panel.Open();

        _panel.Refresh();
        Assert.Empty(_windows.Replaced);

        _store.SetMaster("LFPO", "LFPO_TWR");
        _panel.Refresh();
        Assert.Single(_windows.Replaced);
    }

    private sealed class FakeWindows : IWindowInterface
    {
        public WindowHandle Handle { get; } = new(7);

        public WindowDefinition? Opened { get; private set; }

        public List<IReadOnlyList<WindowElement>> Replaced { get; } = new();

        public WindowHandle OpenWindow(WindowDefinition definition)
        {
            Opened = definition;
            return Handle;
        }

        public bool ReplaceChildren(WindowHandle window, string elementId, IReadOnlyList<WindowElement> children)
        {
            Replaced.Add(children);
            return true;
        }

        public bool CloseWindow(WindowHandle window) => true;

        public bool UpdateElement(WindowHandle window, string elementId, string text) => true;

        public bool SetElementVisible(WindowHandle window, string elementId, bool visible) => true;

        public bool SetElementEnabled(WindowHandle window, string elementId, bool enabled) => true;

        public bool SetElementStyle(WindowHandle window, string elementId, ElementStyle style) => true;

        public bool AppendChild(WindowHandle window, string parentId, WindowElement child) => true;

        public bool RemoveChild(WindowHandle window, string elementId) => true;

        public bool SetSelectedIndex(WindowHandle window, string elementId, int index) => true;

        public bool SetChecked(WindowHandle window, string elementId, bool @checked) => true;

        public bool SetOptions(WindowHandle window, string elementId, IReadOnlyList<string> options, int selectedIndex) => true;

        public bool SetTableRows(WindowHandle window, string elementId, IReadOnlyList<TableRow> rows, int selectedIndex) => true;

        public bool SetTableSelection(WindowHandle window, string elementId, int index) => true;
    }
}
