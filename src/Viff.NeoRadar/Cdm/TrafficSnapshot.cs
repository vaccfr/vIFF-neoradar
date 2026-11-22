using System;
using System.Collections.Generic;
using NeoRadar.PluginSdk.ControllerData;
using NeoRadar.PluginSdk.Flightplan;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Represents what the client knows of one flight that vIFF does not: ground status, runway, SID and wake.
/// </summary>
internal sealed record TrafficEntry(
    GroundStatus GroundStatus,
    string Runway,
    string Sid,
    string WakeCategory,
    string Eobt,
    string Scratchpad)
{
    /// <summary>Gets a value indicating whether start-up has been approved.</summary>
    public bool Started => GroundStatus != GroundStatus.None;

    /// <summary>Gets the runway and SID as vIFF stores them, "RWY/SID", or empty when no runway is known.</summary>
    public string DepInfo => Runway.Length == 0 ? string.Empty : $"{Runway}/{Sid}";
}

/// <summary>
/// Holds the client's flight plans and controller data, read once per cycle so no handler calls the host per flight.
/// </summary>
internal sealed class TrafficSnapshot
{
    private static readonly TrafficEntry s_unknown = new(GroundStatus.None, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    private readonly IReadOnlyDictionary<string, TrafficEntry> _entries;

    public TrafficSnapshot(IReadOnlyDictionary<string, TrafficEntry> entries)
    {
        _entries = entries;
    }

    /// <summary>
    /// Gets an empty snapshot, for a client that is not connected.
    /// </summary>
    public static TrafficSnapshot Empty { get; } = new(new Dictionary<string, TrafficEntry>());

    /// <summary>
    /// Reads every flight plan and controller data record from the client.
    /// </summary>
    public static TrafficSnapshot Read(IFlightplanApi flightplans, IControllerDataApi controllerData)
    {
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
                runway,
                sid,
                plan.WakeCategory,
                plan.Eobt,
                scratchpads.GetValueOrDefault(plan.Callsign) ?? string.Empty);
        }

        foreach (KeyValuePair<string, GroundStatus> status in statuses)
        {
            entries.TryAdd(status.Key, s_unknown with { GroundStatus = status.Value, Scratchpad = scratchpads[status.Key] });
        }

        return new TrafficSnapshot(entries);
    }

    public TrafficEntry this[string callsign] => _entries.TryGetValue(callsign, out TrafficEntry? entry) ? entry : s_unknown;
}
