using System;
using Viff.NeoRadar.Common;
using Xunit;

namespace Viff.NeoRadar.Tests.Common;

public sealed class CdmTimeTests
{
    [Theory]
    [InlineData("1234", 12, 34, 0)]
    [InlineData("123456", 12, 34, 56)]
    [InlineData(" 0000 ", 0, 0, 0)]
    public void ParseTimeOfDay_ReadsFourAndSixDigitTimes(string text, int hours, int minutes, int seconds)
    {
        Assert.Equal(new TimeSpan(hours, minutes, seconds), CdmTime.ParseTimeOfDay(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("2400")]
    [InlineData("1260")]
    [InlineData("12:34")]
    [InlineData("123")]
    [InlineData("ctot")]
    public void ParseTimeOfDay_RefusesWhatIsNotATime(string? text)
    {
        Assert.Null(CdmTime.ParseTimeOfDay(text));
    }

    [Fact]
    public void ResolveNear_PlacesAnEarlyMorningTimeOnTheNextDayLateInTheEvening()
    {
        DateTimeOffset lateEvening = new(2026, 11, 22, 23, 50, 0, TimeSpan.Zero);

        DateTimeOffset resolved = CdmTime.ResolveNear(new TimeSpan(0, 10, 0), lateEvening);

        Assert.Equal(new DateTimeOffset(2026, 11, 23, 0, 10, 0, TimeSpan.Zero), resolved);
    }

    [Fact]
    public void ResolveNear_PlacesALateTimeOnThePreviousDayJustAfterMidnight()
    {
        DateTimeOffset afterMidnight = new(2026, 11, 23, 0, 5, 0, TimeSpan.Zero);

        DateTimeOffset resolved = CdmTime.ResolveNear(new TimeSpan(23, 55, 0), afterMidnight);

        Assert.Equal(new DateTimeOffset(2026, 11, 22, 23, 55, 0, TimeSpan.Zero), resolved);
    }

    [Fact]
    public void CeilToMinute_RoundsUpOnlyPartMinutes()
    {
        Assert.Equal(TestData.Noon, CdmTime.CeilToMinute(TestData.Noon));
        Assert.Equal(TestData.Noon.AddMinutes(1), CdmTime.CeilToMinute(TestData.Noon.AddSeconds(1)));
    }

    [Fact]
    public void Display_ShowsHoursAndMinutesOfEitherForm()
    {
        Assert.Equal("1321", CdmTime.Display("132100"));
        Assert.Equal("1331", CdmTime.Display("1331"));
        Assert.Equal(string.Empty, CdmTime.Display("ctot"));
    }
}
