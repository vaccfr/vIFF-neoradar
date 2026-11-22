using System;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Tests;

internal static class TestData
{
    /// <summary>12:00:00Z on a fixed day.</summary>
    public static readonly DateTimeOffset Noon = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public static DepartureRecord Departure(string callsign, string eobt, string airport = "LFPG", int taxi = 10) => new()
    {
        Callsign = callsign,
        Departure = airport,
        Arrival = "EGLL",
        Eobt = eobt,
        Tobt = eobt,
        TaxiMinutes = taxi,
    };

    public static DepartureRecord WithCdm(this DepartureRecord record, Func<CdmData, CdmData> change) =>
        record with { Cdm = change(record.Cdm) };
}
