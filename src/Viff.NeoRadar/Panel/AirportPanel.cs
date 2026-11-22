using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NeoRadar.PluginSdk.Window;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;

namespace Viff.NeoRadar.Panel;

/// <summary>
/// The CDM Airport Panel: a compact strip with one chip per watched CDM airport, as the CDM plugin's airport panel.
/// </summary>
/// <remarks>
/// A chip is green when this controller is master, purple when someone else is, yellow while connecting and red with no
/// master. Left click connects or releases the master role, right click switches LVO. Window calls are safe from any
/// thread; events arrive on the UI thread.
/// </remarks>
internal sealed class AirportPanel
{
    private const string StripId = "airports";
    private const string ChipPrefix = "apt:";

    private static readonly (byte R, byte G, byte B) s_noMaster = (150, 35, 35);
    private static readonly (byte R, byte G, byte B) s_connecting = (160, 135, 20);
    private static readonly (byte R, byte G, byte B) s_mine = (30, 125, 50);
    private static readonly (byte R, byte G, byte B) s_other = (105, 50, 140);

    private readonly IWindowInterface _windows;
    private readonly CdmStore _store;
    private readonly CdmSync _sync;
    private readonly AirportControls _controls;
    private readonly MasterRole _master;
    private readonly ConnectionGate _gate;
    private readonly Notifier _notifier;

    // Guards _handle and _shown; opened from the command and menu threads, refreshed from the pool.
    private readonly object _lock = new();
    private WindowHandle _handle = WindowHandle.Invalid;
    private string _shown = string.Empty;

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
            _shown = string.Empty;
        }

        if (previous.IsValid)
        {
            _windows.CloseWindow(previous);
        }

        (IReadOnlyList<WindowElement> chips, string signature) = Chips();
        WindowDefinition definition = new(
            "CDM Airport Panel",
            Width: 300,
            Height: 72,
            Icon: "plane-takeoff",
            ShowInList: true,
            Elements: new[]
            {
                new WindowElement
                {
                    Id = StripId,
                    Type = ElementType.Panel,
                    Direction = 1,
                    Wrap = true,
                    Style = new ElementStyle { Spacing = 4 },
                    Children = chips,
                },
            },
            Resizable: true,
            MinWidth: 120,
            MinHeight: 48)
        {
            Padding = 6,
        };

        WindowHandle opened = _windows.OpenWindow(definition);
        lock (_lock)
        {
            _handle = opened;
            _shown = signature;
        }
    }

    /// <summary>
    /// Redraws the chips if the panel is open and something they show changed.
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

        (IReadOnlyList<WindowElement> chips, string signature) = Chips();
        lock (_lock)
        {
            if (handle != _handle || signature == _shown)
            {
                return;
            }

            _shown = signature;
        }

        _windows.ReplaceChildren(handle, StripId, chips);
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

        if (evt.EventType != "click" || !evt.ElementId.StartsWith(ChipPrefix, StringComparison.Ordinal))
        {
            return;
        }

        string icao = evt.ElementId[ChipPrefix.Length..];
        if (evt.Value == "right")
        {
            bool lvo = _controls.ToggleLvo(icao);
            _notifier.Say($"LVO {(lvo ? "on" : "off")} for {icao}.");
            Refresh();
            return;
        }

        if (evt.Value != "left")
        {
            return;
        }

        string me = _gate.Callsign;
        if (me.Length > 0 && string.Equals(_store.Airport(icao).MasterPosition, me, StringComparison.OrdinalIgnoreCase))
        {
            _master.Release(icao);
        }
        else
        {
            _master.Claim(icao);
        }
    }

    private (IReadOnlyList<WindowElement> Chips, string Signature) Chips()
    {
        string me = _gate.Callsign;
        List<WindowElement> chips = new();
        List<string> signature = new();
        foreach (AirportView airport in _store.Airports(_sync.WatchedAirports()).Where(a => a.IsCdm))
        {
            bool mine = me.Length > 0 && string.Equals(airport.MasterPosition, me, StringComparison.OrdinalIgnoreCase);
            bool pending = _master.IsPending(airport.Icao);
            bool lvo = _controls.IsLvo(airport.Icao);
            int rate = _controls.EffectiveRate(airport.Icao, airport.Capacity?.Rate ?? 0, out _);

            ((byte R, byte G, byte B) colour, string state, string action) = (pending, mine, airport.HasMaster) switch
            {
                (true, _, _) => (s_connecting, "Connecting...", string.Empty),
                (_, true, _) => (s_mine, "You are master", "Click to release."),
                (_, _, true) => (s_other, $"Master: {airport.MasterPosition}", string.Empty),
                _ => (s_noMaster, "No master", "Click to connect as master."),
            };

            string rateText = rate > 0 ? rate.ToString(CultureInfo.InvariantCulture) + "/h" : "no rate";
            chips.Add(new WindowElement
            {
                Id = ChipPrefix + airport.Icao,
                Type = ElementType.Badge,
                Text = lvo ? airport.Icao + " LVO" : airport.Icao,
                Clickable = true,
                Tooltip = $"{state} · {rateText}{(lvo ? " · LVO" : string.Empty)}. {action} Right click: LVO.".Replace("  ", " ", StringComparison.Ordinal),
                Style = new ElementStyle
                {
                    BackgroundColor = colour,
                    TextColor = (255, 255, 255),
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    PaddingH = 8,
                    PaddingV = 3,
                    BorderRadius = 3,
                },
            });
            signature.Add($"{airport.Icao}|{state}|{rateText}|{lvo}");
        }

        if (chips.Count == 0)
        {
            chips.Add(new WindowElement
            {
                Type = ElementType.Label,
                Text = "No CDM airport watched: add one to your airports, or .cdm master ICAO",
                Style = new ElementStyle { Role = ElementRole.Muted, FontSize = 11 },
            });
        }

        return (chips, string.Join(";", signature));
    }
}
