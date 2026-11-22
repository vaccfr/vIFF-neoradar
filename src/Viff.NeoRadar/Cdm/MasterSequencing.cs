using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Logger;
using Viff.NeoRadar.Common;
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

    private const int DefaultTaxiMinutes = 15;

    private readonly IViffApi _api;
    private readonly CdmStore _store;
    private readonly AirportControls _controls;
    private readonly ILoggerApi _logger;
    private readonly Action<string> _notify;

    // Callsign to the TOBT ("HHMM") whose TSAT expired; the flight stays out of the sequence until its TOBT changes.
    private readonly Dictionary<string, string> _expired = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    public MasterSequencing(IViffApi api, CdmStore store, AirportControls controls, ILoggerApi logger, Action<string> notify)
    {
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

        int rate = _controls.EffectiveRate(icao, airport.Capacity.Rate, out bool lvoRateMissing);
        if (lvoRateMissing)
        {
            WarnOnce($"{icao}:lvo-rate", $"LVO is on for {icao} but no LVO rate is set; using {rate}/h. Set one with .cdm rate {icao} <normal> <lvo>.");
        }

        if (rate <= 0)
        {
            WarnOnce($"{icao}:no-rate", $"vIFF holds no departure rate for {icao}. Set one with .cdm rate {icao} <normal> <lvo>.");
            return;
        }

        int defaultTaxi = airport.Capacity.TaxiMinutes > 0 ? airport.Capacity.TaxiMinutes : DefaultTaxiMinutes;
        IReadOnlyList<(string Name, int ExtraTaxiMinutes)> pads = _controls.DeIcePads(icao);

        List<PlannedFlight> plans = new();
        List<SequenceInput> inputs = new();
        foreach (DepartureRecord record in _store.Departures(icao).Where(r => !r.IsAirborne))
        {
            PlannedFlight? plan = Plan(record, traffic[record.Callsign], defaultTaxi, pads, now);
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

        Dictionary<string, SequenceSlot> slots = Sequencer
            .Build(inputs, rate, _controls.StartupDelays(icao), _controls.DepartureDelays(icao), now)
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
            _logger.Debug($"{icao}: sequenced {inputs.Count} flight(s) at {rate}/h, wrote {written}, {failed} failed");
        }
    }

    private PlannedFlight? Plan(
        DepartureRecord record,
        TrafficEntry traffic,
        int defaultTaxi,
        IReadOnlyList<(string Name, int ExtraTaxiMinutes)> pads,
        DateTimeOffset now)
    {
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
        if (!started && asrt.Length == 0 && tsatPast && !_expired.ContainsKey(record.Callsign))
        {
            _expired[record.Callsign] = tobtKey;
            suspending = !record.IsCdmSuspended;
        }

        bool excluded = !started && (_expired.ContainsKey(record.Callsign) || tobt.Value > now + TsatHorizon);
        TimeSpan exot = TimeSpan.FromMinutes(Exot(record, traffic, defaultTaxi, pads));
        DateTimeOffset? ctot = CdmTime.ParseNear(record.EffectiveCtot, now);
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
                Priority: cdm.Confirmed || setBy is not null || asrt.Length > 0);

        return new PlannedFlight(record, CdmTime.ToHhmmss(tobt.Value), asrt, setBy, clears, exot, started, excluded, suspending, lifting, input);
    }

    private static int Exot(DepartureRecord record, TrafficEntry traffic, int defaultTaxi, IReadOnlyList<(string Name, int ExtraTaxiMinutes)> pads)
    {
        int taxi = record.TaxiMinutes > 0 ? record.TaxiMinutes : defaultTaxi;
        if (record.Cdm.Deice.Length == 0)
        {
            return taxi;
        }

        string? pad = DeIce.RemotePad(record.Cdm.Deice);
        int padTaxi = pad is null ? 0 : pads.FirstOrDefault(p => string.Equals(p.Name, pad, StringComparison.OrdinalIgnoreCase)).ExtraTaxiMinutes;
        return taxi + padTaxi + DeIce.MinutesFor(traffic.WakeCategory);
    }

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
