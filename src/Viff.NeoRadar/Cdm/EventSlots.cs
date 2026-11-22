using System;
using System.Collections.Generic;
using System.Linq;
using Viff.NeoRadar.Common;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Represents one booked event slot, as the CDM plugin's slots file lists it.
/// </summary>
/// <param name="Cid">The pilot's network ID.</param>
/// <param name="Callsign">The callsign booked, or empty for any.</param>
/// <param name="Departure">The departure booked, or empty for any.</param>
/// <param name="Destination">The destination booked, or empty for any.</param>
/// <param name="Slot">The slot, "HHMM".</param>
internal sealed record EventSlot(string Cid, string Callsign, string Departure, string Destination, string Slot);

/// <summary>
/// Holds the event slots read from the slots file, and finds a flight's slot.
/// </summary>
/// <remarks>
/// Lines read <c>cid,slot</c>, <c>cid,callsign,slot</c> or <c>cid,callsign,departure,destination,slot</c>; '#' starts a
/// comment. A booking with a route also matches the same pilot under another callsign, as in the CDM plugin.
/// </remarks>
internal sealed class EventSlots
{
    // Guards _slots, swapped whole by the sync job and read by the tag and sequencing threads.
    private readonly object _lock = new();
    private IReadOnlyList<EventSlot> _slots = Array.Empty<EventSlot>();

    public int Count
    {
        get { lock (_lock) { return _slots.Count; } }
    }

    public static IReadOnlyList<EventSlot> Parse(string text)
    {
        List<EventSlot> slots = new();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split(',', StringSplitOptions.TrimEntries);
            EventSlot? slot = parts.Length switch
            {
                2 => new EventSlot(parts[0], string.Empty, string.Empty, string.Empty, parts[1]),
                3 => new EventSlot(parts[0], parts[1].ToUpperInvariant(), string.Empty, string.Empty, parts[2]),
                5 => new EventSlot(parts[0], parts[1].ToUpperInvariant(), parts[2].ToUpperInvariant(), parts[3].ToUpperInvariant(), parts[4]),
                _ => null,
            };
            if (slot is not null && slot.Cid.Length > 0 && slot.Slot.Length == 4 && CdmTime.ParseTimeOfDay(slot.Slot) is not null)
            {
                slots.Add(slot);
            }
        }

        return slots;
    }

    public void Replace(IReadOnlyList<EventSlot> slots)
    {
        lock (_lock)
        {
            _slots = slots;
        }
    }

    /// <summary>
    /// Finds the slot booked for a flight, "HHMM", or empty when it has none.
    /// </summary>
    public string Find(string cid, string callsign, string departure, string destination)
    {
        if (cid.Length == 0)
        {
            return string.Empty;
        }

        IReadOnlyList<EventSlot> slots;
        lock (_lock)
        {
            slots = _slots;
        }

        EventSlot? match = slots.FirstOrDefault(s => s.Cid == cid && Matches(s, callsign, departure, destination));
        return match?.Slot ?? string.Empty;
    }

    private static bool Matches(EventSlot slot, string callsign, string departure, string destination)
    {
        if (slot.Callsign.Length == 0 || string.Equals(slot.Callsign, callsign, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return slot.Departure.Length > 0 &&
               string.Equals(slot.Departure, departure, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(slot.Destination, destination, StringComparison.OrdinalIgnoreCase);
    }
}
