using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Viff.NeoRadar.Config;

/// <summary>
/// Represents one rate.txt line: <c>AIRPORT:A:ArrRwys:NotArrRwys:D:DepRwys:NotDepRwys:Dependent:Rate_Lvo[_Pressure],...</c>.
/// </summary>
internal sealed record RateLine(
    string Airport,
    IReadOnlyList<string> ArrivalRunways,
    IReadOnlyList<string> NotArrivalRunways,
    IReadOnlyList<string> DepartureRunways,
    IReadOnlyList<string> NotDepartureRunways,
    IReadOnlyList<string> Dependent,
    IReadOnlyList<(int Rate, int Lvo, int? Pressure)> Rates,
    int LineNumber);

/// <summary>
/// Represents the rate that applies to one departure runway, and what its take-offs are spaced against.
/// </summary>
/// <param name="Rate">Departures per hour.</param>
/// <param name="LvoRate">Departures per hour under LVO.</param>
/// <param name="Pressure">The runway pressure in minutes the line sets for the runway, or null to use the default.</param>
/// <param name="Dependent">Runways and SID patterns ('%' or '*' any one character) that share the runway's sequence.</param>
/// <param name="LineNumber">The rate.txt line it came from, for the log.</param>
internal sealed record RunwayRate(int Rate, int LvoRate, int? Pressure, IReadOnlyList<string> Dependent, int LineNumber);

/// <summary>
/// Holds a vACC's rate.txt and finds the rate of a departure as the CDM plugin does.
/// </summary>
internal sealed class RateTable
{
    private readonly IReadOnlyList<RateLine> _lines;

    public RateTable(IReadOnlyList<RateLine> lines)
    {
        _lines = lines;
    }

    public static RateTable Empty { get; } = new(Array.Empty<RateLine>());

    public int Count => _lines.Count;

    /// <summary>
    /// Gets the airports the table defines.
    /// </summary>
    public IReadOnlySet<string> Airports => _lines.Select(l => l.Airport).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses rate.txt, skipping comments and collecting a warning for each line it cannot read.
    /// </summary>
    public static RateTable Parse(string text, List<string> warnings)
    {
        List<RateLine> lines = new();
        string[] rows = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < rows.Length; i++)
        {
            string row = rows[i].Trim();
            if (row.Length == 0 || row.StartsWith('#'))
            {
                continue;
            }

            RateLine? line = ParseLine(row, i + 1);
            if (line is null)
            {
                warnings.Add($"rate.txt line {i + 1} is not AIRPORT:A:...:D:...:RATE_LVO and was skipped: {row}");
                continue;
            }

            lines.Add(line);
        }

        return new RateTable(lines);
    }

    /// <summary>
    /// Finds the rate of a departure from the first line that applies, as the CDM plugin's rateForRunway: the line names
    /// the runway (or '*'), one of its arrival runways is active (or '*'), and none of its excluded runways is active. A
    /// line that does not name the runway but lists the flight's SID as dependent lends its dependencies to the result.
    /// </summary>
    public RunwayRate? Find(string airport, string runway, string sid, IReadOnlyCollection<string> activeArrivals, IReadOnlyCollection<string> activeDepartures)
    {
        RateLine? lending = null;
        foreach (RateLine line in _lines.Where(l => string.Equals(l.Airport, airport, StringComparison.OrdinalIgnoreCase)))
        {
            bool namesRunway = line.DepartureRunways.Any(r => r == "*" || string.Equals(r, runway, StringComparison.OrdinalIgnoreCase));
            if (!namesRunway)
            {
                if (lending is null && sid.Length > 0 && line.Dependent.Any(p => PatternMatches(p, sid)))
                {
                    lending = line;
                }

                continue;
            }

            bool arrivalsMatch = line.ArrivalRunways.Any(r => r == "*" || activeArrivals.Contains(r, StringComparer.OrdinalIgnoreCase));
            bool noExcludedArrival = !line.NotArrivalRunways.Any(r => r != "*" && activeArrivals.Contains(r, StringComparer.OrdinalIgnoreCase));
            bool noExcludedDeparture = !line.NotDepartureRunways.Any(r => r != "*" && activeDepartures.Contains(r, StringComparer.OrdinalIgnoreCase));
            if (!arrivalsMatch || !noExcludedArrival || !noExcludedDeparture)
            {
                continue;
            }

            int position = 0;
            if (line.Rates.Count > 1)
            {
                int index = line.DepartureRunways.ToList().FindIndex(r => string.Equals(r, runway, StringComparison.OrdinalIgnoreCase));
                position = index >= 0 && index < line.Rates.Count ? index : 0;
            }

            (int rate, int lvo, int? pressure) = line.Rates[position];
            IReadOnlyList<string> dependent = lending?.Dependent ?? line.Dependent;
            return new RunwayRate(rate, lvo, pressure, dependent.Where(d => d != "*").ToList(), line.LineNumber);
        }

        return null;
    }

    /// <summary>
    /// Returns whether a dependency matches a runway or SID: equal, or a pattern of the same length where '%' or '*' match
    /// any one character, as the CDM plugin's patternMatches.
    /// </summary>
    public static bool PatternMatches(string pattern, string value)
    {
        if (value.Length == 0 || pattern.Length != value.Length)
        {
            return false;
        }

        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '%' && pattern[i] != '*' && char.ToUpperInvariant(pattern[i]) != char.ToUpperInvariant(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static RateLine? ParseLine(string row, int lineNumber)
    {
        string[] parts = row.Split(':');
        if (parts.Length != 9 || parts[0].Length != 4 ||
            !string.Equals(parts[1], "A", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parts[4], "D", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        List<(int, int, int?)> rates = new();
        foreach (string tuple in parts[8].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] values = tuple.Split('_');
            if (values.Length is < 2 or > 3 ||
                !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int rate) ||
                !int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out int lvo))
            {
                return null;
            }

            int? pressure = null;
            if (values.Length == 3)
            {
                if (!int.TryParse(values[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed))
                {
                    return null;
                }

                pressure = parsed;
            }

            rates.Add((rate, lvo, pressure));
        }

        if (rates.Count == 0)
        {
            return null;
        }

        static IReadOnlyList<string> List(string field) =>
            field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(f => f.ToUpperInvariant()).ToList();

        return new RateLine(parts[0].ToUpperInvariant(), List(parts[2]), List(parts[3]), List(parts[5]), List(parts[6]), List(parts[7]), rates, lineNumber);
    }
}
