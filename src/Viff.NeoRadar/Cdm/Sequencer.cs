using System;
using System.Collections.Generic;
using System.Linq;
using Viff.NeoRadar.Common;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Represents one flight offered to the sequencer.
/// </summary>
/// <param name="Callsign">The flight's callsign.</param>
/// <param name="Runway">The departure runway, or empty when none is known.</param>
/// <param name="Tobt">The TOBT, or the EOBT when no TOBT is set.</param>
/// <param name="Exot">The taxi-out time, de-icing included.</param>
/// <param name="Ctot">The CTOT the take-off must meet, or null for none.</param>
/// <param name="Started">True once start-up was approved, so the flight keeps its times.</param>
/// <param name="CurrentTsat">The TSAT the flight holds, or null.</param>
/// <param name="CurrentTtot">The TTOT the flight holds, or null.</param>
/// <param name="Priority">True for a confirmed TOBT or a reported-ready flight, which is placed before unconfirmed ones.</param>
internal sealed record SequenceInput(
    string Callsign,
    string Runway,
    DateTimeOffset Tobt,
    TimeSpan Exot,
    DateTimeOffset? Ctot,
    bool Started,
    DateTimeOffset? CurrentTsat,
    DateTimeOffset? CurrentTtot,
    bool Priority);

/// <summary>
/// Represents the times the sequencer gave one flight.
/// </summary>
/// <param name="CtotMissed">True when the earliest possible take-off is after CTOT +10, so a revised CTOT is needed.</param>
internal sealed record SequenceSlot(string Callsign, DateTimeOffset Tsat, DateTimeOffset Ttot, bool CtotMissed);

/// <summary>
/// Builds an airport's departure sequence: one runway queue at the departure rate, CTOTs pinned, started flights frozen.
/// </summary>
internal static class Sequencer
{
    /// <summary>
    /// Gets the latest take-off a CTOT allows.
    /// </summary>
    public static readonly TimeSpan CtotLateTolerance = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Sequences the flights and returns a slot for each one placed, in TTOT order.
    /// </summary>
    /// <param name="flights">The flights to place.</param>
    /// <param name="ratePerHour">The departure rate, above 0.</param>
    /// <param name="earliestTsatByRunway">The earliest TSAT allowed per runway.</param>
    /// <param name="earliestTtotByRunway">The earliest TTOT allowed per runway.</param>
    /// <param name="now">The current time.</param>
    public static IReadOnlyList<SequenceSlot> Build(
        IReadOnlyList<SequenceInput> flights,
        int ratePerHour,
        IReadOnlyDictionary<string, DateTimeOffset> earliestTsatByRunway,
        IReadOnlyDictionary<string, DateTimeOffset> earliestTtotByRunway,
        DateTimeOffset now)
    {
        if (ratePerHour <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ratePerHour), "The departure rate must be above 0.");
        }

        TimeSpan spacing = TimeSpan.FromSeconds(3600.0 / ratePerHour);
        List<DateTimeOffset> occupied = new();
        List<SequenceSlot> slots = new();

        foreach (SequenceInput flight in flights.Where(f => f.Started && f.CurrentTtot is not null))
        {
            DateTimeOffset ttot = flight.CurrentTtot!.Value;
            occupied.Add(ttot);
            slots.Add(new SequenceSlot(flight.Callsign, flight.CurrentTsat ?? ttot - flight.Exot, ttot, CtotMissed: false));
        }

        List<(SequenceInput Flight, DateTimeOffset EarliestTtot)> waiting = flights
            .Where(f => !f.Started)
            .Select(f => (f, EarliestTtot(f, earliestTsatByRunway, earliestTtotByRunway, now)))
            .ToList();

        foreach ((SequenceInput flight, DateTimeOffset earliest) in waiting.Where(w => w.Flight.Ctot is not null))
        {
            DateTimeOffset ctot = flight.Ctot!.Value;
            DateTimeOffset ttot = earliest > ctot ? earliest : ctot;
            occupied.Add(ttot);
            slots.Add(new SequenceSlot(flight.Callsign, ttot - flight.Exot, ttot, ttot > ctot + CtotLateTolerance));
        }

        IEnumerable<(SequenceInput Flight, DateTimeOffset EarliestTtot)> queue = waiting
            .Where(w => w.Flight.Ctot is null)
            .OrderByDescending(w => w.Flight.Priority)
            .ThenBy(w => w.EarliestTtot)
            .ThenBy(w => w.Flight.Callsign, StringComparer.Ordinal);

        foreach ((SequenceInput flight, DateTimeOffset earliest) in queue)
        {
            DateTimeOffset ttot = FirstFreeSlot(earliest, occupied, spacing);
            occupied.Add(ttot);
            slots.Add(new SequenceSlot(flight.Callsign, ttot - flight.Exot, ttot, CtotMissed: false));
        }

        return slots.OrderBy(s => s.Ttot).ThenBy(s => s.Callsign, StringComparer.Ordinal).ToList();
    }

    private static DateTimeOffset EarliestTtot(
        SequenceInput flight,
        IReadOnlyDictionary<string, DateTimeOffset> earliestTsatByRunway,
        IReadOnlyDictionary<string, DateTimeOffset> earliestTtotByRunway,
        DateTimeOffset now)
    {
        DateTimeOffset startUp = CdmTime.CeilToMinute(flight.Tobt > now ? flight.Tobt : now);
        if (earliestTsatByRunway.TryGetValue(flight.Runway, out DateTimeOffset earliestTsat) && earliestTsat > startUp)
        {
            startUp = earliestTsat;
        }

        DateTimeOffset takeOff = startUp + flight.Exot;
        if (earliestTtotByRunway.TryGetValue(flight.Runway, out DateTimeOffset earliestTtot) && earliestTtot > takeOff)
        {
            takeOff = earliestTtot;
        }

        return takeOff;
    }

    private static DateTimeOffset FirstFreeSlot(DateTimeOffset earliest, List<DateTimeOffset> occupied, TimeSpan spacing)
    {
        DateTimeOffset candidate = earliest;
        bool moved = true;
        while (moved)
        {
            moved = false;
            foreach (DateTimeOffset taken in occupied)
            {
                if ((candidate - taken).Duration() < spacing)
                {
                    candidate = taken + spacing;
                    moved = true;
                }
            }
        }

        return candidate;
    }
}
