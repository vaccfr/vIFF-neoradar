using System;
using System.Collections.Generic;
using System.Linq;
using NeoRadar.PluginSdk.Airport;
using NeoRadar.PluginSdk.ControllerData;
using NeoRadar.PluginSdk.Flightplan;

namespace Viff.NeoRadar.Tests.Fakes;

internal sealed class FakeAirportApi : IAirportApi
{
    public List<AirportConfig> Configurations { get; } = new();

    public IReadOnlyList<AirportConfig> GetConfigurations() => Configurations.ToList();

    public AirportConfig? GetConfigurationByIcao(string icao) => Configurations.FirstOrDefault(c => c.Icao == icao);

    public bool IsDepRunwayActive(string icao, string runway) => false;

    public bool IsArrRunwayActive(string icao, string runway) => false;

    public bool SetRunwayStatus(string icao, string runway, RunwayType type) => false;

    public bool SetShowRunwayCenterlines(string icao, bool show) => false;

    public bool RemoveRunwayStatus(string icao, string runway, RunwayType type) => false;

    public BatchUpdateRunwaysResult BatchUpdateRunways(IReadOnlyList<RunwayStatusChange> toAdd, IReadOnlyList<RunwayStatusChange> toRemove) =>
        new(Array.Empty<RunwayStatusChange>(), toAdd, Array.Empty<RunwayStatusChange>(), toRemove);

    public bool DeleteAirport(string icao) => false;

    public bool SetAirportStatus(string icao, AirportStatus status) => false;
}

internal sealed class FakeFlightplanApi : IFlightplanApi
{
    public List<Flightplan> Plans { get; } = new();

    public void Add(string callsign, string origin, string destination, string eobt) =>
        Plans.Add(new Flightplan { Callsign = callsign, Origin = origin, Destination = destination, Eobt = eobt });

    public IReadOnlyList<Flightplan> GetAll() => Plans.ToList();

    public Flightplan? GetByCallsign(string callsign) => Plans.FirstOrDefault(p => p.Callsign == callsign);
}

internal sealed class FakeControllerDataApi : IControllerDataApi
{
    public List<(string Callsign, string Text)> Scratchpads { get; } = new();

    /// <summary>Gets each aircraft's nine strip boxes, as the client holds them.</summary>
    public Dictionary<string, string[]> Boxes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Callsign, int Index, string Text)> StripWrites { get; } = new();

    public List<(string Callsign, GroundStatus Status)> GroundStatusWrites { get; } = new();

    public Dictionary<string, ControllerDataModel> Models { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool RefuseStripWrites { get; set; }

    public static ControllerDataModel Model(string callsign, bool cleared, GroundStatus status = GroundStatus.None, string owner = "", bool mine = false) =>
        new(callsign, 0, string.Empty, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty, cleared, mine, owner, string.Empty,
            AssignedSpeedVariance.None, AttentionState.Unconcerned, status);

    public IReadOnlyList<ControllerDataModel> GetAll() => Models.Values.ToList();

    public ControllerDataModel? GetByCallsign(string callsign) => Models.GetValueOrDefault(callsign);

    public bool SetGroundStatus(string callsign, GroundStatus groundStatus)
    {
        GroundStatusWrites.Add((callsign, groundStatus));
        if (Models.TryGetValue(callsign, out ControllerDataModel? model))
        {
            Models[callsign] = model with { GroundStatus = groundStatus };
        }

        return true;
    }

    public IReadOnlyList<string>? GetFlightStripAnnotations(string callsign) =>
        Boxes.TryGetValue(callsign, out string[]? boxes) ? boxes : Enumerable.Repeat(string.Empty, 9).ToArray();

    public bool SetFlightStripAnnotation(string callsign, int index, string text)
    {
        StripWrites.Add((callsign, index, text));
        if (RefuseStripWrites)
        {
            return false;
        }

        if (!Boxes.TryGetValue(callsign, out string[]? boxes))
        {
            boxes = Enumerable.Repeat(string.Empty, 9).ToArray();
            Boxes[callsign] = boxes;
        }

        boxes[index] = text;
        return true;
    }

    public bool PushFlightStrip(string callsign, string toController) => true;

    public bool SetAssignedSquawk(string callsign, string squawk) => false;

    public bool SetClearedFlightLevel(string callsign, int clearedFlightLevel) => false;

    public bool SetAssignedHeading(string callsign, int heading) => false;

    public bool SetAssignedSpeed(string callsign, int speed, bool isMach, AssignedSpeedVariance variance) => false;

    public bool ClearAssignedSpeed(string callsign) => false;

    public bool SetScratchpad(string callsign, string scratchpad)
    {
        Scratchpads.Add((callsign, scratchpad));
        return true;
    }

    public bool SetAssignedDirect(string callsign, string waypointIdentifier) => false;

    public bool ClearAssignedDirect(string callsign) => false;

    public bool SetNextController(string callsign, string nextControllerCallsign) => false;

    public bool SetClearanceIssued(string callsign, bool issued) => false;

    public bool AssumeAircraft(string callsign) => false;

    public bool ReleaseAircraft(string callsign) => false;

    public bool HandoffAircraft(string callsign, string toController) => false;

    public bool AcceptHandoff(string callsign, string fromController) => false;

    public bool RejectHandoff(string callsign, string fromController) => false;

    public bool CancelHandoff(string callsign, string targetController) => false;
}
