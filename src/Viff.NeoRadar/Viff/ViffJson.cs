using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Viff.NeoRadar.Viff;

/// <summary>
/// Parses vIFF responses, reading every field leniently so a missing or retyped field never fails a whole response.
/// </summary>
internal static class ViffJson
{
    /// <summary>
    /// Parses a <c>/ifps/depAirport</c> response, or returns null when it is not a JSON array.
    /// </summary>
    public static IReadOnlyList<DepartureRecord>? ParseDepartures(string json)
    {
        using JsonDocument? document = TryParse(json);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<DepartureRecord> records = new();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string callsign = Text(item, "callsign");
            if (callsign.Length == 0)
            {
                continue;
            }

            JsonElement atfcm = Child(item, "atfcmData");
            JsonElement cdm = Child(item, "cdmData");
            string regulation = Text(atfcm, "mostPenalisingRegulation");
            records.Add(new DepartureRecord
            {
                Callsign = callsign,
                Departure = Text(item, "departure"),
                Arrival = Text(item, "arrival"),
                Eobt = Text(item, "eobt"),
                Tobt = Text(item, "tobt"),
                TaxiMinutes = Number(item, "taxi"),
                Ctot = Text(item, "ctot"),
                Aobt = Text(item, "aobt"),
                Atot = Text(item, "atot"),
                CdmStatus = Text(item, "cdmSts"),
                AtfcmStatus = Text(item, "atfcmStatus"),
                Regulation = regulation.Length > 0 ? regulation : Text(item, "mostPenalizingAirspace"),
                IsRea = Flag(atfcm, "isRea"),
                Informed = Flag(item, "informed") || Flag(atfcm, "informed"),
                Cdm = new CdmData
                {
                    Tobt = Text(cdm, "tobt"),
                    Tsat = Text(cdm, "tsat"),
                    Ttot = Text(cdm, "ttot"),
                    Ctot = Text(cdm, "ctot"),
                    Reason = Text(cdm, "reason"),
                    Asrt = Text(cdm, "asrt"),
                    DepInfo = Text(cdm, "depInfo"),
                    Deice = Text(cdm, "deice"),
                    ReqAsrt = Text(cdm, "reqAsrt"),
                    ReqTobt = Text(cdm, "reqTobt"),
                    ReqTobtType = Text(cdm, "reqTobtType"),
                    Confirmed = Flag(cdm, "confirmed"),
                },
            });
        }

        return records;
    }

    /// <summary>
    /// Parses a <c>/etfms/getCadAirport</c> response, or returns null when it holds no airport.
    /// </summary>
    public static AirportCapacity? ParseCapacity(string json)
    {
        using JsonDocument? document = TryParse(json);
        if (document is null)
        {
            return null;
        }

        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
            {
                return null;
            }

            root = root[0];
        }

        string icao = Text(root, "icao");
        if (root.ValueKind != JsonValueKind.Object || icao.Length == 0)
        {
            return null;
        }

        return new AirportCapacity(icao, Number(root, "rate"), Number(root, "taxiTime"), Flag(root, "isCdm"));
    }

    /// <summary>
    /// Parses an <c>/airport</c> response, or returns null when it is not a JSON array.
    /// </summary>
    public static IReadOnlyList<MasterAirport>? ParseMasters(string json)
    {
        using JsonDocument? document = TryParse(json);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<MasterAirport> masters = new();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string icao = Text(item, "icao");
            string position = Text(item, "position");
            if (icao.Length > 0 && position.Length > 0)
            {
                masters.Add(new MasterAirport(icao, position));
            }
        }

        return masters;
    }

    /// <summary>
    /// Returns whether a write endpoint answered "true", which is how vIFF reports success.
    /// </summary>
    public static bool IsTrue(string body) =>
        body.Trim().StartsWith("true", StringComparison.OrdinalIgnoreCase);

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : default;

    private static string Text(JsonElement element, string name)
    {
        JsonElement value = Child(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static int Number(JsonElement element, string name)
    {
        JsonElement value = Child(element, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            return (int)Math.Round(number);
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }

    private static bool Flag(JsonElement element, string name)
    {
        JsonElement value = Child(element, name);
        return value.ValueKind == JsonValueKind.True ||
               (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase));
    }
}
