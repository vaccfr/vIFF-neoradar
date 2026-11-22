using System.Collections.Generic;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Viff;

public sealed class ViffJsonTests
{
    // Trimmed from a live /ifps/depAirport response.
    private const string Departures = """
        [{"callsign":"SAS927","cid":"1683521","departure":"EKCH","arrival":"KBOS","eobt":"1325","tobt":"1324","obt":"1324",
          "reqTobt":"","taxi":12,"ctot":"","aobt":"1316","atot":"","eta":"2149","mostPenalizingAirspace":"","cdmSts":"COMPLY",
          "informed":true,"atfcmStatus":"ATC_ACTIV",
          "atfcmData":{"mostPenalisingRegulation":"","isRea":false,"informed":false},
          "cdmData":{"tobt":"131600","tsat":"132100","ttot":"133400","ctot":"","reason":"","asrt":"1318","depInfo":"04R/GOLGA3A",
                     "deice":"","reqAsrt":"","reqTobt":"1320","reqTobtType":"PILOT","confirmed":true}},
         {"callsign":"DLH6KM","departure":"EKCH","ctot":"1340","atfcmData":{"mostPenalisingRegulation":"LSGG","isRea":true}},
         {"departure":"EKCH"}, 42]
        """;

    [Fact]
    public void ParseDepartures_ReadsEveryField()
    {
        IReadOnlyList<DepartureRecord>? records = ViffJson.ParseDepartures(Departures);

        Assert.NotNull(records);
        Assert.Equal(2, records.Count);
        DepartureRecord sas = records[0];
        Assert.Equal(("SAS927", "EKCH", "1325", 12, "1316"), (sas.Callsign, sas.Departure, sas.Eobt, sas.TaxiMinutes, sas.Aobt));
        Assert.Equal(("ATC_ACTIV", "COMPLY", true), (sas.AtfcmStatus, sas.CdmStatus, sas.Informed));
        Assert.Equal(("131600", "132100", "133400", "1318", "04R/GOLGA3A"), (sas.Cdm.Tobt, sas.Cdm.Tsat, sas.Cdm.Ttot, sas.Cdm.Asrt, sas.Cdm.DepInfo));
        Assert.Equal(("1320", "PILOT", true), (sas.Cdm.ReqTobt, sas.Cdm.ReqTobtType, sas.Cdm.Confirmed));
    }

    [Fact]
    public void ParseDepartures_ToleratesMissingObjects()
    {
        DepartureRecord dlh = ViffJson.ParseDepartures(Departures)![1];

        Assert.Equal(("1340", "LSGG", true), (dlh.Ctot, dlh.Regulation, dlh.IsRea));
        Assert.Equal(string.Empty, dlh.Cdm.Tsat);
        Assert.Equal("1340", dlh.EffectiveCtot);
    }

    [Fact]
    public void ParseDepartures_RefusesWhatIsNotAnArray()
    {
        Assert.Null(ViffJson.ParseDepartures("{\"message\":\"Not Found\"}"));
        Assert.Null(ViffJson.ParseDepartures("<html>"));
    }

    [Fact]
    public void ParseCapacity_ReadsTheCadAirport()
    {
        const string json = """{"icao":"LFLL","taxiTime":12,"rate":36,"isCdm":true,"activated":true}""";

        Assert.Equal(new AirportCapacity("LFLL", 36, 12, true), ViffJson.ParseCapacity(json));
        Assert.Null(ViffJson.ParseCapacity("null"));
        Assert.Null(ViffJson.ParseCapacity("[]"));
    }

    [Fact]
    public void ParseMasters_SkipsEntriesWithoutAPosition()
    {
        const string json = """[{"icao":"LFPG","position":"LFPG_DEL"},{"icao":"LFPO","position":null}]""";

        Assert.Equal(new[] { new MasterAirport("LFPG", "LFPG_DEL") }, ViffJson.ParseMasters(json));
    }

    [Fact]
    public void ParseRestricted_KeepsFlightsWithACtot()
    {
        const string json = """
            [{"callsign":"KLM54G","ctot":"1605","etot":"1537","atot":"","mostPenalizingAirspace":"","mostPenalisingRegulation":"","cdmSts":""},
             {"callsign":"EJU75VY","ctot":"1605","atot":"","mostPenalizingAirspace":"LEBL","mostPenalisingRegulation":"","cdmSts":"REA"},
             {"callsign":"BAD","ctot":"","atot":""}]
            """;

        IReadOnlyList<RestrictedFlight>? flights = ViffJson.ParseRestricted(json);

        Assert.Equal(
            new[]
            {
                new RestrictedFlight("KLM54G", "1605", string.Empty, string.Empty, string.Empty),
                new RestrictedFlight("EJU75VY", "1605", "LEBL", string.Empty, "REA"),
            },
            flights);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("true\n", true)]
    [InlineData("false", false)]
    [InlineData("{\"callsign\":\"X\"}", false)]
    public void IsTrue_ReadsTheWriteAcknowledgement(string body, bool expected)
    {
        Assert.Equal(expected, ViffJson.IsTrue(body));
    }

    [Fact]
    public void ManualCtot_NeedsTheManualReasonSoAStaleAtfcmCtotIsNotTakenForOne()
    {
        DepartureRecord manual = new() { Callsign = "A", Cdm = new CdmData { Ctot = "1230", Reason = "MANUAL" } };
        DepartureRecord stale = new() { Callsign = "B", Cdm = new CdmData { Ctot = "1230", Reason = "LSGG" } };

        Assert.Equal("1230", manual.EffectiveCtot);
        Assert.Equal(string.Empty, stale.EffectiveCtot);
    }
}
