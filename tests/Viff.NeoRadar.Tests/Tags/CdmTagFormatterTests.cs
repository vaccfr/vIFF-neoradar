using System;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Tags;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Tags;

public sealed class CdmTagFormatterTests
{
    private static readonly AirportView s_cdmAirport = new("LFPG", new AirportCapacity("LFPG", 30, 12, true), "LFPG_DEL");
    private static readonly AirportView s_otherAirport = new("LFRS", new AirportCapacity("LFRS", 0, 0, false), string.Empty);

    [Theory]
    [InlineData(-20, "Future")]
    [InlineData(-5, "Window")]
    [InlineData(5, "Window")]
    [InlineData(6, "Closing")]
    [InlineData(7, "Expired")]
    public void StateOf_FollowsTheStartUpWindow(int minutesSinceTsat, string expected)
    {
        DateTimeOffset tsat = TestData.Noon;

        Assert.Equal(Enum.Parse<TsatState>(expected), CdmTagFormatter.StateOf(true, false, tsat, tsat.AddMinutes(minutesSinceTsat)));
    }

    [Fact]
    public void Format_ShowsATildeBeforeATsatIsCalculated()
    {
        FlightTags tags = Format(TestData.Departure("AFR1", "1230"), s_cdmAirport);

        Assert.Equal("~", tags.Items[CdmItem.Tsat].Text);
        Assert.Equal(TsatState.Pending, tags.Tsat);
    }

    [Fact]
    public void Format_ShowsTimesCountdownAndRdy()
    {
        DepartureRecord record = TestData.Departure("AFR1", "1200").WithCdm(c => c with { Tobt = "120500", Tsat = "121000", Ttot = "122000", Asrt = "1158" });

        FlightTags tags = Format(record, s_cdmAirport);

        Assert.Equal("1205", tags.Items[CdmItem.Tobt].Text);
        Assert.Equal(("1210", CdmTagFormatter.LightGreen), (tags.Items[CdmItem.Tsat].Text, tags.Items[CdmItem.Tsat].Colour));
        Assert.Equal("10", tags.Items[CdmItem.Ttg].Text);
        Assert.Equal("1220", tags.Items[CdmItem.Ttot].Text);
        Assert.Equal(CdmTagFormatter.DarkGreen, tags.Items[CdmItem.Ready].Colour);
        Assert.True(tags.Ready);
    }

    [Fact]
    public void Format_ShowsTheEobtAndNoTsatAtANonCdmAirport()
    {
        DepartureRecord record = TestData.Departure("AFR1", "1230") with { Ctot = "1300", Regulation = "LFRR" };

        FlightTags tags = Format(record, s_otherAirport);

        Assert.Equal("1230", tags.Items[CdmItem.Tobt].Text);
        Assert.Equal(string.Empty, tags.Items[CdmItem.Tsat].Text);
        Assert.Equal(("1300", "LFRR"), (tags.Items[CdmItem.Ctot].Text, tags.Items[CdmItem.Regulation].Text));
        Assert.Equal(string.Empty, tags.Items[CdmItem.Message].Text);
    }

    [Fact]
    public void Format_ColoursACtotRedOnceTheTtotIsPastItsWindow()
    {
        DepartureRecord record = TestData.Departure("AFR1", "1200") with { Ctot = "1215" };
        record = record.WithCdm(c => c with { Tsat = "1220", Ttot = "123000" });

        Assert.Equal(CdmTagFormatter.Red, Format(record, s_cdmAirport).Items[CdmItem.Ctot].Colour);
    }

    [Theory]
    [InlineData("FLS-CDM", false, "", "FLS-CDM")]
    [InlineData("SAM", true, "", "REA")]
    [InlineData("ATC_ACTIV", false, "", "ACT")]
    [InlineData("", false, "REA", "REA")]
    [InlineData("ATC_ACTIV", true, "FLS-CDM", "FLS-CDM")]
    [InlineData("FLS-NRA", false, "COMPLY", "FLS-NRA")]
    public void Etfms_PutsSuspensionsFirstThenRea(string atfcmStatus, bool isRea, string cdmStatus, string expected)
    {
        DepartureRecord record = new() { AtfcmStatus = atfcmStatus, IsRea = isRea, CdmStatus = cdmStatus };

        Assert.Equal(expected, CdmTagFormatter.Etfms(record));
    }

    [Fact]
    public void Format_ShowsASuspendedFlightPlanInRed()
    {
        DepartureRecord record = TestData.Departure("DLH3MY", "1155").WithCdm(c => c with { Tobt = "115500", Tsat = "115500" }) with { CdmStatus = "FLS-CDM" };

        FlightTags tags = Format(record, s_cdmAirport);

        Assert.True(tags.Suspended);
        Assert.Equal(TsatState.Suspended, tags.Tsat);
        Assert.Equal(("FLS-CDM", CdmTagFormatter.Red), (tags.Items[CdmItem.Etfms].Text, tags.Items[CdmItem.Etfms].Colour));
        Assert.Equal(CdmTagFormatter.Red, tags.Items[CdmItem.Tsat].Colour);
        Assert.Equal(CdmTagFormatter.Red, tags.Items[CdmItem.Tobt].Colour);
        Assert.Equal(string.Empty, tags.Items[CdmItem.Ttg].Text);
    }

    private static FlightTags Format(DepartureRecord record, AirportView airport) =>
        CdmTagFormatter.Format(new FlightView(record, airport, null, string.Empty), TestData.Noon, flashOn: true);
}
