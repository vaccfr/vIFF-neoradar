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

    public IReadOnlyList<ControllerDataModel> GetAll() => Array.Empty<ControllerDataModel>();

    public ControllerDataModel? GetByCallsign(string callsign) => null;

    public bool SetGroundStatus(string callsign, GroundStatus groundStatus) => false;

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
