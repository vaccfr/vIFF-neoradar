using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Aircraft;
using NeoRadar.PluginSdk.Airport;
using NeoRadar.PluginSdk.ControllerData;
using NeoRadar.PluginSdk.Flightplan;
using NeoRadar.PluginSdk.Logger;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Config;
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
    private static readonly TimeSpan s_statusRefresh = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_slotsRefresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_configRefresh = TimeSpan.FromMinutes(30);
    private const int MaxExotPerCycle = 3;
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
    private readonly ConfigLoader? _config;
    private readonly NetworkStatuses? _statuses;
    private readonly IAircraftApi? _aircraft;
    private readonly ILoggerApi? _logger;
    private DateTimeOffset _statusesReadAt = DateTimeOffset.MinValue;
    private DateTimeOffset _slotsReadAt = DateTimeOffset.MinValue;
    private DateTimeOffset _configReadAt = DateTimeOffset.MinValue;
    private volatile bool _reloadRequested;

    // Only the sync job reads and writes this: the taxi time last sent for each flight of an airport without CDM.
    private readonly Dictionary<string, int> _exotSent = new(StringComparer.OrdinalIgnoreCase);

    // Airports named in a .cdm command that the airport configuration does not hold; read by the command thread too.
    private readonly object _lock = new();
    private readonly HashSet<string> _extraAirports = new(StringComparer.OrdinalIgnoreCase);
    private volatile TrafficSnapshot _latestTraffic = TrafficSnapshot.Empty;

    // Only the sync job reads and writes these: while vIFF is down, cycles wait out a growing delay.
    private static readonly TimeSpan s_maxBackoff = TimeSpan.FromMinutes(2);
    private TimeSpan _backoff = TimeSpan.Zero;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    public CdmSync(
        IViffApi api,
        CdmStore store,
        AirportControls controls,
        MasterSequencing master,
        ConnectionGate gate,
        IAirportApi airports,
        IFlightplanApi flightplans,
        IControllerDataApi controllerData,
        TimeProvider clock,
        ConfigLoader? config = null,
        NetworkStatuses? statuses = null,
        IAircraftApi? aircraft = null,
        ILoggerApi? logger = null)
    {
        _config = config;
        _statuses = statuses;
        _aircraft = aircraft;
        _logger = logger;
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
    /// Asks the next cycle to reload CDMconfig.xml and the files it names.
    /// </summary>
    public void RequestConfigReload() => _reloadRequested = true;

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
        if (now < _retryAt || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        TrafficSnapshot traffic = _gate.Callsign.Length > 0
            ? TrafficSnapshot.Read(_flightplans, _controllerData, _aircraft, _airports)
            : TrafficSnapshot.Empty;
        _latestTraffic = traffic;

        // The vACC's files do not depend on vIFF: they load even while it is down.
        await ReadConfigAsync(now, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<MasterAirport>? masters = await _api.GetMastersAsync(cancellationToken).ConfigureAwait(false);
        if (masters is null)
        {
            // vIFF is unreachable or failing: stop this cycle rather than send every other read to it too.
            _backoff = _backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(30) : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, s_maxBackoff.Ticks));
            _retryAt = now + _backoff;
            _logger?.Warning($"vIFF did not answer the masters read; next try in {_backoff.TotalSeconds:0} s");
            return;
        }

        if (_backoff > TimeSpan.Zero)
        {
            _logger?.Info("vIFF answers again; syncing resumes");
        }

        _backoff = TimeSpan.Zero;
        _retryAt = DateTimeOffset.MinValue;
        LogMasterChanges(masters);
        _store.SetMasters(masters);

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
                AirportCapacity? previous = _store.Airport(icao).Capacity;
                if (capacity is null)
                {
                    _logger?.Warning($"{icao}: vIFF's capacity read failed; keeping {(previous is null ? "none" : "the last one")}");
                }
                else if (capacity != previous)
                {
                    _logger?.Info($"{icao}: vIFF says {(capacity.IsCdm ? "CDM" : "not CDM")}, rate {capacity.Rate}/h, taxi {capacity.TaxiMinutes} min");
                }

                _store.SetCapacity(icao, capacity ?? previous, now);
            }

            IReadOnlyList<DepartureRecord>? departures = await _api.GetDeparturesAsync(icao, cancellationToken).ConfigureAwait(false);
            if (departures is not null)
            {
                _store.ReplaceDepartures(icao, departures, now);
                _logger?.Debug($"{icao}: {departures.Count} departure(s) from vIFF");
            }
            else
            {
                _logger?.Warning($"{icao}: vIFF's departures read failed; showing the last ones");
            }
        }

        HashSet<string> watchedSet = new(watched, StringComparer.OrdinalIgnoreCase);
        _store.RetainAirports(watchedSet);
        await ReadNetworkAirportsAsync(traffic, watchedSet, now, cancellationToken).ConfigureAwait(false);
        await ReadStatusesAsync(now, cancellationToken).ConfigureAwait(false);
        foreach (FlightView flight in _store.Flights())
        {
            if (traffic[flight.Record.Callsign].Started && flight.Asat is null)
            {
                _store.ObserveStartUp(flight.Record.Callsign, started: true, DateTimeOffset.MinValue);
            }
        }

        // The client is only called while the plugin runs: a cycle the shutdown cancelled stops here.
        cancellationToken.ThrowIfCancellationRequested();
        if (_gate.CanWrite(out string callsign, out _))
        {
            foreach (string icao in _store.AirportsMasteredBy(callsign))
            {
                await _master.RunAsync(icao, traffic, now, cancellationToken).ConfigureAwait(false);
            }

            WriteScratchpadRemarks(callsign, traffic);
            await SendTaxiTimesAsync(traffic, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Synced?.Invoke();
    }

    private void LogMasterChanges(IReadOnlyList<MasterAirport> masters)
    {
        if (_logger is null)
        {
            return;
        }

        Dictionary<string, string> now = new(StringComparer.OrdinalIgnoreCase);
        foreach (MasterAirport master in masters)
        {
            now[master.Icao] = master.Position;
        }

        foreach (string icao in WatchedAirports())
        {
            AirportView airport = _store.Airport(icao);
            string before = airport.HasMaster ? airport.MasterPosition : string.Empty;
            string after = now.GetValueOrDefault(icao) ?? string.Empty;
            if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"{icao}: CDM master {(before.Length == 0 ? "none" : before)} -> {(after.Length == 0 ? "none" : after)}");
            }
        }
    }

    // Network statuses feed the airborne compliance items.
    private async Task ReadStatusesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_statuses is not null && now - _statusesReadAt >= s_statusRefresh)
        {
            _statusesReadAt = now;
            IReadOnlyDictionary<string, string>? statuses = await _api.GetAllStatusAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, string>? onTime = await _api.GetAllOnTimeAsync(cancellationToken).ConfigureAwait(false);
            _statuses.Replace(statuses, onTime);
            _logger?.Debug($"Network statuses: {statuses?.Count.ToString() ?? "read failed"}, on-time: {onTime?.Count.ToString() ?? "read failed"}");
        }
    }

    // CDMconfig.xml and its files reload every half hour, on request and when the package changes; event slots,
    // which change during an event, every five minutes.
    private async Task ReadConfigAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_config is null)
        {
            return;
        }

        bool reload = _reloadRequested || now - _configReadAt >= s_configRefresh || _config.SourceChanged();
        if (reload)
        {
            _reloadRequested = false;
            _configReadAt = now;
            _slotsReadAt = now;
            await _config.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (now - _slotsReadAt >= s_slotsRefresh)
        {
            _slotsReadAt = now;
            await _config.LoadSlotsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // At an airport without CDM, vIFF still takes each departure's taxi time (EXOT) from the taxi zone it stands in,
    // as the CDM plugin sends it, so its own TTOT and CTOT estimates hold. Sent again only when the time changes.
    private async Task SendTaxiTimesAsync(TrafficSnapshot traffic, CancellationToken cancellationToken)
    {
        if (_config is null || !traffic.Callsigns.Any())
        {
            return;
        }

        TaxiZones zones = _config.Current.Zones;
        HashSet<string> configured = new(_airports.GetConfigurations().Select(c => c.Icao), StringComparer.OrdinalIgnoreCase);
        int sent = 0;
        foreach (string callsign in traffic.Callsigns)
        {
            TrafficEntry entry = traffic[callsign];
            if (sent >= MaxExotPerCycle || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (entry.Started || !configured.Contains(entry.Origin) || _store.Airport(entry.Origin).IsCdm ||
                !zones.HasAirport(entry.Origin) || entry.Runway.Length == 0 || entry.Latitude is not double lat || entry.Longitude is not double lon)
            {
                continue;
            }

            TaxiZone? zone = zones.Find(entry.Origin, entry.Runway, lat, lon);
            if (zone is null || (_exotSent.TryGetValue(callsign, out int last) && last == zone.TaxiMinutes))
            {
                continue;
            }

            _exotSent[callsign] = zone.TaxiMinutes;
            sent++;
            bool ok = await _api.SendDpiAsync(callsign, $"EXOT/{zone.TaxiMinutes}", cancellationToken).ConfigureAwait(false);
            _logger?.Debug($"{callsign}: EXOT {zone.TaxiMinutes} min at {entry.Origin} runway {entry.Runway}{(ok ? string.Empty : ", refused")}");
        }

        if (_exotSent.Count > 2000)
        {
            _exotSent.Clear();
        }
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
