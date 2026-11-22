using System;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Strips;
using Viff.NeoRadar.Tags;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Tags;

public sealed class NewItemsTests
{
    private static readonly AirportView s_lfpg = new("LFPG", new AirportCapacity("LFPG", 60, 18, true), "LFPG_DEL");
    private static readonly DateTimeOffset s_now = TestData.Noon;

    [Theory]
    [InlineData("1203", "C")]
    [InlineData("1210", "O")]
    [InlineData(StripData.TsacPending, "P")]
    [InlineData("", "")]
    public void Tsac_IsGreenNearTheTsatOrangeFurther(string tsac, string expected)
    {
        TagText text = CdmTagFormatter.Tsac(tsac, s_now, s_now);

        string actual = text.Text.Length == 0 ? string.Empty
            : text.Text == "...." ? "P"
            : text.Colour == CdmTagFormatter.DarkGreen ? "C" : text.Colour == CdmTagFormatter.Orange ? "O" : "?";
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("1230", "1230", "1230", "grey")]
    [InlineData("1230", "1233", "1230+3", "grey")]
    [InlineData("1230", "1240", "1230+10", "red")]
    [InlineData("1230", "1220", "1230-10", "green")]
    [InlineData("1230", "", "X", "orange")]
    [InlineData("", "1230", "", "none")]
    public void Ctoc_ShowsHowTheCtotMovedSinceItWasCommunicated(string ctoc, string ctot, string text, string colour)
    {
        TagText tag = CdmTagFormatter.Ctoc(ctoc, ctot, s_now);

        Assert.Equal(text, tag.Text);
        Assert.Equal(colour, tag.Colour switch
        {
            null => "none",
            var c when c == CdmTagFormatter.Grey => "grey",
            var c when c == CdmTagFormatter.Red => "red",
            var c when c == CdmTagFormatter.DarkGreen => "green",
            var c when c == CdmTagFormatter.Orange => "orange",
            _ => "?",
        });
    }

    [Fact]
    public void Format_ShowsPhaseDelayStripFieldsAndEventSlot()
    {
        DepartureRecord record = TestData.Departure("AFR1", "1200").WithCdm(c => c with { Tobt = "120000", Tsat = "121200", Ttot = "123000" });
        StripData strip = StripData.Empty.With(StripData.Tsac, "1212").With(StripData.AutoStartup, StripData.AutoStartupArmed).With(StripData.SetBy, "A");

        FlightTags tags = CdmTagFormatter.Format(new FlightView(record, s_lfpg, null, string.Empty), s_now, true, new FlightExtras(strip, "1240", false));

        Assert.Equal("C", tags.Items[CdmItem.Phase].Text);
        Assert.Equal("12", tags.Items[CdmItem.Delay].Text);
        Assert.Equal("1212", tags.Items[CdmItem.Tsac].Text);
        Assert.Equal(("S", CdmTagFormatter.DarkGreen), (tags.Items[CdmItem.AutoStartup].Text, tags.Items[CdmItem.AutoStartup].Colour));
        Assert.Equal("A", tags.Items[CdmItem.SetBy].Text);
        Assert.Equal("1240", tags.Items[CdmItem.EventSlot].Text);
    }

    [Theory]
    [InlineData("1300", "", "P")]
    [InlineData("1200", "115000", "I")]
    [InlineData("1200", "", "C")]
    public void Phase_IsPlannedInWindowOrInvalid(string eobt, string tsat, string expected)
    {
        DepartureRecord record = TestData.Departure("AFR1", eobt).WithCdm(c => c with { Tsat = tsat });

        Assert.Equal(expected, CdmTagFormatter.Format(new FlightView(record, s_lfpg, null, string.Empty), s_now, true).Items[CdmItem.Phase].Text);
    }

    [Fact]
    public void Format_GreysAnIgnoredCtot()
    {
        DepartureRecord record = TestData.Departure("AFR1", "1200") with { Ctot = "1230" };

        FlightTags tags = CdmTagFormatter.Format(new FlightView(record, s_lfpg, null, string.Empty), s_now, true, new FlightExtras(StripData.Empty, string.Empty, true));

        Assert.Equal(("1230", CdmTagFormatter.Grey), (tags.Items[CdmItem.Ctot].Text, tags.Items[CdmItem.Ctot].Colour));
    }

    [Theory]
    [InlineData("COMPLY", "C")]
    [InlineData("AIRB", "A")]
    [InlineData("REA", "")]
    public void Compliance_ShowsTheAirborneLetter(string status, string expected)
    {
        Assert.Equal(expected, CdmTagFormatter.Compliance(status).Text);
    }

    [Fact]
    public void OnTime_IsRedWhenLateGreenWhenEarly()
    {
        Assert.Equal(("+12", CdmTagFormatter.Red), (CdmTagFormatter.OnTime(12).Text, CdmTagFormatter.OnTime(12).Colour));
        Assert.Equal(("-3", CdmTagFormatter.DarkGreen), (CdmTagFormatter.OnTime(-3).Text, CdmTagFormatter.OnTime(-3).Colour));
        Assert.Equal(string.Empty, CdmTagFormatter.OnTime(0).Text);
        Assert.Equal(string.Empty, CdmTagFormatter.OnTime(null).Text);
    }
}
