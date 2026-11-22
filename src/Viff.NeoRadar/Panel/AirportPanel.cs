using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NeoRadar.PluginSdk.Window;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;

namespace Viff.NeoRadar.Panel;

/// <summary>
/// The CDM Airport Panel: each watched airport's master status, with Connect and Release, LVO and the rate in use.
/// </summary>
/// <remarks>
/// Row colours follow the French vACC panel: red no master, yellow connecting, green connected as master, purple
/// someone else is master. Window calls are safe from any thread; events arrive on the UI thread.
/// </remarks>
internal sealed class AirportPanel
{
    private const string TableId = "airports";
    private const string FooterId = "footer";
    private const string ActionColumn = "action";
    private const string LvoColumn = "lvo";

    private static readonly ElementStyle s_noMaster = Row((120, 30, 30));
    private static readonly ElementStyle s_connecting = Row((130, 110, 20));
    private static readonly ElementStyle s_mine = Row((25, 100, 40));
    private static readonly ElementStyle s_other = Row((90, 40, 120));
    private static readonly ElementStyle s_notCdm = new() { Role = ElementRole.Muted };

    private readonly IWindowInterface _windows;
    private readonly CdmStore _store;
    private readonly CdmSync _sync;
    private readonly AirportControls _controls;
    private readonly MasterRole _master;
    private readonly ConnectionGate _gate;
    private readonly Notifier _notifier;

    // Guards _handle; opened from the command and menu threads, refreshed from the pool.
    private readonly object _lock = new();
    private WindowHandle _handle = WindowHandle.Invalid;

    public AirportPanel(
        IWindowInterface windows,
        CdmStore store,
        CdmSync sync,
        AirportControls controls,
        MasterRole master,
        ConnectionGate gate,
        Notifier notifier)
    {
        _windows = windows;
        _store = store;
        _sync = sync;
        _controls = controls;
        _master = master;
        _gate = gate;
        _notifier = notifier;
    }

    /// <summary>
    /// Opens the panel, or closes and reopens it so a hidden one shows again.
    /// </summary>
    public void Open()
    {
        WindowHandle previous;
        lock (_lock)
        {
            previous = _handle;
            _handle = WindowHandle.Invalid;
        }

        if (previous.IsValid)
        {
            _windows.CloseWindow(previous);
        }

        WindowDefinition definition = new(
            "CDM Airport Panel",
            Width: 520,
            Height: 260,
            Icon: "plane-takeoff",
            ShowInList: true,
            Elements: new[]
            {
                new WindowElement
                {
                    Type = ElementType.Grid,
                    Rows = "*,auto",
                    Children = new[]
                    {
                        new WindowElement
                        {
                            Id = TableId,
                            Type = ElementType.Table,
                            Row = 0,
                            Table = new TableDefinition(
                                new[]
                                {
                                    new TableColumn("icao", "ICAO", "60"),
                                    new TableColumn("status", "STATUS", "*"),
                                    new TableColumn("rate", "RATE", "70", HAlign.Center),
                                    new TableColumn(LvoColumn, "LVO", "45", HAlign.Center, TableColumnKind.Check),
                                    new TableColumn(ActionColumn, string.Empty, "90", HAlign.Center, TableColumnKind.Button),
                                },
                                Rows()),
                        },
                        new WindowElement
                        {
                            Id = FooterId,
                            Type = ElementType.Label,
                            Row = 1,
                            Text = Footer(),
                            Style = new ElementStyle { Role = ElementRole.Muted, FontSize = 11, PaddingV = 4 },
                        },
                    },
                },
            },
            Resizable: true,
            MinWidth: 400,
            MinHeight: 160);

        WindowHandle opened = _windows.OpenWindow(definition);
        lock (_lock)
        {
            _handle = opened;
        }
    }

    /// <summary>
    /// Redraws the rows and footer if the panel is open.
    /// </summary>
    public void Refresh()
    {
        WindowHandle handle;
        lock (_lock)
        {
            handle = _handle;
        }

        if (!handle.IsValid)
        {
            return;
        }

        _windows.SetTableRows(handle, TableId, Rows(), -1);
        _windows.UpdateElement(handle, FooterId, Footer());
    }

    /// <summary>
    /// Closes the panel, at shutdown.
    /// </summary>
    public void Close()
    {
        WindowHandle handle;
        lock (_lock)
        {
            handle = _handle;
            _handle = WindowHandle.Invalid;
        }

        if (handle.IsValid)
        {
            _windows.CloseWindow(handle);
        }
    }

    // UI thread.
    public void OnWindowEvent(WindowEvent evt)
    {
        lock (_lock)
        {
            if (evt.Window != _handle)
            {
                return;
            }
        }

        string[] parts = evt.Value.Split('|');
        if (evt.EventType == "cell" && parts.Length == 2 && parts[1] == ActionColumn)
        {
            string icao = parts[0];
            if (string.Equals(_store.Airport(icao).MasterPosition, _gate.Callsign, StringComparison.OrdinalIgnoreCase) && _gate.Callsign.Length > 0)
            {
                _master.Release(icao);
            }
            else
            {
                _master.Claim(icao);
            }
        }
        else if (evt.EventType == "toggle" && parts.Length == 3 && parts[1] == LvoColumn)
        {
            bool lvo = _controls.ToggleLvo(parts[0]);
            _notifier.Say($"LVO {(lvo ? "on" : "off")} for {parts[0]}.");
            Refresh();
        }
    }

    private IReadOnlyList<TableRow> Rows()
    {
        string me = _gate.Callsign;
        return _store.Airports(_sync.WatchedAirports()).Select(airport =>
        {
            bool mine = me.Length > 0 && string.Equals(airport.MasterPosition, me, StringComparison.OrdinalIgnoreCase);
            (string status, ElementStyle style, string action) = airport switch
            {
                _ when _master.IsPending(airport.Icao) => ("Connecting...", s_connecting, string.Empty),
                _ when mine => ("Connected as master", s_mine, "Release"),
                { HasMaster: true } => ($"Master: {airport.MasterPosition}", s_other, string.Empty),
                { Capacity: null } => ("Waiting for vIFF", s_notCdm, string.Empty),
                { IsCdm: false } => ("Not a CDM airport", s_notCdm, string.Empty),
                _ => ("Not connected as master", s_noMaster, "Connect"),
            };

            bool lvo = _controls.IsLvo(airport.Icao);
            int rate = _controls.EffectiveRate(airport.Icao, airport.Capacity?.Rate ?? 0, out _);
            string rateText = rate > 0 ? rate.ToString(CultureInfo.InvariantCulture) + "/h" : "-";
            return new TableRow(airport.Icao, new[] { airport.Icao, status, rateText, lvo ? "true" : "false", action }, style);
        }).ToList();
    }

    private string Footer()
    {
        DateTimeOffset? lastSync = _store.LastSync;
        string sync = lastSync is null ? "never" : lastSync.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
        return $"vIFF data read {sync} · airports come from your airport configuration and .cdm master";
    }

    private static ElementStyle Row((byte R, byte G, byte B) background) =>
        new() { BackgroundColor = background, TextColor = (255, 255, 255) };
}
