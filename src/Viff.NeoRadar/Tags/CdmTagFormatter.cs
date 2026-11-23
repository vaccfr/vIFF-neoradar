using System;
using System.Collections.Generic;
using System.Linq;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Strips;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Tags;

/// <summary>
/// Specifies a tag item the plugin registers; the names match the CDM plugin's columns.
/// </summary>
internal enum CdmItem
{
    Tobt,
    SetBy,
    Tsat,
    Ttg,
    Ttot,
    Ctot,
    Etfms,
    Ready,
    Asrt,
    Asat,
    DeIce,
    Message,
    Regulation,
    Eobt,
    Tsac,
    Ctoc,
    Phase,
    Delay,
    AutoStartup,
    EventSlot,
    Time,
    TimeShort,
    Status,
    Sequence,
}

/// <summary>
/// Specifies where a listed departure stands, for lists that split the departures by what they need now.
/// </summary>
internal static class CdmStage
{
    /// <summary>The TSAT (or TOBT while there is none) is more than 10 minutes away, and nothing needs doing.</summary>
    public const string Later = "later";

    /// <summary>Start-up is due within 10 minutes, or the flight is ready, suspended or past its window.</summary>
    public const string Now = "now";

    /// <summary>Start-up was approved and the flight is still on the ground.</summary>
    public const string Started = "started";

    /// <summary>How long before its start-up time a flight moves from later to now.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Represents what the tags show beyond vIFF's record: the flight strip, a booked event slot and an ignored CTOT.
/// </summary>
internal sealed record FlightExtras(StripData Strip, string EventSlot, bool CtotIgnored)
{
    public static FlightExtras None { get; } = new(StripData.Empty, string.Empty, false);
}

/// <summary>
/// Specifies where the time stands against a flight's TSAT, as the TSAT colours show it.
/// </summary>
internal enum TsatState
{
    /// <summary>Not a CDM flight, or start-up already approved.</summary>
    None,

    /// <summary>No TSAT calculated yet; the item shows "~".</summary>
    Pending,

    /// <summary>Earlier than TSAT −5.</summary>
    Future,

    /// <summary>The start-up approval window, TSAT −5 to TSAT +5.</summary>
    Window,

    /// <summary>TSAT +5 to TSAT +6, when the TSAT flashes.</summary>
    Closing,

    /// <summary>Past TSAT +6 without start-up; the flight has lost its place.</summary>
    Expired,

    /// <summary>vIFF holds the flight plan suspended (FLS); it is out of the sequence.</summary>
    Suspended,
}

/// <summary>
/// Represents one tag item's text and colour, null colour keeping the label's own.
/// </summary>
internal readonly record struct TagText(string Text, (byte R, byte G, byte B)? Colour = null);

/// <summary>
/// Represents everything pushed for one flight: tag items and expression values.
/// </summary>
internal sealed record FlightTags(
    IReadOnlyDictionary<CdmItem, TagText> Items,
    bool IsCdm,
    bool Ready,
    bool HasCtot,
    bool Informed,
    bool Suspended,
    TsatState Tsat,
    string Etfms,
    string Stage = "");

/// <summary>
/// Turns a flight's CDM data into tag texts and colours, as the CDM plugin shows them.
/// </summary>
internal static class CdmTagFormatter
{
    public static readonly (byte, byte, byte) DarkGreen = (0, 192, 0);
    public static readonly (byte, byte, byte) LightGreen = (143, 216, 148);
    public static readonly (byte, byte, byte) Yellow = (245, 239, 13);
    public static readonly (byte, byte, byte) Orange = (237, 133, 46);
    public static readonly (byte, byte, byte) Red = (190, 0, 0);
    public static readonly (byte, byte, byte) Grey = (128, 128, 128);

    private static readonly TimeSpan s_windowHalf = TimeSpan.FromMinutes(5);

    /// <param name="flight">The flight.</param>
    /// <param name="now">The current time.</param>
    /// <param name="flashOn">Which half of the flash cycle to draw, alternating each second.</param>
    public static FlightTags Format(FlightView flight, DateTimeOffset now, bool flashOn, FlightExtras? extras = null)
    {
        extras ??= FlightExtras.None;
        StripData strip = extras.Strip;
        DepartureRecord record = flight.Record;
        bool isCdm = flight.Airport.IsCdm;
        bool started = flight.Asat is not null || record.Aobt.Length > 0;
        DateTimeOffset? tsat = isCdm ? CdmTime.ParseNear(record.Cdm.Tsat, now) : null;
        DateTimeOffset? ttot = isCdm ? CdmTime.ParseNear(record.Cdm.Ttot, now) : null;
        bool suspended = record.Suspension.Length > 0;
        TsatState state = suspended && isCdm && !started ? TsatState.Suspended : StateOf(isCdm, started, tsat, now);
        string ctot = CdmTime.Display(record.EffectiveCtot);
        string etfms = Etfms(record);

        TagText tobtText = suspended ? Tobt(record, isCdm, now) with { Colour = Red } : Tobt(record, isCdm, now);
        TagText tsatText = Tsat(state, tsat, flashOn);
        (string label, string letter, TagText time) = CurrentTime(record, isCdm, started, state, tobtText, tsatText, ttot);
        Dictionary<CdmItem, TagText> items = new()
        {
            [CdmItem.Tobt] = tobtText,
            [CdmItem.SetBy] = new TagText(isCdm ? (flight.TobtSetBy.Length > 0 ? flight.TobtSetBy : strip[StripData.SetBy]) : string.Empty, DarkGreen),
            [CdmItem.Tsat] = tsatText,
            [CdmItem.Ttg] = Ttg(state, tsat, now),
            [CdmItem.Ttot] = new TagText(ttot is null ? string.Empty : CdmTime.ToHhmm(ttot.Value)),
            [CdmItem.Ctot] = extras.CtotIgnored && ctot.Length > 0 ? new TagText(ctot, Grey) : Ctot(record, ctot, ttot, now),
            [CdmItem.Etfms] = new TagText(etfms, EtfmsColour(etfms)),
            [CdmItem.Ready] = new TagText(flight.Watched ? "RDY" : string.Empty, record.Cdm.Asrt.Length > 0 ? DarkGreen : Red),
            [CdmItem.Asrt] = new TagText(CdmTime.Display(record.Cdm.Asrt)),
            [CdmItem.Asat] = new TagText(flight.Asat is { } asat && asat != DateTimeOffset.MinValue ? CdmTime.ToHhmm(asat) : string.Empty),
            [CdmItem.DeIce] = new TagText(DeIce.Label(record.Cdm.Deice), Yellow),
            [CdmItem.Message] = new TagText(isCdm && flight.Watched ? "MSG" : string.Empty, record.Informed ? DarkGreen : Red),
            [CdmItem.Regulation] = new TagText(ctot.Length > 0 ? (record.HasManualCtot ? CdmDataUpdate.ManualReason : record.Regulation) : string.Empty),
            [CdmItem.Eobt] = new TagText(CdmTime.Display(record.Eobt)),
            [CdmItem.Tsac] = Tsac(strip[StripData.Tsac], tsat, now),
            [CdmItem.Ctoc] = Ctoc(strip[StripData.Ctoc], ctot, now),
            [CdmItem.Phase] = Phase(record, isCdm, started, state, now),
            [CdmItem.Delay] = Delay(record, tsat, now),
            [CdmItem.AutoStartup] = strip[StripData.AutoStartup] switch
            {
                StripData.AutoStartupArmed => new TagText("S", DarkGreen),
                StripData.AutoStartupDone => new TagText("S", Grey),
                _ => new TagText(string.Empty),
            },
            [CdmItem.EventSlot] = new TagText(extras.EventSlot, Grey),
            [CdmItem.Time] = time.Text.Length == 0 ? time : time with { Text = $"{label} {time.Text}" },
            [CdmItem.TimeShort] = time.Text.Length == 0 ? time : time with { Text = letter + time.Text },
            [CdmItem.Status] = Status(flight, isCdm, started, suspended, state, etfms),
        };

        string stage = Stage(flight, isCdm, started, suspended, state, etfms, tsat, now);
        return new FlightTags(items, isCdm, record.Cdm.Asrt.Length > 0, ctot.Length > 0, record.Informed, suspended, state, etfms, stage);
    }

    /// <summary>
    /// Gets the one time that matters now, with what it is: the TOBT (EOBT while there is none) until a TSAT is
    /// calculated, then the TSAT until start-up, then the TTOT. A suspended flight shows its TOBT, which must change.
    /// </summary>
    /// <returns>The label (EOBT, TOBT, TSAT, TTOT), its one-letter form (E, B, S, T) and the time as its own item shows it.</returns>
    private static (string Label, string Letter, TagText Time) CurrentTime(
        DepartureRecord record,
        bool isCdm,
        bool started,
        TsatState state,
        TagText tobt,
        TagText tsat,
        DateTimeOffset? ttot)
    {
        (string Label, string Letter) offBlock = isCdm && record.Cdm.Tobt.Length > 0 ? ("TOBT", "B") : ("EOBT", "E");
        if (!isCdm)
        {
            return (offBlock.Label, offBlock.Letter, tobt);
        }

        if (started)
        {
            return ("TTOT", "T", new TagText(ttot is null ? string.Empty : CdmTime.ToHhmm(ttot.Value)));
        }

        return state is TsatState.Pending or TsatState.Suspended
            ? (offBlock.Label, offBlock.Letter, tobt)
            : ("TSAT", "S", tsat);
    }

    // The one status that matters most: suspended, expired, REA, then ready.
    private static TagText Status(FlightView flight, bool isCdm, bool started, bool suspended, TsatState state, string etfms)
    {
        if (!flight.Watched || !isCdm)
        {
            return new TagText(string.Empty);
        }

        if (suspended)
        {
            return new TagText("SUSP", Red);
        }

        if (started)
        {
            return new TagText(string.Empty);
        }

        if (state == TsatState.Expired)
        {
            return new TagText("EXP", Red);
        }

        if (etfms == "REA")
        {
            return new TagText("REA", Yellow);
        }

        return flight.Record.Cdm.Asrt.Length > 0 ? new TagText("RDY", DarkGreen) : new TagText(string.Empty);
    }

    private static string Stage(FlightView flight, bool isCdm, bool started, bool suspended, TsatState state, string etfms, DateTimeOffset? tsat, DateTimeOffset now)
    {
        DepartureRecord record = flight.Record;
        if (!flight.Watched || !isCdm)
        {
            return string.Empty;
        }

        if (started)
        {
            return CdmStage.Started;
        }

        if (suspended || etfms == "REA" || record.Cdm.Asrt.Length > 0 || state is TsatState.Window or TsatState.Closing or TsatState.Expired)
        {
            return CdmStage.Now;
        }

        DateTimeOffset? due = tsat ?? CdmTime.ParseNear(record.Cdm.Tobt.Length > 0 ? record.Cdm.Tobt : record.Eobt, now);
        return due is not null && due.Value - now <= CdmStage.Lead ? CdmStage.Now : CdmStage.Later;
    }

    /// <summary>
    /// Numbers each airport's departures in take-off order, by TTOT, from 01: started flights included, suspended and
    /// airborne ones left out. Two digits, so a list sorted by the column sorts by take-off order.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Sequence(IReadOnlyList<FlightView> flights, DateTimeOffset now)
    {
        Dictionary<string, string> numbers = new(StringComparer.OrdinalIgnoreCase);
        IEnumerable<IGrouping<string, (FlightView Flight, DateTimeOffset Ttot)>> airports = flights
            .Where(f => f.Watched && f.Airport.IsCdm && !f.Record.IsAirborne && f.Record.Suspension.Length == 0)
            .Select(f => (Flight: f, Ttot: CdmTime.ParseNear(f.Record.Cdm.Ttot, now)))
            .Where(f => f.Ttot is not null)
            .Select(f => (f.Flight, Ttot: f.Ttot!.Value))
            .GroupBy(f => f.Flight.Record.Departure, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, (FlightView Flight, DateTimeOffset Ttot)> airport in airports)
        {
            int number = 0;
            foreach ((FlightView flight, _) in airport.OrderBy(f => f.Ttot).ThenBy(f => f.Flight.Record.Callsign, StringComparer.Ordinal))
            {
                number++;
                numbers[flight.Record.Callsign] = number.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return numbers;
    }

    public static TsatState StateOf(bool isCdm, bool started, DateTimeOffset? tsat, DateTimeOffset now)
    {
        if (!isCdm || started)
        {
            return TsatState.None;
        }

        if (tsat is null)
        {
            return TsatState.Pending;
        }

        TimeSpan sinceTsat = now - tsat.Value;
        if (sinceTsat < -s_windowHalf)
        {
            return TsatState.Future;
        }

        if (sinceTsat <= s_windowHalf)
        {
            return TsatState.Window;
        }

        return sinceTsat <= MasterSequencing.TsatExpiry ? TsatState.Closing : TsatState.Expired;
    }

    /// <summary>
    /// Gets the ETFMS status to show: a suspension first, then REA, then the ATFCM message.
    /// </summary>
    public static string Etfms(DepartureRecord record)
    {
        if (record.Suspension.Length > 0)
        {
            return record.Suspension;
        }

        string status = record.AtfcmStatus.ToUpperInvariant();
        if (record.IsRea || string.Equals(record.CdmStatus, "REA", StringComparison.OrdinalIgnoreCase))
        {
            return "REA";
        }

        return status == "ATC_ACTIV" ? "ACT" : status;
    }

    /// <summary>
    /// Gets the TSAC: green within 5 minutes of the TSAT, orange further, "...." while it waits for the TSAT.
    /// </summary>
    public static TagText Tsac(string tsac, DateTimeOffset? tsat, DateTimeOffset now)
    {
        if (tsac == StripData.TsacPending)
        {
            return new TagText("....", Grey);
        }

        DateTimeOffset? communicated = CdmTime.ParseNear(tsac, now);
        if (communicated is null)
        {
            return new TagText(string.Empty);
        }

        bool close = tsat is not null && (tsat.Value - communicated.Value).Duration() <= s_windowHalf;
        return new TagText(CdmTime.ToHhmm(communicated.Value), close ? DarkGreen : Orange);
    }

    /// <summary>
    /// Gets the CTOC and how the CTOT moved since: grey within 5 minutes, red when it is later, green when earlier,
    /// and an orange X when the CTOT was cancelled.
    /// </summary>
    public static TagText Ctoc(string ctoc, string ctot, DateTimeOffset now)
    {
        DateTimeOffset? communicated = CdmTime.ParseNear(ctoc, now);
        if (communicated is null)
        {
            return new TagText(string.Empty);
        }

        DateTimeOffset? current = CdmTime.ParseNear(ctot, now);
        if (current is null)
        {
            return new TagText("X", Orange);
        }

        int moved = (int)Math.Round((current.Value - communicated.Value).TotalMinutes);
        string text = CdmTime.ToHhmm(communicated.Value) + (moved == 0 ? string.Empty : moved > 0 ? $"+{moved}" : $"{moved}");
        return new TagText(text, moved > 5 ? Red : moved < -5 ? DarkGreen : Grey);
    }

    private static TagText Phase(DepartureRecord record, bool isCdm, bool started, TsatState state, DateTimeOffset now)
    {
        if (!isCdm || started)
        {
            return new TagText(string.Empty);
        }

        if (state is TsatState.Expired or TsatState.Suspended)
        {
            return new TagText("I", Red);
        }

        DateTimeOffset? tobt = CdmTime.ParseNear(record.Cdm.Tobt.Length > 0 ? record.Cdm.Tobt : record.Eobt, now);
        return tobt is not null && tobt.Value > now + MasterSequencing.TsatHorizon ? new TagText("P", DarkGreen) : new TagText("C", DarkGreen);
    }

    private static TagText Delay(DepartureRecord record, DateTimeOffset? tsat, DateTimeOffset now)
    {
        DateTimeOffset? tobt = CdmTime.ParseNear(record.Cdm.Tobt, now);
        if (tsat is null || tobt is null)
        {
            return new TagText(string.Empty);
        }

        int minutes = (int)Math.Round((tsat.Value - tobt.Value).TotalMinutes);
        return new TagText(minutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Gets the airborne compliance letter: C (complied), A (connected airborne), empty otherwise.
    /// </summary>
    public static TagText Compliance(string status) => status switch
    {
        "COMPLY" => new TagText("C", DarkGreen),
        "AIRB" => new TagText("A", Red),
        _ => new TagText(string.Empty),
    };

    /// <summary>
    /// Gets the airborne on-time deviation: "+12" late in red, "-3" early in green, empty when on time or unknown.
    /// </summary>
    public static TagText OnTime(int? minutes) => minutes switch
    {
        null or 0 => new TagText(string.Empty),
        > 0 => new TagText($"+{minutes}", Red),
        _ => new TagText($"{minutes}", DarkGreen),
    };

    private static TagText Tobt(DepartureRecord record, bool isCdm, DateTimeOffset now)
    {
        if (!isCdm || record.Cdm.Tobt.Length == 0)
        {
            return new TagText(CdmTime.Display(record.Eobt));
        }

        DateTimeOffset? tobt = CdmTime.ParseNear(record.Cdm.Tobt, now);
        return new TagText(CdmTime.Display(record.Cdm.Tobt), tobt is not null && now < tobt.Value - s_windowHalf ? LightGreen : DarkGreen);
    }

    private static TagText Tsat(TsatState state, DateTimeOffset? tsat, bool flashOn) => state switch
    {
        TsatState.None => new TagText(tsat is null ? string.Empty : CdmTime.ToHhmm(tsat.Value)),
        TsatState.Pending => new TagText("~", Grey),
        TsatState.Future => new TagText(CdmTime.ToHhmm(tsat!.Value), LightGreen),
        TsatState.Window => new TagText(CdmTime.ToHhmm(tsat!.Value), DarkGreen),
        TsatState.Closing => new TagText(CdmTime.ToHhmm(tsat!.Value), flashOn ? Yellow : Grey),
        TsatState.Suspended => new TagText(tsat is null ? "~" : CdmTime.ToHhmm(tsat.Value), Red),
        _ => new TagText(CdmTime.ToHhmm(tsat!.Value), Red),
    };

    private static TagText Ttg(TsatState state, DateTimeOffset? tsat, DateTimeOffset now)
    {
        if (tsat is null || state is TsatState.None or TsatState.Pending or TsatState.Suspended)
        {
            return new TagText(string.Empty);
        }

        int minutes = (int)Math.Floor((tsat.Value - now).TotalMinutes);
        (byte, byte, byte) colour = state switch
        {
            TsatState.Future => LightGreen,
            TsatState.Window => DarkGreen,
            TsatState.Closing => Yellow,
            _ => Red,
        };
        return new TagText(minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), colour);
    }

    private static TagText Ctot(DepartureRecord record, string ctot, DateTimeOffset? ttot, DateTimeOffset now)
    {
        if (ctot.Length == 0)
        {
            return new TagText(string.Empty);
        }

        DateTimeOffset? ctotTime = CdmTime.ParseNear(ctot, now);
        bool missed = ctotTime is not null &&
                      ((ttot is not null && ttot.Value > ctotTime.Value + Sequencer.CtotLateTolerance) ||
                       now > ctotTime.Value + Sequencer.CtotLateTolerance);
        return new TagText(ctot, missed ? Red : record.HasManualCtot ? Orange : DarkGreen);
    }

    private static (byte, byte, byte)? EtfmsColour(string status)
    {
        if (status.StartsWith("FLS", StringComparison.Ordinal))
        {
            return Red;
        }

        return status switch
        {
            "REA" => Yellow,
            "SAM" or "SRM" => Orange,
            "DES" => DarkGreen,
            "" => null,
            _ => Grey,
        };
    }
}
