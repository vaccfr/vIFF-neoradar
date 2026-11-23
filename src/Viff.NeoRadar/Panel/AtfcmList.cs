using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Focus;
using NeoRadar.PluginSdk.Settings;
using NeoRadar.PluginSdk.Window;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Panel;

/// <summary>
/// The ATFCM Flight List: vIFF's regulated, suspended and re-slotted flights, as the CDM plugin's <c>.cdm atfcm</c> panel.
/// </summary>
/// <remarks>
/// Only your own departures show: flights from the airports set active with a departure runway, as NeoRadar's
/// isMyDeparture. The filter, a prefix of the departure, arrival or regulation, narrows them further. Double-clicking a row selects the aircraft. Window calls are safe from any thread; events arrive on the UI thread.
/// </remarks>
internal sealed class AtfcmList
{
    private const string FilterId = "filter";
    private const string CountId = "count";
    private const string TableId = "flights";
    private const string FilterSetting = "atfcmFilter";

    private static readonly ElementStyle s_suspended = new() { TextColor = (230, 80, 80) };
    private static readonly ElementStyle s_ready = new() { TextColor = (245, 239, 13) };

    private readonly IWindowInterface _windows;
    private readonly IViffApi _api;
    private readonly IFocusApi _focus;
    private readonly ISettingsApi _settings;
    private readonly ConnectionGate _gate;
    private readonly JobScheduler _scheduler;
    private readonly Func<IReadOnlyCollection<string>> _myAirports;

    // Guards _handle and _filter; opened from the command and menu threads, refreshed from the pool.
    private readonly object _lock = new();
    private WindowHandle _handle = WindowHandle.Invalid;
    private string _filter = string.Empty;

    public AtfcmList(
        IWindowInterface windows,
        IViffApi api,
        IFocusApi focus,
        ISettingsApi settings,
        ConnectionGate gate,
        JobScheduler scheduler,
        Func<IReadOnlyCollection<string>>? myAirports = null)
    {
        _myAirports = myAirports ?? Array.Empty<string>;
        _windows = windows;
        _api = api;
        _focus = focus;
        _settings = settings;
        _gate = gate;
        _scheduler = scheduler;
    }

    public bool IsOpen
    {
        get { lock (_lock) { return _handle.IsValid; } }
    }

    /// <summary>
    /// Opens the list, or closes and reopens it so a hidden one shows again, then reads vIFF.
    /// </summary>
    public void Open()
    {
        string filter = _settings.Get(FilterSetting) ?? string.Empty;
        WindowHandle previous;
        lock (_lock)
        {
            previous = _handle;
            _handle = WindowHandle.Invalid;
            _filter = filter;
        }

        if (previous.IsValid)
        {
            _windows.CloseWindow(previous);
        }

        WindowDefinition definition = new(
            "ATFCM Flight List",
            Width: 720,
            Height: 320,
            Icon: "list",
            ShowInList: true,
            Elements: new[]
            {
                new WindowElement
                {
                    Type = ElementType.Grid,
                    Rows = "auto,*",
                    Children = new[]
                    {
                        new WindowElement
                        {
                            Type = ElementType.Panel,
                            Direction = 1,
                            Row = 0,
                            Style = new ElementStyle { Spacing = 8, PaddingV = 4 },
                            Children = new[]
                            {
                                new WindowElement { Type = ElementType.Label, Text = "Filter", VAlign = VAlign.Center },
                                new WindowElement
                                {
                                    Id = FilterId,
                                    Type = ElementType.TextInput,
                                    Text = filter,
                                    Data = "LF, LFPG, EGLL… (Enter)",
                                    Style = new ElementStyle { Width = 140 },
                                },
                                new WindowElement { Id = CountId, Type = ElementType.Label, Text = "Reading vIFF…", VAlign = VAlign.Center, Style = new ElementStyle { Role = ElementRole.Muted } },
                            },
                        },
                        new WindowElement
                        {
                            Id = TableId,
                            Type = ElementType.Table,
                            Row = 1,
                            Table = new TableDefinition(
                                new[]
                                {
                                    new TableColumn("cs", "C/S", "80"),
                                    new TableColumn("adep", "ADEP", "50"),
                                    new TableColumn("ades", "ADES", "50"),
                                    new TableColumn("eobt", "EOBT", "45"),
                                    new TableColumn("tobt", "TOBT", "45"),
                                    new TableColumn("ctot", "CTOT", "45"),
                                    new TableColumn("reg", "REGULATION", "*"),
                                    new TableColumn("sts", "STATUS", "80"),
                                    new TableColumn("inf", "INF", "35", HAlign.Center),
                                    new TableColumn("rea", "REA", "35", HAlign.Center),
                                },
                                Array.Empty<TableRow>()),
                        },
                    },
                },
            },
            Resizable: true,
            MinWidth: 420,
            MinHeight: 160);

        WindowHandle opened = _windows.OpenWindow(definition);
        lock (_lock)
        {
            _handle = opened;
        }

        _scheduler.RunOnce("atfcm list", RefreshAsync);
    }

    /// <summary>
    /// Reads vIFF's ATFCM list and shows it, if the window is open.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        string filter;
        lock (_lock)
        {
            if (!_handle.IsValid)
            {
                return;
            }

            filter = _filter;
        }

        IReadOnlyList<RelevantFlight>? flights = await _api.GetRelevantAsync(filter, cancellationToken).ConfigureAwait(false);
        WindowHandle handle;
        lock (_lock)
        {
            handle = _handle;
        }

        if (!handle.IsValid || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (flights is null)
        {
            _windows.UpdateElement(handle, CountId, "vIFF did not answer; retrying in a minute");
            return;
        }

        HashSet<string> mine = new(_myAirports(), StringComparer.OrdinalIgnoreCase);
        if (mine.Count == 0)
        {
            _windows.SetTableRows(handle, TableId, Array.Empty<TableRow>(), -1);
            _windows.UpdateElement(handle, CountId, "No airport active with a departure runway: your departures show here once one is");
            return;
        }

        List<TableRow> rows = flights
            .Where(f => mine.Contains(f.Departure))
            .OrderBy(f => f.Ctot.Length == 0)
            .ThenBy(f => f.Ctot, StringComparer.Ordinal)
            .ThenBy(f => f.Callsign, StringComparer.Ordinal)
            .Select(Row)
            .ToList();
        _windows.SetTableRows(handle, TableId, rows, -1);
        _windows.UpdateElement(handle, CountId,
            $"{rows.Count} of your departure(s) from {string.Join(" ", mine.Order(StringComparer.Ordinal))}{(filter.Length > 0 ? $" matching {filter}" : string.Empty)}");
    }

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

        if (evt.ElementId == FilterId && evt.EventType == "submit")
        {
            string filter = evt.Value.Trim().ToUpperInvariant();
            lock (_lock)
            {
                _filter = filter;
            }

            _settings.Set(FilterSetting, filter);
            _scheduler.RunOnce("atfcm list", RefreshAsync);
        }
        else if (evt.ElementId == TableId && evt.EventType == "activate" && evt.Value.Length > 0)
        {
            _focus.SelectAircraft(evt.Value);
        }
        else if (evt.EventType == "close")
        {
            lock (_lock)
            {
                _handle = WindowHandle.Invalid;
            }
        }
    }

    private static TableRow Row(RelevantFlight flight)
    {
        bool suspended = flight.Status.StartsWith("FLS", StringComparison.OrdinalIgnoreCase);
        return new TableRow(
            flight.Callsign.Replace("|", string.Empty, StringComparison.Ordinal),
            new[]
            {
                flight.Callsign,
                flight.Departure,
                flight.Arrival,
                CdmTime.Display(flight.Eobt),
                CdmTime.Display(flight.Tobt),
                CdmTime.Display(flight.Ctot),
                flight.Regulation,
                flight.Status,
                flight.Informed ? "✓" : string.Empty,
                flight.IsRea ? "REA" : string.Empty,
            },
            suspended ? s_suspended : flight.IsRea ? s_ready : null);
    }
}
