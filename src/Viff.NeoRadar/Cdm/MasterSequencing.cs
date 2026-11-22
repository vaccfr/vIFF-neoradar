using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Logger;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Config;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Runs the master's work for one airport: applies TOBT and start-up requests, sequences, and writes changed times to vIFF.
/// </summary>
/// <remarks>Runs only on the sync job, so it is never re-entered; its own state needs no lock.</remarks>
internal sealed class MasterSequencing
{
    /// <summary>Gets how long before the TOBT a TSAT is generated.</summary>
    public static readonly TimeSpan TsatHorizon = TimeSpan.FromMinutes(35);

    /// <summary>Gets how long after the TSAT an unstarted flight that has not reported ready loses its place.</summary>
    public static readonly TimeSpan TsatExpiry = TimeSpan.FromMinutes(6);

    private readonly IViffApi _api;
    private readonly CdmStore _store;
    private readonly AirportControls _controls;
    private readonly ILoggerApi _logger;
    private readonly Action<string> _notify;
    private readonly EventSlots? _slots;
    private readonly Func<CdmSetup> _setup;

    // Airport/runway to the rate.txt line last used, so a change of line is logged once.
    private readonly Dictionary<string, string> _ratesInUse = new(StringComparer.OrdinalIgnoreCase);

    // Callsign to the TOBT ("HHMM") whose TSAT expired; the flight stays out of the sequence until its TOBT changes.
    private readonly Dictionary<string, string> _expired = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    public MasterSequencing(
        IViffApi api,
        CdmStore store,
        AirportControls controls,
        ILoggerApi logger,
        Action<string> notify,
        EventSlots? slots = null,
        Func<CdmSetup>? setup = null)
    {
        _slots = slots;
        _setup = setup ?? (() => CdmSetup.None);
        _api = api;
        _store = store;
        _controls = controls;
        _logger = logger;
        _notify = notify;
    }

    public async Task RunAsync(string icao, TrafficSnapshot traffic, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AirportView airport = _store.Airport(icao);
        if (airport.Capacity is null || !airport.IsCdm)
        {
            WarnOnce($"{icao}:not-cdm", $"{icao} is not a CDM airport on vIFF, so no TSATs are calculated.");
            return;
        }

        CdmSetup setup = _setup();
        CdmConfig config = setup.Config;
        bool lvo = _controls.IsLvo(icao);
        int airportRate = AirportRate(icao, airport.Capacity.Rate, config, lvo);
        if (airportRate <= 0 && !setup.Rates.Airports.Contains(icao))
        {
            WarnOnce($"{icao}:no-rate", $"No departure rate for {icao} in vIFF, rate.txt or CDMconfig.xml. Set one with .cdm rate {icao} <normal> <lvo>.");
            return;
        }

        int defaultTaxi = airport.Capacity.TaxiMinutes > 0 ? airport.Capacity.TaxiMinutes : config.DefaultTaxiMinutes;
        PlanContext context = new(icao, setup, lvo, airportRate, defaultTaxi, traffic.ActiveRunways(icao));

        List<PlannedFlight> plans = new();
        List<SequenceInput> inputs = new();
        foreach (DepartureRecord record in _store.Departures(icao).Where(r => !r.IsAirborne))
        {
            PlannedFlight? plan = Plan(record, traffic[record.Callsign], context, now);
            if (plan is null)
            {
                continue;
            }

            plans.Add(plan);
            if (plan.Input is not null)
            {
                inputs.Add(plan.Input);
            }
        }

        SequenceRules rules = new()
        {
            DefaultRate = Math.Max(airportRate, 1),
            EarliestTsatByRunway = _controls.StartupDelays(icao),
            EarliestTtotByRunway = _controls.DepartureDelays(icao),
            Bmi = config.Bmi,
            SidInterval = setup.Intervals.Count == 0 ? null : (flight, other) => setup.Intervals.Between(icao, flight.Runway, flight.Sid, other.Runway, other.Sid),
        };
        Dictionary<string, SequenceSlot> slots = Sequencer
            .Build(inputs, rules, now)
            .ToDictionary(s => s.Callsign, StringComparer.OrdinalIgnoreCase);

        int written = 0;
        int failed = 0;
        foreach (PlannedFlight plan in plans)
        {
            SequenceSlot? slot = slots.GetValueOrDefault(plan.Record.Callsign);
            bool? result = await WriteAsync(plan, slot, traffic[plan.Record.Callsign], cancellationToken).ConfigureAwait(false);
            if (result == true)
            {
                written++;
            }
            else if (result == false)
            {
                failed++;
            }

            if (slot?.CtotMissed == true)
            {
                WarnOnce(
                    $"{plan.Record.Callsign}:ctot:{plan.Record.EffectiveCtot}",
                    $"{plan.Record.Callsign} cannot take off within its CTOT {CdmTime.Display(plan.Record.EffectiveCtot)} -5/+10 " +
                    $"(earliest TTOT {CdmTime.ToHhmm(slot.Ttot)}). Submit a new TOBT to obtain a revised CTOT.");
            }
        }

        if (written > 0 || failed > 0)
        {
            _logger.Debug($"{icao}: sequenced {inputs.Count} of {plans.Count} flight(s){(config.Bmi ? " in BMI blocks" : string.Empty)}" +
                          $"{(lvo ? " under LVO" : string.Empty)}, airport rate {airportRate}/h, wrote {written}, {failed} failed");
        }
    }

    // The airport's rate when no rate.txt line applies: the controller's override, else vIFF's, else CDMconfig.xml's.
    private int AirportRate(string icao, int viffRate, CdmConfig config, bool lvo)
    {
        (int overrideNormal, int overrideLvo) = _controls.GetRates(icao);
        int normal = overrideNormal > 0 ? overrideNormal : viffRate > 0 ? viffRate : config.Rate;
        if (!lvo)
        {
            return normal;
        }

        int lvoRate = overrideLvo > 0 ? overrideLvo : config.RateLvo;
        if (lvoRate > 0)
        {
            return lvoRate;
        }

        WarnOnce($"{icao}:lvo-rate", $"LVO is on for {icao} but no LVO rate is set; using {normal}/h. Set one with .cdm rate {icao} <normal> <lvo>.");
        return normal;
    }

    // A flight's own rate: the controller's override wins, then the rate.txt line of its runway, then the airport rate.
    private (int Rate, RunwayRate? Line) FlightRate(PlanContext context, string runway, string sid)
    {
        (int overrideNormal, int overrideLvo) = _controls.GetRates(context.Icao);
        RunwayRate? line = context.Setup.Rates.Find(context.Icao, runway, sid, context.Runways.Arrivals, context.Runways.Departures);
        int lineRate = line is null ? 0 : context.Lvo ? line.LvoRate : line.Rate;
        int overrideRate = context.Lvo ? overrideLvo : overrideNormal;
        int rate = overrideRate > 0 ? overrideRate : lineRate > 0 ? lineRate : context.AirportRate;

        string key = $"{context.Icao}/{runway}";
        string description = line is null ? $"no rate.txt line, {rate}/h" : $"rate.txt line {line.LineNumber}, {rate}/h";
        if (!_ratesInUse.TryGetValue(key, out string? previous) || previous != description)
        {
            _ratesInUse[key] = description;
            _logger.Info($"{key}{(context.Lvo ? " (LVO)" : string.Empty)}: {description}" +
                         $"{(line is { Dependent.Count: > 0 } ? $", dependent {string.Join(",", line.Dependent)}" : string.Empty)}");
        }

        return (rate, line);
    }

    private PlannedFlight? Plan(DepartureRecord record, TrafficEntry traffic, PlanContext context, DateTimeOffset now)
    {
        CdmConfig config = context.Setup.Config;
        bool started = traffic.Started || record.Aobt.Length > 0;
        if (!started && record.Suspension.Length > 0 && !record.IsCdmSuspended)
        {
            // Suspended by the network (FLS-NRA, FLS-MR, FLS-GS): out of the sequence, and its data is left as vIFF holds it.
            return null;
        }

        CdmData cdm = record.Cdm;
        List<string> clears = new();
        string tobtText = cdm.Tobt;
        string asrt = cdm.Asrt;
        string? setBy = null;

        if (!started && CdmTime.ParseTimeOfDay(cdm.ReqTobt) is not null)
        {
            tobtText = CdmTime.Display(cdm.ReqTobt) + "00";
            setBy = cdm.ReqTobtType.Length > 0 ? cdm.ReqTobtType[..1].ToUpperInvariant() : "A";
            clears.Add("REQTOBT/NULL/NULL");
        }

        if (!started && CdmTime.ParseTimeOfDay(cdm.ReqAsrt) is not null)
        {
            asrt = CdmTime.Display(cdm.ReqAsrt);
            clears.Add("REQASRT/NULL");
        }

        // The EOBT seeds the TOBT; vIFF's own off-block time is not used because it echoes the TSAT sent to it.
        string seed = tobtText.Length > 0 ? tobtText : record.Eobt.Length > 0 ? record.Eobt : traffic.Eobt;
        DateTimeOffset? tobt = CdmTime.ParseNear(seed, now);
        if (tobt is null)
        {
            return null;
        }

        string tobtKey = CdmTime.Display(seed);
        DateTimeOffset? currentTsat = CdmTime.ParseNear(cdm.Tsat, now);
        DateTimeOffset? currentTtot = CdmTime.ParseNear(cdm.Ttot, now);
        bool tsatPast = currentTsat is not null && now > currentTsat.Value + TsatExpiry;

        // vIFF is the authority: a flight it holds FLS-CDM with no valid TSAT stays out, at the TOBT vIFF holds, even
        // after this master restarts or takes over. Lifting it writes a fresh TSAT, so it is not taken back in here.
        if (!started && record.IsCdmSuspended && (currentTsat is null || tsatPast))
        {
            string storedTobt = CdmTime.Display(cdm.Tobt.Length > 0 ? cdm.Tobt : record.Eobt.Length > 0 ? record.Eobt : traffic.Eobt);
            _expired.TryAdd(record.Callsign, storedTobt);
        }

        bool lifting = false;
        if (_expired.TryGetValue(record.Callsign, out string? expiredTobt) && expiredTobt != tobtKey)
        {
            _expired.Remove(record.Callsign);
            lifting = record.IsCdmSuspended;
        }

        bool suspending = false;
        bool invalidates = config.InvalidateAtTsat switch
        {
            TsatInvalidation.Never => false,
            TsatInvalidation.Always => true,
            _ => asrt.Length == 0,
        };
        if (!started && invalidates && tsatPast && !_expired.ContainsKey(record.Callsign))
        {
            _expired[record.Callsign] = tobtKey;
            suspending = !record.IsCdmSuspended;
        }

        bool excluded = !started && (_expired.ContainsKey(record.Callsign) || tobt.Value > now + TsatHorizon);
        TimeSpan exot = TimeSpan.FromMinutes(Exot(record, traffic, context));
        (int rate, RunwayRate? line) = FlightRate(context, traffic.Runway, traffic.Sid);
        int pressure = config.RunwayPressure < 0 ? 0 : line?.Pressure is int linePressure ? linePressure : config.RunwayPressure;
        DateTimeOffset? ctot = _controls.IsCtotIgnored(record.Callsign) ? null : CdmTime.ParseNear(record.EffectiveCtot, now);
        bool eventSlot = _slots?.Find(record.Cid, record.Callsign, record.Departure, record.Arrival).Length > 0;
        if (!excluded && rate <= 0)
        {
            WarnOnce($"{context.Icao}/{traffic.Runway}:no-rate", $"No departure rate for {context.Icao} runway {traffic.Runway}; its flights are not sequenced.");
            excluded = true;
        }
        SequenceInput? input = excluded
            ? null
            : new SequenceInput(
                record.Callsign,
                traffic.Runway,
                tobt.Value,
                exot,
                ctot,
                started,
                currentTsat,
                currentTtot,
                Priority: asrt.Length > 0 || (config.ReqTobtPriority && (cdm.Confirmed || setBy is not null)) || (config.EventPriority && eventSlot))
            {
                Sid = traffic.Sid,
                RatePerHour = rate,
                Dependent = line?.Dependent ?? Array.Empty<string>(),
                PressureMinutes = pressure,
            };

        return new PlannedFlight(record, CdmTime.ToHhmmss(tobt.Value), asrt, setBy, clears, exot, started, excluded, suspending, lifting, input);
    }

    // The CDM plugin's EXOT: the taxi zone the aircraft stands in, else vIFF's taxi time, else the default; de-icing adds
    // its time by wake, and a remote pad takes the zone's time via that pad when given, else the pad's extra minutes.
    private int Exot(DepartureRecord record, TrafficEntry traffic, PlanContext context)
    {
        CdmConfig config = context.Setup.Config;
        TaxiZone? zone = traffic.Latitude is double lat && traffic.Longitude is double lon && traffic.Runway.Length > 0
            ? context.Setup.Zones.Find(context.Icao, traffic.Runway, lat, lon)
            : null;
        int taxi = zone?.TaxiMinutes ?? (record.TaxiMinutes > 0 ? record.TaxiMinutes : context.DefaultTaxi);
        if (record.Cdm.Deice.Length == 0)
        {
            return taxi;
        }

        int deIce = config.DeIceMinutes.TryGetValue(traffic.WakeCategory, out int byWake) ? byWake : DeIce.MinutesFor(traffic.WakeCategory);
        string? pad = DeIce.RemotePad(record.Cdm.Deice);
        if (pad is null)
        {
            return taxi + deIce;
        }

        int padIndex = config.RemotePads.ToList().FindIndex(p => string.Equals(p.Name, pad, StringComparison.OrdinalIgnoreCase));
        if (zone is not null && padIndex >= 0 && padIndex < zone.RemoteTaxiMinutes.Count && zone.RemoteTaxiMinutes[padIndex] > 0)
        {
            return zone.RemoteTaxiMinutes[padIndex] + deIce;
        }

        int extra = _controls.DeIcePads(context.Icao).FirstOrDefault(p => string.Equals(p.Name, pad, StringComparison.OrdinalIgnoreCase)).ExtraTaxiMinutes;
        if (extra == 0 && padIndex >= 0)
        {
            extra = config.RemotePads[padIndex].ExtraTaxiMinutes;
        }

        return taxi + extra + deIce;
    }

    private sealed record PlanContext(
        string Icao,
        CdmSetup Setup,
        bool Lvo,
        int AirportRate,
        int DefaultTaxi,
        (IReadOnlyList<string> Arrivals, IReadOnlyList<string> Departures) Runways);

    // Returns true when written, false when a write failed, null when nothing needed writing.
    private async Task<bool?> WriteAsync(PlannedFlight plan, SequenceSlot? slot, TrafficEntry traffic, CancellationToken cancellationToken)
    {
        DepartureRecord record = plan.Record;
        CdmDataUpdate current = CdmDataUpdate.From(record);
        CdmDataUpdate target = current with
        {
            Tobt = plan.Started && current.Tobt.Length == 0 ? current.Tobt : plan.Tobt,
            Asrt = plan.Asrt,
            DepInfo = traffic.DepInfo.Length > 0 ? traffic.DepInfo : current.DepInfo,
        };

        if (!plan.Started)
        {
            target = target with
            {
                Tsat = slot is null ? string.Empty : CdmTime.ToHhmmss(slot.Tsat),
                Ttot = slot is null ? string.Empty : CdmTime.ToHhmmss(slot.Ttot),
            };
        }

        bool tsatChanged = CdmTime.Display(target.Tsat) != CdmTime.Display(current.Tsat);
        bool changed = tsatChanged ||
                       CdmTime.Display(target.Ttot) != CdmTime.Display(current.Ttot) ||
                       CdmTime.Display(target.Tobt) != CdmTime.Display(current.Tobt) ||
                       target.Asrt != current.Asrt ||
                       target.DepInfo != current.DepInfo;

        bool? result = null;
        if (changed)
        {
            // Nothing is kept locally: the next read of vIFF shows what was stored.
            result = await _api.SetCdmDataAsync(target, cancellationToken).ConfigureAwait(false);
            if (result == true)
            {
                // The OBT lets ETFMS revise the CTOT; a flight leaving an FLS-CDM suspension sends it even with a CTOT.
                if (tsatChanged && target.Tsat.Length > 0 && (record.Ctot.Length == 0 || plan.Lifting))
                {
                    await _api.SendDpiAsync(record.Callsign, $"OBT/{CdmTime.Display(target.Tsat)}/{(int)plan.Exot.TotalMinutes}", cancellationToken)
                        .ConfigureAwait(false);
                }

                if (plan.Suspending)
                {
                    await _api.SendDpiAsync(record.Callsign, "SUSP", cancellationToken).ConfigureAwait(false);
                    _notify($"{record.Callsign} missed its TSAT {CdmTime.Display(record.Cdm.Tsat)} and is suspended (FLS-CDM) until a new TOBT is set.");
                }
            }
        }

        if (result != false)
        {
            foreach (string clear in plan.Clears)
            {
                await _api.SendDpiAsync(record.Callsign, clear, cancellationToken).ConfigureAwait(false);
            }

            if (plan.SetBy is not null)
            {
                _store.NoteTobtSource(record.Callsign, plan.SetBy, plan.Tobt);
            }
        }

        return result;
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key))
        {
            _logger.Warning(message);
            _notify(message);
        }
    }

    private sealed record PlannedFlight(
        DepartureRecord Record,
        string Tobt,
        string Asrt,
        string? SetBy,
        IReadOnlyList<string> Clears,
        TimeSpan Exot,
        bool Started,
        bool Excluded,
        bool Suspending,
        bool Lifting,
        SequenceInput? Input);
}
