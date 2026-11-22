using System;
using System.Collections.Generic;
using System.Linq;
using NeoRadar.PluginSdk.ControllerData;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Strips;

/// <summary>
/// Does the master's strip work for the airports this client is master of: copies vIFF's times into box 0, fills a
/// TSAC waiting for its TSAT, and sets STUP for flights whose automatic start-up is armed.
/// </summary>
internal sealed class StripSync
{
    private static readonly TimeSpan s_autoStartupBefore = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_autoStartupAfter = TimeSpan.FromMinutes(6);

    private readonly StripStore _strips;
    private readonly StripWriter _writer;
    private readonly IControllerDataApi _controllerData;
    private readonly Notifier _notifier;

    public StripSync(StripStore strips, StripWriter writer, IControllerDataApi controllerData, Notifier notifier)
    {
        _strips = strips;
        _writer = writer;
        _controllerData = controllerData;
        _notifier = notifier;
    }

    /// <summary>
    /// Queues a box 0 write for each mastered flight whose strip differs from vIFF, as the CDM plugin's master keeps it.
    /// </summary>
    public void Mirror(IReadOnlyList<FlightView> flights, IReadOnlySet<string> masteredAirports)
    {
        foreach (FlightView flight in flights.Where(f => f.Watched && masteredAirports.Contains(f.Airport.Icao)))
        {
            DepartureRecord record = flight.Record;
            CdmData cdm = record.Cdm;
            string callsign = record.Callsign;
            _writer.Change(callsign, strip =>
            {
                StripData copy = strip
                    .With(StripData.Asrt, cdm.Asrt)
                    .With(StripData.Tobt, CdmTime.Display(cdm.Tobt))
                    .With(StripData.Tsat, cdm.Tsat)
                    .With(StripData.Ttot, cdm.Ttot)
                    .With(StripData.DeIce, DeIce.Label(cdm.Deice))
                    .With(StripData.SetBy, flight.TobtSetBy.Length > 0 ? flight.TobtSetBy : strip[StripData.SetBy])
                    .With(StripData.ManualCtot, record.HasManualCtot ? CdmTime.Display(cdm.Ctot) : string.Empty);
                return strip[StripData.Tsac] == StripData.TsacPending && cdm.Tsat.Length > 0
                    ? copy.With(StripData.Tsac, CdmTime.Display(cdm.Tsat))
                    : copy;
            }, urgent: false);
        }
    }

    /// <summary>
    /// Sets STUP once for each mastered flight armed for automatic start-up that is cleared, in its TSAT −5/+6 window,
    /// not yet started and not tracked by another controller, as the CDM plugin does.
    /// </summary>
    public void RunAutoStartup(IReadOnlyList<FlightView> flights, IReadOnlySet<string> masteredAirports, DateTimeOffset now)
    {
        foreach (FlightView flight in flights.Where(f => f.Watched && f.Airport.IsCdm && masteredAirports.Contains(f.Airport.Icao)))
        {
            string callsign = flight.Record.Callsign;
            if (_writer.Current(callsign)[StripData.AutoStartup] != StripData.AutoStartupArmed)
            {
                continue;
            }

            DateTimeOffset? tsat = CdmTime.ParseNear(flight.Record.Cdm.Tsat, now);
            if (tsat is null || now < tsat.Value - s_autoStartupBefore || now > tsat.Value + s_autoStartupAfter)
            {
                continue;
            }

            ControllerDataModel? data = _controllerData.GetByCallsign(callsign);
            if (data is null || !data.ClearanceIssued || data.GroundStatus != GroundStatus.None ||
                (data.OwnedByCallsign.Length > 0 && !data.OwnedByMe))
            {
                continue;
            }

            if (_controllerData.SetGroundStatus(callsign, GroundStatus.Start))
            {
                _writer.Change(callsign, s => s.With(StripData.AutoStartup, StripData.AutoStartupDone), urgent: true);
                _notifier.Say($"{callsign}: start-up approved automatically (TSAT {CdmTime.Display(flight.Record.Cdm.Tsat)}).");
            }
        }
    }

    /// <summary>
    /// Reads the strips of the flights shown, so the tags have them without reading the client per frame.
    /// </summary>
    public void Track(IEnumerable<FlightView> flights) => _strips.Track(flights.Select(f => f.Record.Callsign));
}
