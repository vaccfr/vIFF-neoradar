using System;
using System.Collections.Generic;
using System.Linq;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Config;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Represents one flight offered to the sequencer.
/// </summary>
/// <param name="Callsign">The flight's callsign.</param>
/// <param name="Runway">The departure runway, or empty when none is known, which queues it with every runway.</param>
/// <param name="Tobt">The TOBT, or the EOBT when no TOBT is set.</param>
/// <param name="Exot">The taxi-out time, de-icing included.</param>
/// <param name="Ctot">The CTOT the take-off must meet, or null for none.</param>
/// <param name="Started">True once start-up was approved, so the flight keeps its times.</param>
/// <param name="CurrentTsat">The TSAT the flight holds, or null.</param>
/// <param name="CurrentTtot">The TTOT the flight holds, or null.</param>
/// <param name="Priority">True for a flight placed before the others: confirmed TOBT, reported ready or an event slot.</param>
internal sealed record SequenceInput(
    string Callsign,
    string Runway,
    DateTimeOffset Tobt,
    TimeSpan Exot,
    DateTimeOffset? Ctot,
    bool Started,
    DateTimeOffset? CurrentTsat,
    DateTimeOffset? CurrentTtot,
    bool Priority)
{
    /// <summary>Gets the SID, for SID intervals and SID dependencies.</summary>
    public string Sid { get; init; } = string.Empty;

    /// <summary>Gets the departures per hour of the flight's runway, or 0 for the rules' default.</summary>
    public int RatePerHour { get; init; }

    /// <summary>Gets the runways and SID patterns sharing the runway's queue, from rate.txt.</summary>
    public IReadOnlyList<string> Dependent { get; init; } = Array.Empty<string>();

    /// <summary>Gets the runway pressure in minutes, or 0 or less for none.</summary>
    public int PressureMinutes { get; init; }
}

/// <summary>
/// Represents what governs a sequence beyond the flights: delays per runway, BMI blocks and SID intervals.
/// </summary>
internal sealed record SequenceRules
{
    private static readonly IReadOnlyDictionary<string, DateTimeOffset> s_none = new Dictionary<string, DateTimeOffset>();

    /// <summary>Gets the departures per hour of a flight whose own rate is 0.</summary>
    public int DefaultRate { get; init; }

    public IReadOnlyDictionary<string, DateTimeOffset> EarliestTsatByRunway { get; init; } = s_none;

    public IReadOnlyDictionary<string, DateTimeOffset> EarliestTtotByRunway { get; init; } = s_none;

    /// <summary>Gets a value indicating whether take-offs fill 10-minute blocks instead of being spaced by the rate.</summary>
    public bool Bmi { get; init; }

    /// <summary>Gets the minimum interval between the flight being placed and another, from their SIDs, or null for none.</summary>
    public Func<SequenceInput, SequenceInput, TimeSpan>? SidInterval { get; init; }
}

/// <summary>
/// Represents the times the sequencer gave one flight.
/// </summary>
/// <param name="CtotMissed">True when the earliest possible take-off is after CTOT +10, so a revised CTOT is needed.</param>
internal sealed record SequenceSlot(string Callsign, DateTimeOffset Tsat, DateTimeOffset Ttot, bool CtotMissed);

/// <summary>
/// Builds an airport's departure sequence as the CDM plugin does: each runway at its own rate, spaced only against the
/// same or dependent runways and SIDs, SID intervals on top, CTOTs pinned, started flights frozen.
/// </summary>
internal static class Sequencer
{
    /// <summary>
    /// Gets the latest take-off a CTOT allows.
    /// </summary>
    public static readonly TimeSpan CtotLateTolerance = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan s_block = TimeSpan.FromMinutes(10);
    private const int MaxMoves = 5000;

    /// <summary>
    /// Sequences flights on one rate with no rules, every flight in one queue unless runways differ.
    /// </summary>
    public static IReadOnlyList<SequenceSlot> Build(
        IReadOnlyList<SequenceInput> flights,
        int ratePerHour,
        IReadOnlyDictionary<string, DateTimeOffset> earliestTsatByRunway,
        IReadOnlyDictionary<string, DateTimeOffset> earliestTtotByRunway,
        DateTimeOffset now) =>
        Build(flights, new SequenceRules
        {
            DefaultRate = ratePerHour,
            EarliestTsatByRunway = earliestTsatByRunway,
            EarliestTtotByRunway = earliestTtotByRunway,
        }, now);

    /// <summary>
    /// Sequences the flights and returns a slot for each one placed, in TTOT order.
    /// </summary>
    public static IReadOnlyList<SequenceSlot> Build(IReadOnlyList<SequenceInput> flights, SequenceRules rules, DateTimeOffset now)
    {
        if (rules.DefaultRate <= 0 && flights.Any(f => !f.Started && f.RatePerHour <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(rules), "Every flight needs a departure rate above 0.");
        }

        List<(SequenceInput Flight, DateTimeOffset Ttot)> placed = new();
        List<SequenceSlot> slots = new();
        Dictionary<string, DateTimeOffset> startUps = new(StringComparer.OrdinalIgnoreCase);

        foreach (SequenceInput flight in flights.Where(f => f.Started && f.CurrentTtot is not null))
        {
            DateTimeOffset ttot = flight.CurrentTtot!.Value;
            placed.Add((flight, ttot));
            slots.Add(new SequenceSlot(flight.Callsign, flight.CurrentTsat ?? ttot - flight.Exot, ttot, CtotMissed: false));
        }

        List<(SequenceInput Flight, DateTimeOffset EarliestTtot)> waiting = new();
        foreach (SequenceInput flight in flights.Where(f => !f.Started))
        {
            DateTimeOffset startUp = EarliestStartUp(flight, rules, now);
            startUps[flight.Callsign] = startUp;
            waiting.Add((flight, EarliestTtot(flight, startUp, rules)));
        }

        foreach ((SequenceInput flight, DateTimeOffset earliest) in waiting.Where(w => w.Flight.Ctot is not null))
        {
            DateTimeOffset ctot = flight.Ctot!.Value;
            DateTimeOffset ttot = earliest > ctot ? earliest : ctot;
            placed.Add((flight, ttot));
            slots.Add(new SequenceSlot(flight.Callsign, ttot - flight.Exot, ttot, ttot > ctot + CtotLateTolerance));
        }

        IEnumerable<(SequenceInput Flight, DateTimeOffset EarliestTtot)> queue = waiting
            .Where(w => w.Flight.Ctot is null)
            .OrderByDescending(w => w.Flight.Priority)
            .ThenBy(w => w.EarliestTtot)
            .ThenBy(w => w.Flight.Callsign, StringComparer.Ordinal);

        foreach ((SequenceInput flight, DateTimeOffset earliest) in queue)
        {
            DateTimeOffset ttot = rules.Bmi ? PlaceInBlock(flight, earliest, placed, rules) : PlaceSpaced(flight, earliest, placed, rules);
            placed.Add((flight, ttot));
            slots.Add(new SequenceSlot(flight.Callsign, ttot - flight.Exot, ttot, CtotMissed: false));
        }

        slots = ApplyRunwayPressure(slots, flights, startUps, rules);
        return slots.OrderBy(s => s.Ttot).ThenBy(s => s.Callsign, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Returns whether two flights share a runway queue: the same runway, an unknown runway, or a runway or SID the
    /// placed flight's rate.txt line lists as dependent.
    /// </summary>
    public static bool SameQueue(SequenceInput flight, SequenceInput other)
    {
        if (flight.Runway.Length == 0 || other.Runway.Length == 0 ||
            string.Equals(flight.Runway, other.Runway, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return flight.Dependent.Any(d => RateTable.PatternMatches(d, other.Runway) || RateTable.PatternMatches(d, other.Sid));
    }

    /// <summary>
    /// Gets how many take-offs a 10-minute block of the hour holds at a rate: the rate spread over six blocks with the
    /// remainder evenly placed, as the CDM plugin's BMI (40/h gives 7, 7, 6, 7, 7, 6).
    /// </summary>
    public static int BlockCapacity(int ratePerHour, int blockIndex)
    {
        int baseCapacity = ratePerHour / 6;
        int remainder = ratePerHour % 6;
        if (remainder > 0)
        {
            double spacing = 6.0 / remainder;
            for (int i = 0; i < remainder; i++)
            {
                if (blockIndex == (int)(i * spacing))
                {
                    return baseCapacity + 1;
                }
            }
        }

        return baseCapacity;
    }

    private static int RateOf(SequenceInput flight, SequenceRules rules) => flight.RatePerHour > 0 ? flight.RatePerHour : rules.DefaultRate;

    private static DateTimeOffset EarliestStartUp(SequenceInput flight, SequenceRules rules, DateTimeOffset now)
    {
        DateTimeOffset startUp = CdmTime.CeilToMinute(flight.Tobt > now ? flight.Tobt : now);
        return rules.EarliestTsatByRunway.TryGetValue(flight.Runway, out DateTimeOffset earliestTsat) && earliestTsat > startUp ? earliestTsat : startUp;
    }

    private static DateTimeOffset EarliestTtot(SequenceInput flight, DateTimeOffset startUp, SequenceRules rules)
    {
        DateTimeOffset takeOff = startUp + flight.Exot;
        return rules.EarliestTtotByRunway.TryGetValue(flight.Runway, out DateTimeOffset earliestTtot) && earliestTtot > takeOff ? earliestTtot : takeOff;
    }

    private static TimeSpan SidInterval(SequenceInput flight, SequenceInput other, SequenceRules rules) =>
        rules.SidInterval?.Invoke(flight, other) ?? TimeSpan.Zero;

    private static DateTimeOffset PlaceSpaced(SequenceInput flight, DateTimeOffset earliest, List<(SequenceInput Flight, DateTimeOffset Ttot)> placed, SequenceRules rules)
    {
        TimeSpan rateSpacing = TimeSpan.FromSeconds(3600.0 / RateOf(flight, rules));
        DateTimeOffset candidate = earliest;
        for (int moves = 0; moves < MaxMoves; moves++)
        {
            bool moved = false;
            foreach ((SequenceInput other, DateTimeOffset taken) in placed)
            {
                TimeSpan separation = SameQueue(flight, other) ? rateSpacing : TimeSpan.Zero;
                TimeSpan interval = SidInterval(flight, other, rules);
                if (interval > separation)
                {
                    separation = interval;
                }

                if (separation > TimeSpan.Zero && (candidate - taken).Duration() < separation)
                {
                    candidate = taken + separation;
                    moved = true;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        return candidate;
    }

    private static DateTimeOffset PlaceInBlock(SequenceInput flight, DateTimeOffset earliest, List<(SequenceInput Flight, DateTimeOffset Ttot)> placed, SequenceRules rules)
    {
        int rate = RateOf(flight, rules);
        DateTimeOffset candidate = earliest;
        for (int moves = 0; moves < MaxMoves; moves++)
        {
            DateTimeOffset blockStart = new(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, candidate.Minute / 10 * 10, 0, candidate.Offset);
            int capacity = BlockCapacity(rate, candidate.Minute / 10);
            int taken = placed.Count(p => SameQueue(flight, p.Flight) && p.Ttot >= blockStart && p.Ttot < blockStart + s_block);
            if (taken >= capacity)
            {
                candidate = blockStart + s_block;
                continue;
            }

            bool moved = false;
            foreach ((SequenceInput other, DateTimeOffset otherTtot) in placed)
            {
                TimeSpan interval = SidInterval(flight, other, rules);
                if (interval > TimeSpan.Zero && (candidate - otherTtot).Duration() < interval)
                {
                    candidate = otherTtot + interval;
                    moved = true;
                    break;
                }
            }

            if (!moved)
            {
                return candidate;
            }
        }

        return candidate;
    }

    // Runway pressure lets a flight start up to that many minutes before its rate-based TSAT and wait in the runway
    // queue instead; the TTOT does not change. A CTOT flight advances by the average planned wait of the non-CTOT
    // departures of its queue in the pressure window before its TTOT, capped at the pressure.
    private static List<SequenceSlot> ApplyRunwayPressure(
        List<SequenceSlot> slots,
        IReadOnlyList<SequenceInput> flights,
        Dictionary<string, DateTimeOffset> startUps,
        SequenceRules rules)
    {
        Dictionary<string, SequenceInput> byCallsign = flights.ToDictionary(f => f.Callsign, StringComparer.OrdinalIgnoreCase);
        if (!flights.Any(f => f.PressureMinutes > 0))
        {
            return slots;
        }

        List<SequenceSlot> adjusted = new();
        foreach (SequenceSlot slot in slots)
        {
            SequenceInput flight = byCallsign[slot.Callsign];
            if (flight.Started || flight.PressureMinutes <= 0 || flight.Ctot is not null || !startUps.TryGetValue(slot.Callsign, out DateTimeOffset startUp))
            {
                adjusted.Add(slot);
                continue;
            }

            TimeSpan pressure = TimeSpan.FromMinutes(flight.PressureMinutes);
            TimeSpan delay = slot.Tsat - startUp;
            adjusted.Add(slot with { Tsat = delay > pressure ? startUp + (delay - pressure) : startUp });
        }

        List<SequenceSlot> final = new();
        foreach (SequenceSlot slot in adjusted)
        {
            SequenceInput flight = byCallsign[slot.Callsign];
            if (flight.Started || flight.PressureMinutes <= 0 || flight.Ctot is null || !startUps.TryGetValue(slot.Callsign, out DateTimeOffset startUp))
            {
                final.Add(slot);
                continue;
            }

            TimeSpan pressure = TimeSpan.FromMinutes(flight.PressureMinutes);
            List<double> waits = adjusted
                .Where(o => o.Callsign != slot.Callsign && o.Ttot >= slot.Ttot - pressure && o.Ttot <= slot.Ttot)
                .Select(o => (Slot: o, Flight: byCallsign[o.Callsign]))
                .Where(o => o.Flight.Ctot is null && !o.Flight.Started && SameQueue(flight, o.Flight))
                .Select(o => Math.Max(0, (o.Slot.Ttot - o.Slot.Tsat - o.Flight.Exot).TotalMinutes))
                .ToList();
            TimeSpan advance = waits.Count == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(Math.Min(pressure.TotalMinutes, waits.Average()));
            DateTimeOffset tsat = slot.Ttot - flight.Exot - advance;
            final.Add(slot with { Tsat = tsat > startUp ? tsat : startUp });
        }

        return final;
    }
}
