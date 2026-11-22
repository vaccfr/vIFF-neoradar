using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Viff.NeoRadar.Config;

/// <summary>
/// Represents one taxizones.txt line: a four-corner area of an airport and the taxi time from it to a runway.
/// </summary>
/// <param name="RemoteTaxiMinutes">The taxi minutes via each remote de-icing pad REM1 to REM5, when the line gives them.</param>
/// <param name="EventExtraMinutes">The extra minutes in event mode, or null.</param>
internal sealed record TaxiZone(
    string Airport,
    string Runway,
    IReadOnlyList<(double Latitude, double Longitude)> Corners,
    int TaxiMinutes,
    IReadOnlyList<int> RemoteTaxiMinutes,
    int? EventExtraMinutes,
    int LineNumber);

/// <summary>
/// Holds a vACC's taxizones.txt and finds the zone an aircraft stands in, as the CDM plugin does.
/// </summary>
/// <remarks>
/// Lines read <c>AIRPORT:RUNWAY:lat:lon:lat:lon:lat:lon:lat:lon:TAXI[:REM1,REM2,...[:EVENT]]</c>, with coordinates in
/// decimal degrees or EuroScope's N049.00.37.123 form.
/// </remarks>
internal sealed class TaxiZones
{
    private readonly IReadOnlyList<TaxiZone> _zones;

    public TaxiZones(IReadOnlyList<TaxiZone> zones)
    {
        _zones = zones;
    }

    public static TaxiZones Empty { get; } = new(Array.Empty<TaxiZone>());

    public int Count => _zones.Count;

    public bool HasAirport(string airport) => _zones.Any(z => string.Equals(z.Airport, airport, StringComparison.OrdinalIgnoreCase));

    public static TaxiZones Parse(string text, List<string> warnings)
    {
        List<TaxiZone> zones = new();
        string[] rows = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < rows.Length; i++)
        {
            string row = rows[i].Trim();
            if (row.Length == 0 || row.StartsWith('#'))
            {
                continue;
            }

            TaxiZone? zone = ParseLine(row, i + 1);
            if (zone is null)
            {
                warnings.Add($"taxizones.txt line {i + 1} could not be read and was skipped: {row}");
                continue;
            }

            zones.Add(zone);
        }

        return new TaxiZones(zones);
    }

    /// <summary>
    /// Finds the first zone of the airport and runway that contains the position, or null.
    /// </summary>
    public TaxiZone? Find(string airport, string runway, double latitude, double longitude) =>
        _zones.FirstOrDefault(z =>
            string.Equals(z.Airport, airport, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(z.Runway, runway, StringComparison.OrdinalIgnoreCase) &&
            Contains(z.Corners, latitude, longitude));

    /// <summary>
    /// Returns whether a point lies inside a polygon, by the even-odd ray casting the CDM plugin's inPoly uses.
    /// </summary>
    public static bool Contains(IReadOnlyList<(double Latitude, double Longitude)> corners, double latitude, double longitude)
    {
        bool inside = false;
        for (int i = 0, j = corners.Count - 1; i < corners.Count; j = i++)
        {
            (double latI, double lonI) = corners[i];
            (double latJ, double lonJ) = corners[j];
            if ((lonI > longitude) != (lonJ > longitude) &&
                latitude < (latJ - latI) * (longitude - lonI) / (lonJ - lonI) + latI)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Reads a coordinate in decimal degrees or EuroScope's form (N049.00.37.123, E002.32.53.000), or null.
    /// </summary>
    public static double? ReadCoordinate(string text)
    {
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return value;
        }

        if (text.Length < 2 || "NSEW".IndexOf(char.ToUpperInvariant(text[0])) < 0)
        {
            return null;
        }

        string[] parts = text[1..].Split('.');
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int degrees) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
            !double.TryParse($"{parts[2]}.{parts[3]}", NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return null;
        }

        double result = degrees + minutes / 60.0 + seconds / 3600.0;
        return char.ToUpperInvariant(text[0]) is 'S' or 'W' ? -result : result;
    }

    private static TaxiZone? ParseLine(string row, int lineNumber)
    {
        string[] parts = row.Split(':');
        if (parts.Length is < 11 or > 13 || parts[0].Length != 4 || parts[1].Length is < 2 or > 3)
        {
            return null;
        }

        List<(double, double)> corners = new();
        for (int i = 0; i < 4; i++)
        {
            double? latitude = ReadCoordinate(parts[2 + (i * 2)]);
            double? longitude = ReadCoordinate(parts[3 + (i * 2)]);
            if (latitude is null || longitude is null)
            {
                return null;
            }

            corners.Add((latitude.Value, longitude.Value));
        }

        if (!int.TryParse(parts[10], NumberStyles.None, CultureInfo.InvariantCulture, out int taxi))
        {
            return null;
        }

        List<int> remote = new();
        if (parts.Length >= 12 && parts[11].Trim().Length > 0)
        {
            foreach (string value in parts[11].Split(',', StringSplitOptions.TrimEntries))
            {
                remote.Add(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ? minutes : 0);
            }
        }

        int? eventExtra = parts.Length == 13 && int.TryParse(parts[12], NumberStyles.None, CultureInfo.InvariantCulture, out int extra) ? extra : null;
        return new TaxiZone(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant(), corners, taxi, remote, eventExtra, lineNumber);
    }
}
