using System;
using System.Collections.Generic;
using System.Linq;
using NeoRadar.PluginSdk.Aircraft;
using NeoRadar.PluginSdk.Airport;
using NeoRadar.PluginSdk.ControllerData;
using NeoRadar.PluginSdk.Flightplan;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Represents what the client knows of one flight that vIFF does not: ground status, runway, SID and wake.
/// </summary>
internal sealed record TrafficEntry(
    GroundStatus GroundStatus,
    string Origin,
    string Runway,
    string Sid,
    string WakeCategory,
    string Eobt,
    string Scratchpad)
{
    /// <summary>Gets a value indicating whether start-up has been approved.</summary>
    public bool Started => GroundStatus != GroundStatus.None;

    /// <summary>Gets the aircraft's latitude, for taxi zones, or null when the client has no position.</summary>
    public double? Latitude { get; init; }

    /// <summary>Gets the aircraft's longitude, for taxi zones, or null when the client has no position.</summary>
    public double? Longitude { get; init; }

    /// <summary>Gets the runway and SID as vIFF stores them, "RWY/SID", or empty when no runway is known.</summary>
    public string DepInfo => Runway.Length == 0 ? string.Empty : $"{Runway}/{Sid}";
}

/// <summary>
/// Holds the client's flight plans and controller data, read once per cycle so no handler calls the host per flight.
/// </summary>
internal sealed class TrafficSnapshot
{
    private static readonly TrafficEntry s_unknown = new(GroundStatus.None, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    private static readonly (IReadOnlyList<string>, IReadOnlyList<string>) s_noRunways = (Array.Empty<string>(), Array.Empty<string>());

    private readonly IReadOnlyDictionary<string, TrafficEntry> _entries;
    private readonly IReadOnlyDictionary<string, (IReadOnlyList<string> Arrivals, IReadOnlyList<string> Departures)> _runways;

    public TrafficSnapshot(
        IReadOnlyDictionary<string, TrafficEntry> entries,
        IReadOnlyDictionary<string, (IReadOnlyList<string> Arrivals, IReadOnlyList<string> Departures)>? runways = null)
    {
        _entries = entries;
        _runways = runways ?? new Dictionary<string, (IReadOnlyList<string>, IReadOnlyList<string>)>();
    }

    /// <summary>
    /// Gets an airport's active arrival and departure runways, as the client's runway configuration holds them.
    /// </summary>
    public (IReadOnlyList<string> Arrivals, IReadOnlyList<string> Departures) ActiveRunways(string icao) =>
        _runways.TryGetValue(icao, out (IReadOnlyList<string> Arrivals, IReadOnlyList<string> Departures) runways) ? runways : s_noRunways;

    /// <summary>
    /// Gets an empty snapshot, for a client that is not connected.
    /// </summary>
    public static TrafficSnapshot Empty { get; } = new(new Dictionary<string, TrafficEntry>());

    /// <summary>
    /// Reads every flight plan and controller data record from the client.
    /// </summary>
    public static TrafficSnapshot Read(IFlightplanApi flightplans, IControllerDataApi controllerData, IAircraftApi? aircraft = null, IAirportApi? airports = null)
    {
        Dictionary<string, (double, double)> positions = new(StringComparer.OrdinalIgnoreCase);
        foreach (Aircraft target in aircraft?.GetAll() ?? Array.Empty<Aircraft>())
        {
            positions[target.Callsign] = (target.Position.Latitude, target.Position.Longitude);
        }

        Dictionary<string, (IReadOnlyList<string>, IReadOnlyList<string>)> runways = new(StringComparer.OrdinalIgnoreCase);
        foreach (AirportConfig config in airports?.GetConfigurations() ?? Array.Empty<AirportConfig>())
        {
            runways[config.Icao] = (config.ArrRunways.ToList(), config.DepRunways.ToList());
        }

        Dictionary<string, GroundStatus> statuses = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> scratchpads = new(StringComparer.OrdinalIgnoreCase);
        foreach (ControllerDataModel data in controllerData.GetAll())
        {
            statuses[data.Callsign] = data.GroundStatus;
            scratchpads[data.Callsign] = data.Scratchpad;
        }

        Dictionary<string, TrafficEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        foreach (Flightplan plan in flightplans.GetAll())
        {
            string runway = plan.Route.DepRunway.Length > 0 ? plan.Route.DepRunway : plan.Route.SuggestedDepRunway;
            string sid = plan.Route.Sid.Length > 0 ? plan.Route.Sid : plan.Route.SuggestedSid;
            entries[plan.Callsign] = new TrafficEntry(
                statuses.GetValueOrDefault(plan.Callsign),
                plan.Origin.ToUpperInvariant(),
                runway,
                sid,
                plan.WakeCategory,
                plan.Eobt,
                scratchpads.GetValueOrDefault(plan.Callsign) ?? string.Empty)
            {
                Latitude = positions.TryGetValue(plan.Callsign, out (double Lat, double Lon) at) ? at.Lat : null,
                Longitude = positions.TryGetValue(plan.Callsign, out (double Lat, double Lon) at2) ? at2.Lon : null,
            };
        }

        foreach (KeyValuePair<string, GroundStatus> status in statuses)
        {
            entries.TryAdd(status.Key, s_unknown with { GroundStatus = status.Value, Scratchpad = scratchpads[status.Key] });
        }

        return new TrafficSnapshot(entries, runways);
    }

    /// <summary>
    /// Gets the callsigns the client holds a flight plan or controller data for.
    /// </summary>
    public IEnumerable<string> Callsigns => _entries.Keys;

    /// <summary>
    /// Returns whether the client holds the flight.
    /// </summary>
    public bool Contains(string callsign) => _entries.ContainsKey(callsign);

    public TrafficEntry this[string callsign] => _entries.TryGetValue(callsign, out TrafficEntry? entry) ? entry : s_unknown;
}
