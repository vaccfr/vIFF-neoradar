using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Airport;
using NeoRadar.PluginSdk.ControllerData;
using NeoRadar.PluginSdk.Flightplan;
using NeoRadar.PluginSdk.Logger;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Runs one sync cycle: reads masters, capacities and departures of the watched airports from vIFF, then does the
/// master's work for the airports this client is master of.
/// </summary>
/// <remarks>Runs on the sync job only, so cycles never overlap.</remarks>
internal sealed class CdmSync
{
    private static readonly TimeSpan s_capacityRefresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_networkAirportRefresh = TimeSpan.FromMinutes(1);
    private const int MaxNetworkAirportsPerCycle = 6;
    private const int MaxScratchpadWritesPerCycle = 4;

    private readonly IViffApi _api;
    private readonly CdmStore _store;
    private readonly AirportControls _controls;
    private readonly MasterSequencing _master;
    private readonly ConnectionGate _gate;
    private readonly IAirportApi _airports;
    private readonly IFlightplanApi _flightplans;
    private readonly IControllerDataApi _controllerData;
    private readonly TimeProvider _clock;

    // Airports named in a .cdm command that the airport configuration does not hold; read by the command thread too.
    private readonly object _lock = new();
    private readonly HashSet<string> _extraAirports = new(StringComparer.OrdinalIgnoreCase);
    private volatile TrafficSnapshot _latestTraffic = TrafficSnapshot.Empty;

    public CdmSync(
        IViffApi api,
        CdmStore store,
        AirportControls controls,
        MasterSequencing master,
        ConnectionGate gate,
        IAirportApi airports,
        IFlightplanApi flightplans,
        IControllerDataApi controllerData,
        TimeProvider clock)
    {
        _api = api;
        _store = store;
        _controls = controls;
        _master = master;
        _gate = gate;
        _airports = airports;
        _flightplans = flightplans;
        _controllerData = controllerData;
        _clock = clock;
    }

    /// <summary>
    /// Gets the client's flights as read at the start of the last cycle.
    /// </summary>
    public TrafficSnapshot LatestTraffic => _latestTraffic;

    /// <summary>
    /// Raised on the sync job after each cycle.
    /// </summary>
    public event Action? Synced;

    /// <summary>
    /// Watches an airport the airport configuration does not hold, such as one named in <c>.cdm master</c>.
    /// </summary>
    public void Watch(string icao)
    {
        lock (_lock)
        {
            _extraAirports.Add(icao.ToUpperInvariant());
        }
    }

    /// <summary>
    /// Gets the airports watched: the client's configured airports, those named in commands, and those it is master of.
    /// </summary>
    public IReadOnlyList<string> WatchedAirports()
    {
        SortedSet<string> watched = new(StringComparer.OrdinalIgnoreCase);
        foreach (AirportConfig config in _airports.GetConfigurations())
        {
            if (config.Icao.Length == 4)
            {
                watched.Add(config.Icao.ToUpperInvariant());
            }
        }

        lock (_lock)
        {
            watched.UnionWith(_extraAirports);
        }

        string callsign = _gate.Callsign;
        if (callsign.Length > 0)
        {
            watched.UnionWith(_store.AirportsMasteredBy(callsign));
        }

        return watched.ToList();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        TrafficSnapshot traffic = _gate.Callsign.Length > 0 ? TrafficSnapshot.Read(_flightplans, _controllerData) : TrafficSnapshot.Empty;
        _latestTraffic = traffic;

        IReadOnlyList<MasterAirport>? masters = await _api.GetMastersAsync(cancellationToken).ConfigureAwait(false);
        if (masters is not null)
        {
            _store.SetMasters(masters);
        }

        IReadOnlyList<RestrictedFlight>? restricted = await _api.GetRestrictedAsync(cancellationToken).ConfigureAwait(false);
        if (restricted is not null)
        {
            _store.SetNetworkCtots(restricted);
        }

        IReadOnlyList<string> watched = WatchedAirports();
        foreach (string icao in watched)
        {
            if (!_store.HasFreshCapacity(icao, now - s_capacityRefresh))
            {
                AirportCapacity? capacity = await _api.GetCapacityAsync(icao, cancellationToken).ConfigureAwait(false);
                _store.SetCapacity(icao, capacity ?? _store.Airport(icao).Capacity, now);
            }

            IReadOnlyList<DepartureRecord>? departures = await _api.GetDeparturesAsync(icao, cancellationToken).ConfigureAwait(false);
            if (departures is not null)
            {
                _store.ReplaceDepartures(icao, departures, now);
            }
        }

        HashSet<string> watchedSet = new(watched, StringComparer.OrdinalIgnoreCase);
        _store.RetainAirports(watchedSet);
        await ReadNetworkAirportsAsync(traffic, watchedSet, now, cancellationToken).ConfigureAwait(false);
        foreach (FlightView flight in _store.Flights())
        {
            if (traffic[flight.Record.Callsign].Started && flight.Asat is null)
            {
                _store.ObserveStartUp(flight.Record.Callsign, started: true, DateTimeOffset.MinValue);
            }
        }

        if (_gate.CanWrite(out string callsign, out _))
        {
            foreach (string icao in _store.AirportsMasteredBy(callsign))
            {
                await _master.RunAsync(icao, traffic, now, cancellationToken).ConfigureAwait(false);
            }

            WriteScratchpadRemarks(callsign, traffic);
        }

        Synced?.Invoke();
    }

    // Reads the departures of other airports with a master that the client has traffic from, so their TSATs show.
    private async Task ReadNetworkAirportsAsync(
        TrafficSnapshot traffic,
        IReadOnlySet<string> watched,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        HashSet<string> origins = new(StringComparer.OrdinalIgnoreCase);
        foreach (string callsign in traffic.Callsigns)
        {
            string origin = traffic[callsign].Origin;
            if (origin.Length == 4 && !watched.Contains(origin) && _store.Airport(origin).HasMaster)
            {
                origins.Add(origin);
            }
        }

        IEnumerable<string> due = origins
            .Select(icao => (Icao: icao, FetchedAt: _store.NetworkFetchedAt(icao)))
            .Where(a => a.FetchedAt is null || a.FetchedAt.Value <= now - s_networkAirportRefresh)
            .OrderBy(a => a.FetchedAt ?? DateTimeOffset.MinValue)
            .Take(MaxNetworkAirportsPerCycle)
            .Select(a => a.Icao)
            .ToList();
        foreach (string icao in due)
        {
            IReadOnlyList<DepartureRecord>? departures = await _api.GetDeparturesAsync(icao, cancellationToken).ConfigureAwait(false);
            if (departures is not null)
            {
                _store.ReplaceNetworkDepartures(icao, departures, now);
            }
        }

        _store.RetainNetworkAirports(origins);
    }

    // Mirrors the EuroScope plugin's .cdm remarks and .cdm rmkctot for controllers still reading the scratchpad.
    private void WriteScratchpadRemarks(string callsign, TrafficSnapshot traffic)
    {
        bool tsatRemarks = _controls.TsatRemarks;
        bool ctotRemarks = _controls.CtotRemarks;
        if (!tsatRemarks && !ctotRemarks)
        {
            return;
        }

        HashSet<string> mastered = new(_store.AirportsMasteredBy(callsign), StringComparer.OrdinalIgnoreCase);
        int writes = 0;
        foreach (FlightView flight in _store.Flights().Where(f => mastered.Contains(f.Airport.Icao)))
        {
            TrafficEntry entry = traffic[flight.Record.Callsign];
            if (entry.Started || writes >= MaxScratchpadWritesPerCycle)
            {
                continue;
            }

            string ctot = CdmTime.Display(flight.Record.EffectiveCtot);
            string tsat = CdmTime.Display(flight.Record.Cdm.Tsat);
            string? wanted = null;
            if (ctotRemarks && ctot.Length > 0)
            {
                wanted = ".C" + ctot;
            }
            else if (tsatRemarks && tsat.Length > 0)
            {
                wanted = tsat;
            }
            else if (ctotRemarks && entry.Scratchpad.Contains(".C", StringComparison.Ordinal))
            {
                wanted = string.Empty;
            }

            if (wanted is not null && wanted != entry.Scratchpad && _controllerData.SetScratchpad(flight.Record.Callsign, wanted))
            {
                writes++;
            }
        }
    }
}
