using System;
using System.Collections.Generic;
using System.Linq;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Represents what the plugin knows of one airport.
/// </summary>
/// <param name="Icao">The airport's ICAO code.</param>
/// <param name="Capacity">vIFF's capacity for the airport, or null when it has not been read.</param>
/// <param name="MasterPosition">The position vIFF lists as master, or empty when there is none.</param>
internal sealed record AirportView(string Icao, AirportCapacity? Capacity, string MasterPosition)
{
    /// <summary>
    /// Gets a value indicating whether the airport runs CDM: as vIFF's CAD says, or, before it is read, because it has a master.
    /// </summary>
    public bool IsCdm => Capacity?.IsCdm ?? HasMaster;

    public bool HasMaster => MasterPosition.Length > 0;
}

/// <summary>
/// Represents one departure with everything the tags need about it.
/// </summary>
/// <param name="Record">The flight as vIFF last reported it, with this client's own writes and the network CTOT applied.</param>
/// <param name="Airport">The departure airport.</param>
/// <param name="Asat">When this client first saw start-up approved, or null.</param>
/// <param name="TobtSetBy">"P" when the pilot set the TOBT, "A" when a controller did, or empty when unconfirmed.</param>
/// <param name="Watched">True for a departure from one of the client's own airports, false for one from elsewhere on the network.</param>
internal sealed record FlightView(DepartureRecord Record, AirportView Airport, DateTimeOffset? Asat, string TobtSetBy, bool Watched = true);

/// <summary>
/// Holds what vIFF last reported: the departures of the watched airports, the departures of other mastered airports the
/// client has traffic from, every CTOT on the network, capacities and masters.
/// </summary>
internal sealed class CdmStore
{
    // Guards every field; the sync job writes, the tag, command and window threads read.
    private readonly object _lock = new();
    private readonly Dictionary<string, List<DepartureRecord>> _departures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (List<DepartureRecord> Records, DateTimeOffset FetchedAt)> _networkDepartures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RestrictedFlight> _networkCtots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (AirportCapacity? Capacity, DateTimeOffset FetchedAt)> _capacities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _masters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _asat = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string By, string Tobt)> _tobtSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets when the departures were last read, or null before the first read.
    /// </summary>
    public DateTimeOffset? LastSync
    {
        get { lock (_lock) { return _lastSync; } }
    }

    private DateTimeOffset? _lastSync;

    public void ReplaceDepartures(string icao, IReadOnlyList<DepartureRecord> records, DateTimeOffset now)
    {
        lock (_lock)
        {
            _departures[icao] = records.ToList();
            NoteRequestsUnderLock(records);
            _lastSync = now;
        }
    }

    /// <summary>
    /// Stores the departures of an airport the client does not watch but has traffic from.
    /// </summary>
    public void ReplaceNetworkDepartures(string icao, IReadOnlyList<DepartureRecord> records, DateTimeOffset now)
    {
        lock (_lock)
        {
            _networkDepartures[icao] = (records.ToList(), now);
            NoteRequestsUnderLock(records);
        }
    }

    /// <summary>
    /// Gets when an unwatched airport's departures were last read, or null when never.
    /// </summary>
    public DateTimeOffset? NetworkFetchedAt(string icao)
    {
        lock (_lock)
        {
            return _networkDepartures.TryGetValue(icao, out (List<DepartureRecord> Records, DateTimeOffset FetchedAt) entry) ? entry.FetchedAt : null;
        }
    }

    /// <summary>
    /// Drops the departures of unwatched airports the client no longer has traffic from.
    /// </summary>
    public void RetainNetworkAirports(IReadOnlySet<string> airports)
    {
        lock (_lock)
        {
            foreach (string icao in _networkDepartures.Keys.Where(k => !airports.Contains(k)).ToList())
            {
                _networkDepartures.Remove(icao);
            }
        }
    }

    /// <summary>
    /// Replaces the network-wide list of CTOTs.
    /// </summary>
    public void SetNetworkCtots(IReadOnlyList<RestrictedFlight> flights)
    {
        lock (_lock)
        {
            _networkCtots.Clear();
            foreach (RestrictedFlight flight in flights)
            {
                _networkCtots[flight.Callsign] = flight;
            }
        }
    }

    public void SetCapacity(string icao, AirportCapacity? capacity, DateTimeOffset fetchedAt)
    {
        lock (_lock)
        {
            _capacities[icao] = (capacity, fetchedAt);
        }
    }

    /// <summary>
    /// Returns whether the airport's capacity was read, successfully or not, after <paramref name="since"/>.
    /// </summary>
    public bool HasFreshCapacity(string icao, DateTimeOffset since)
    {
        lock (_lock)
        {
            return _capacities.TryGetValue(icao, out (AirportCapacity? Capacity, DateTimeOffset FetchedAt) entry) && entry.FetchedAt >= since;
        }
    }

    public void SetMasters(IReadOnlyList<MasterAirport> masters)
    {
        lock (_lock)
        {
            _masters.Clear();
            foreach (MasterAirport master in masters)
            {
                _masters[master.Icao] = master.Position;
            }
        }
    }

    /// <summary>
    /// Records a master change this client made, before the next read of the master list confirms it.
    /// </summary>
    public void SetMaster(string icao, string? position)
    {
        lock (_lock)
        {
            if (position is null)
            {
                _masters.Remove(icao);
            }
            else
            {
                _masters[icao] = position;
            }
        }
    }

    /// <summary>
    /// Gets the airports vIFF lists with <paramref name="position"/> as master.
    /// </summary>
    public IReadOnlyList<string> AirportsMasteredBy(string position)
    {
        lock (_lock)
        {
            return _masters.Where(m => string.Equals(m.Value, position, StringComparison.OrdinalIgnoreCase)).Select(m => m.Key).ToList();
        }
    }

    /// <summary>
    /// Drops what is held for airports no longer watched.
    /// </summary>
    public void RetainAirports(IReadOnlySet<string> watched)
    {
        lock (_lock)
        {
            foreach (string icao in _departures.Keys.Where(k => !watched.Contains(k)).ToList())
            {
                _departures.Remove(icao);
            }
        }
    }

    public AirportView Airport(string icao)
    {
        lock (_lock)
        {
            return AirportUnderLock(icao);
        }
    }

    public IReadOnlyList<AirportView> Airports(IEnumerable<string> icaos)
    {
        lock (_lock)
        {
            return icaos.Select(AirportUnderLock).ToList();
        }
    }

    /// <summary>
    /// Gets a watched airport's departures, each with its network CTOT.
    /// </summary>
    public IReadOnlyList<DepartureRecord> Departures(string icao)
    {
        lock (_lock)
        {
            return _departures.TryGetValue(icao, out List<DepartureRecord>? records)
                ? records.Select(WithNetworkCtotUnderLock).ToList()
                : Array.Empty<DepartureRecord>();
        }
    }

    /// <summary>
    /// Finds a departure of a watched airport, with its network CTOT, or null.
    /// </summary>
    public DepartureRecord? Find(string callsign)
    {
        lock (_lock)
        {
            foreach (List<DepartureRecord> records in _departures.Values)
            {
                DepartureRecord? record = records.Find(r => string.Equals(r.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
                if (record is not null)
                {
                    return WithNetworkCtotUnderLock(record);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Replaces one watched flight's record, as a write this client made will read back.
    /// </summary>
    public void Update(string callsign, Func<DepartureRecord, DepartureRecord> change)
    {
        lock (_lock)
        {
            foreach (List<DepartureRecord> records in _departures.Values)
            {
                int index = records.FindIndex(r => string.Equals(r.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    records[index] = change(records[index]);
                    return;
                }
            }
        }
    }

    public void NoteTobtSource(string callsign, string by, string tobt)
    {
        lock (_lock)
        {
            _tobtSources[callsign] = (by, CdmTime.Display(tobt));
        }
    }

    /// <summary>
    /// Records when start-up was first seen approved, and forgets it when the status goes back to none.
    /// </summary>
    public void ObserveStartUp(string callsign, bool started, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!started)
            {
                _asat.Remove(callsign);
            }
            else
            {
                _asat.TryAdd(callsign, now);
            }
        }
    }

    /// <summary>
    /// Gets every departure of the watched airports not yet airborne.
    /// </summary>
    public IReadOnlyList<FlightView> Flights()
    {
        lock (_lock)
        {
            List<FlightView> views = new();
            foreach (KeyValuePair<string, List<DepartureRecord>> airport in _departures)
            {
                AirportView airportView = AirportUnderLock(airport.Key);
                foreach (DepartureRecord record in airport.Value.Where(r => !r.IsAirborne))
                {
                    views.Add(ViewUnderLock(WithNetworkCtotUnderLock(record), airportView, watched: true));
                }
            }

            return views;
        }
    }

    /// <summary>
    /// Gets the flights the client holds that depart from elsewhere and are not yet airborne: those of other mastered
    /// airports with their CDM times, and every other flight with a CTOT.
    /// </summary>
    public IReadOnlyList<FlightView> NetworkFlights(TrafficSnapshot traffic)
    {
        lock (_lock)
        {
            HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
            foreach (List<DepartureRecord> records in _departures.Values)
            {
                taken.UnionWith(records.Select(r => r.Callsign));
            }

            List<FlightView> views = new();
            foreach (KeyValuePair<string, (List<DepartureRecord> Records, DateTimeOffset FetchedAt)> airport in _networkDepartures)
            {
                AirportView airportView = AirportUnderLock(airport.Key);
                foreach (DepartureRecord record in airport.Value.Records)
                {
                    if (!record.IsAirborne && traffic.Contains(record.Callsign) && taken.Add(record.Callsign))
                    {
                        views.Add(ViewUnderLock(WithNetworkCtotUnderLock(record), airportView, watched: false));
                    }
                }
            }

            foreach (RestrictedFlight flight in _networkCtots.Values)
            {
                if (flight.Atot.Length > 0 || !traffic.Contains(flight.Callsign) || !taken.Add(flight.Callsign))
                {
                    continue;
                }

                TrafficEntry entry = traffic[flight.Callsign];
                DepartureRecord record = new()
                {
                    Callsign = flight.Callsign,
                    Departure = entry.Origin,
                    Eobt = entry.Eobt,
                    Ctot = flight.Ctot,
                    Regulation = flight.Regulation,
                    CdmStatus = flight.CdmStatus,
                };
                views.Add(ViewUnderLock(record, AirportUnderLock(entry.Origin), watched: false));
            }

            return views;
        }
    }

    // An ATFCM CTOT always applies, so one the network list holds fills a record that the airport feed left without one.
    private DepartureRecord WithNetworkCtotUnderLock(DepartureRecord record)
    {
        if (record.Ctot.Length > 0 ||
            !_networkCtots.TryGetValue(record.Callsign, out RestrictedFlight? flight) ||
            flight.Atot.Length > 0)
        {
            return record;
        }

        return record with
        {
            Ctot = flight.Ctot,
            Regulation = record.Regulation.Length > 0 ? record.Regulation : flight.Regulation,
        };
    }

    private void NoteRequestsUnderLock(IReadOnlyList<DepartureRecord> records)
    {
        foreach (DepartureRecord record in records)
        {
            if (record.Cdm.ReqTobt.Length > 0 && record.Cdm.ReqTobtType.Length > 0)
            {
                _tobtSources[record.Callsign] = (record.Cdm.ReqTobtType[..1].ToUpperInvariant(), CdmTime.Display(record.Cdm.ReqTobt));
            }
        }
    }

    private FlightView ViewUnderLock(DepartureRecord record, AirportView airport, bool watched)
    {
        DateTimeOffset? asat = _asat.TryGetValue(record.Callsign, out DateTimeOffset seen) ? seen : null;
        return new FlightView(record, airport, asat, TobtSetByUnderLock(record), watched);
    }

    private string TobtSetByUnderLock(DepartureRecord record)
    {
        string tobt = CdmTime.Display(record.Cdm.Tobt);
        if (tobt.Length > 0 && _tobtSources.TryGetValue(record.Callsign, out (string By, string Tobt) source) && source.Tobt == tobt)
        {
            return source.By;
        }

        return record.Cdm.Confirmed ? "P" : string.Empty;
    }

    private AirportView AirportUnderLock(string icao)
    {
        AirportCapacity? capacity = _capacities.TryGetValue(icao, out (AirportCapacity? Capacity, DateTimeOffset FetchedAt) entry) ? entry.Capacity : null;
        string master = _masters.TryGetValue(icao, out string? position) ? position : string.Empty;
        return new AirportView(icao.ToUpperInvariant(), capacity, master);
    }
}
