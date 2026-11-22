using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Viff.NeoRadar.Config;

/// <summary>
/// Represents one sidInterval.txt rule: the minimum minutes between two take-offs on SIDs to these points.
/// </summary>
/// <param name="Runway">The runway of the flight being sequenced.</param>
/// <param name="OtherRunway">The runway of the other flight, or empty for any.</param>
internal sealed record SidInterval(string Airport, string Runway, string Sid, string OtherRunway, string OtherSid, double Minutes);

/// <summary>
/// Holds a vACC's sidInterval.txt and finds the interval between two departures, as the CDM plugin does.
/// </summary>
/// <remarks>
/// Lines read <c>ICAO,RWY,SID1,SID2,MINUTES</c> or <c>ICAO,RWY1,SID1,RWY2,SID2,MINUTES</c>, SIDs given by their point
/// (LARPA for LARPA4Q). A rule matches the SID pair in either order.
/// </remarks>
internal sealed class SidIntervals
{
    private readonly ILookup<string, SidInterval> _byAirportRunway;

    public SidIntervals(IReadOnlyList<SidInterval> rules)
    {
        Count = rules.Count;
        _byAirportRunway = rules.ToLookup(r => Key(r.Airport, r.Runway), StringComparer.OrdinalIgnoreCase);
    }

    public static SidIntervals Empty { get; } = new(Array.Empty<SidInterval>());

    public int Count { get; }

    public static SidIntervals Parse(string text, List<string> warnings)
    {
        List<SidInterval> rules = new();
        string[] rows = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < rows.Length; i++)
        {
            string row = rows[i].Trim();
            if (row.Length == 0 || row.StartsWith('#'))
            {
                continue;
            }

            string[] parts = row.Split(',', StringSplitOptions.TrimEntries);
            SidInterval? rule = parts.Length switch
            {
                5 when Minutes(parts[4]) is { } minutes => new SidInterval(parts[0], parts[1], parts[2], string.Empty, parts[3], minutes),
                6 when Minutes(parts[5]) is { } minutes => new SidInterval(parts[0], parts[1], parts[2], parts[3], parts[4], minutes),
                _ => null,
            };
            if (rule is null)
            {
                warnings.Add($"sidInterval.txt line {i + 1} could not be read and was skipped: {row}");
                continue;
            }

            rules.Add(rule with
            {
                Airport = rule.Airport.ToUpperInvariant(),
                Runway = rule.Runway.ToUpperInvariant(),
                Sid = rule.Sid.ToUpperInvariant(),
                OtherRunway = rule.OtherRunway.ToUpperInvariant(),
                OtherSid = rule.OtherSid.ToUpperInvariant(),
            });
        }

        return new SidIntervals(rules);
    }

    /// <summary>
    /// Gets the SID point of a SID, its name without the last two characters (LARPA for LARPA4Q), or empty.
    /// </summary>
    public static string PointOf(string sid) => sid.Length > 3 ? sid[..^2].ToUpperInvariant() : string.Empty;

    /// <summary>
    /// Gets the minimum interval between a flight being sequenced and another, or zero when no rule applies.
    /// </summary>
    public TimeSpan Between(string airport, string runway, string sid, string otherRunway, string otherSid)
    {
        string point = PointOf(sid);
        string otherPoint = PointOf(otherSid);
        if (point.Length == 0 || otherPoint.Length == 0)
        {
            return TimeSpan.Zero;
        }

        foreach (SidInterval rule in _byAirportRunway[Key(airport, runway)])
        {
            bool sidsMatch = (rule.Sid == point && rule.OtherSid == otherPoint) || (rule.Sid == otherPoint && rule.OtherSid == point);
            bool otherRunwayMatches = rule.OtherRunway.Length == 0 || string.Equals(rule.OtherRunway, otherRunway, StringComparison.OrdinalIgnoreCase);
            if (sidsMatch && otherRunwayMatches)
            {
                return TimeSpan.FromMinutes(rule.Minutes);
            }
        }

        return TimeSpan.Zero;
    }

    private static double? Minutes(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) && minutes > 0 ? minutes : null;

    private static string Key(string airport, string runway) => airport.ToUpperInvariant() + "/" + runway.ToUpperInvariant();
}
