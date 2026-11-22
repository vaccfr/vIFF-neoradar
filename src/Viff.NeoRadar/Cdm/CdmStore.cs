using System;
using System.Collections.Generic;
using System.Linq;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Represents what the plugin knows of one watched airport.
/// </summary>
/// <param name="Icao">The airport's ICAO code.</param>
/// <param name="Capacity">vIFF's capacity for the airport, or null before it is known.</param>
/// <param name="MasterPosition">The position vIFF lists as master, or empty when there is none.</param>
internal sealed record AirportView(string Icao, AirportCapacity? Capacity, string MasterPosition)
{
    public bool IsCdm => Capacity?.IsCdm == true;

    public bool HasMaster => MasterPosition.Length > 0;
}

/// <summary>
/// Represents one departure with everything the tags need about it.
/// </summary>
/// <param name="Record">The flight as vIFF last reported it, with this client's own writes applied.</param>
/// <param name="Airport">The departure airport.</param>
/// <param name="Asat">When this client first saw start-up approved, or null.</param>
/// <param name="TobtSetBy">"P" when the pilot set the TOBT, "A" when a controller did, or empty when unconfirmed.</param>
internal sealed record FlightView(DepartureRecord Record, AirportView Airport, DateTimeOffset? Asat, string TobtSetBy);

/// <summary>
/// Holds the departures, capacities and masters of the watched airports, as last read from vIFF.
/// </summary>
internal sealed class CdmStore
{
    // Guards every field; the sync job writes, the tag, command and window threads read.
    private readonly object _lock = new();
    private readonly Dictionary<string, List<DepartureRecord>> _departures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (AirportCapacity? Capacity, DateTimeOffset FetchedAt)> _capacities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _masters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _asat = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string By, string Tobt)> _tobtSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets when the departures were last read, or null before the first read.
    /// </summary>
    public DateTimeOffset? LastSync { get; private set; }

    public void ReplaceDepartures(string icao, IReadOnlyList<DepartureRecord> records, DateTimeOffset now)
    {
        lock (_lock)
        {
            _departures[icao] = records.ToList();
            foreach (DepartureRecord record in records)
            {
                if (record.Cdm.ReqTobt.Length > 0 && record.Cdm.ReqTobtType.Length > 0)
                {
                    _tobtSources[record.Callsign] = (record.Cdm.ReqTobtType[..1].ToUpperInvariant(), CdmTime.Display(record.Cdm.ReqTobt));
                }
            }

            LastSync = now;
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

    public IReadOnlyList<DepartureRecord> Departures(string icao)
    {
        lock (_lock)
        {
            return _departures.TryGetValue(icao, out List<DepartureRecord>? records) ? records.ToList() : Array.Empty<DepartureRecord>();
        }
    }

    public DepartureRecord? Find(string callsign)
    {
        lock (_lock)
        {
            return FindUnderLock(callsign);
        }
    }

    /// <summary>
    /// Replaces one flight's record, as a write this client made will read back.
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
    /// Gets every watched departure not yet airborne, with its airport.
    /// </summary>
    public IReadOnlyList<FlightView> Flights()
    {
        lock (_lock)
        {
            List<FlightView> views = new();
            foreach (KeyValuePair<string, List<DepartureRecord>> airport in _departures)
            {
                AirportView airportView = AirportUnderLock(airport.Key);
                foreach (DepartureRecord record in airport.Value)
                {
                    if (record.IsAirborne)
                    {
                        continue;
                    }

                    DateTimeOffset? asat = _asat.TryGetValue(record.Callsign, out DateTimeOffset seen) ? seen : null;
                    views.Add(new FlightView(record, airportView, asat, TobtSetByUnderLock(record)));
                }
            }

            return views;
        }
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

    private DepartureRecord? FindUnderLock(string callsign)
    {
        foreach (List<DepartureRecord> records in _departures.Values)
        {
            DepartureRecord? record = records.Find(r => string.Equals(r.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
            if (record is not null)
            {
                return record;
            }
        }

        return null;
    }
}
