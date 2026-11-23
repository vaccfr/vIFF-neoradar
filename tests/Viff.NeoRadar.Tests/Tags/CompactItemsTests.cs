using System;
using System.Collections.Generic;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Tags;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Tags;

public sealed class CompactItemsTests
{
    private static readonly AirportView s_lfpg = new("LFPG", new AirportCapacity("LFPG", 60, 18, true), "LFPG_DEL");
    private static readonly AirportView s_lfob = new("LFOB", new AirportCapacity("LFOB", 10, 8, false), string.Empty);
    private static readonly DateTimeOffset s_now = TestData.Noon;

    [Fact]
    public void Time_NamesTheTimeThatMattersNow()
    {
        Assert.Equal(("EOBT 1230", "E1230"), Times(TestData.Departure("A", "1230"), s_lfob));
        Assert.Equal(("TOBT 1230", "B1230"), Times(TestData.Departure("A", "1230").WithCdm(c => c with { Tobt = "123000" })));
        Assert.Equal(("TSAT 1235", "S1235"), Times(TestData.Departure("A", "1230").WithCdm(c => c with { Tobt = "123000", Tsat = "123500", Ttot = "125000" })));
        Assert.Equal(("TTOT 1210", "T1210"), Times(
            TestData.Departure("A", "1150").WithCdm(c => c with { Tobt = "115000", Tsat = "115500", Ttot = "121000" }) with { Aobt = "1155" }));
        Assert.Equal(("TOBT 1140", "B1140"), Times(
            TestData.Departure("A", "1140").WithCdm(c => c with { Tobt = "114000" }) with { CdmStatus = "FLS-CDM" }));
    }

    [Fact]
    public void Time_KeepsTheColourOfItsOwnItem()
    {
        FlightTags tags = Format(TestData.Departure("A", "1200").WithCdm(c => c with { Tobt = "120000", Tsat = "120200", Ttot = "121500" }));

        Assert.Equal(tags.Items[CdmItem.Tsat].Colour, tags.Items[CdmItem.Time].Colour);
        Assert.Equal(CdmTagFormatter.DarkGreen, tags.Items[CdmItem.Time].Colour);
    }

    [Theory]
    [InlineData("FLS-CDM", "", "120200", "SUSP")]
    [InlineData("", "", "114000", "EXP")]
    [InlineData("REA", "", "122000", "REA")]
    [InlineData("", "1158", "122000", "RDY")]
    [InlineData("", "", "122000", "")]
    public void Status_ShowsTheStatusThatMattersMost(string cdmStatus, string asrt, string tsat, string expected)
    {
        DepartureRecord record = TestData.Departure("A", "1130").WithCdm(c => c with { Tobt = "113000", Tsat = tsat, Ttot = "125000", Asrt = asrt })
            with { CdmStatus = cdmStatus };

        Assert.Equal(expected, Format(record).Items[CdmItem.Status].Text);
    }

    [Theory]
    [InlineData("122500", "", "", CdmStage.Later)]
    [InlineData("120900", "", "", CdmStage.Now)]
    [InlineData("122500", "1158", "", CdmStage.Now)]
    [InlineData("115500", "", "1156", CdmStage.Started)]
    public void Stage_SplitsTheDeparturesByWhatTheyNeed(string tsat, string asrt, string aobt, string expected)
    {
        DepartureRecord record = TestData.Departure("A", "1130").WithCdm(c => c with { Tobt = "113000", Tsat = tsat, Ttot = "125000", Asrt = asrt })
            with { Aobt = aobt };

        Assert.Equal(expected, Format(record).Stage);
    }

    [Fact]
    public void Stage_IsEmptyForFlightsFromElsewhereOrWithoutCdm()
    {
        DepartureRecord record = TestData.Departure("A", "1205");

        Assert.Equal(string.Empty, Format(record, s_lfob).Stage);
        Assert.Equal(string.Empty, CdmTagFormatter.Format(new FlightView(record, s_lfpg, null, string.Empty, Watched: false), s_now, true).Stage);
    }

    [Fact]
    public void Sequence_NumbersEachAirportByTtot()
    {
        List<FlightView> flights = new()
        {
            View(TestData.Departure("LATE", "1200").WithCdm(c => c with { Ttot = "123000" })),
            View(TestData.Departure("EARLY", "1200").WithCdm(c => c with { Ttot = "121500" })),
            View(TestData.Departure("TAXI", "1150").WithCdm(c => c with { Ttot = "121000" }) with { Aobt = "1155" }),
            View(TestData.Departure("SUSP", "1150").WithCdm(c => c with { Ttot = "120500" }) with { CdmStatus = "FLS-CDM" }),
            View(TestData.Departure("GONE", "1140").WithCdm(c => c with { Ttot = "115500" }) with { Atot = "1156" }),
            View(TestData.Departure("ORLY", "1200", airport: "LFPO").WithCdm(c => c with { Ttot = "122000" })),
        };

        IReadOnlyDictionary<string, string> sequence = CdmTagFormatter.Sequence(flights, s_now);

        Assert.Equal("01", sequence["TAXI"]);
        Assert.Equal("02", sequence["EARLY"]);
        Assert.Equal("03", sequence["LATE"]);
        Assert.Equal("01", sequence["ORLY"]);
        Assert.False(sequence.ContainsKey("SUSP"));
        Assert.False(sequence.ContainsKey("GONE"));
    }

    private static FlightView View(DepartureRecord record) =>
        new(record, record.Departure == "LFPO" ? s_lfpg with { Icao = "LFPO" } : s_lfpg, null, string.Empty);

    private static FlightTags Format(DepartureRecord record, AirportView? airport = null) =>
        CdmTagFormatter.Format(new FlightView(record, airport ?? s_lfpg, null, string.Empty), s_now, true);

    private static (string, string) Times(DepartureRecord record, AirportView? airport = null)
    {
        FlightTags tags = Format(record, airport);
        return (tags.Items[CdmItem.Time].Text, tags.Items[CdmItem.TimeShort].Text);
    }
}
