using System.Collections.Generic;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Tests.Fakes;
using Xunit;

namespace Viff.NeoRadar.Tests.Cdm;

public sealed class AirportControlsTests
{
    private readonly AirportControls _controls = new(new FakeSettings());

    [Fact]
    public void EffectiveRate_PrefersTheOverrideAndTheLvoRateUnderLvo()
    {
        Assert.Equal(36, _controls.EffectiveRate("LFPG", 36, out _));

        _controls.SetRates("LFPG", 40, 20);
        Assert.Equal(40, _controls.EffectiveRate("LFPG", 36, out _));

        _controls.ToggleLvo("LFPG");
        Assert.Equal(20, _controls.EffectiveRate("LFPG", 36, out bool missing));
        Assert.False(missing);
    }

    [Fact]
    public void EffectiveRate_ReportsAMissingLvoRate()
    {
        _controls.ToggleLvo("LFPG");

        Assert.Equal(36, _controls.EffectiveRate("LFPG", 36, out bool missing));
        Assert.True(missing);
    }

    [Fact]
    public void ParsePads_ReadsNamesAndMinutes()
    {
        IReadOnlyList<(string, int)>? pads = AirportControls.ParsePads("rem1=10, REM2");

        Assert.Equal(new[] { ("REM1", 10), ("REM2", 0) }, pads);
    }

    [Theory]
    [InlineData("REM1=ten")]
    [InlineData("REM 1=10")]
    [InlineData("STND=5")]
    [InlineData("REM1=90")]
    [InlineData("REM1=1=2")]
    public void ParsePads_RefusesMalformedLists(string definition)
    {
        Assert.Null(AirportControls.ParsePads(definition));
    }

    [Fact]
    public void Delays_AreKeptPerAirportAndRunway()
    {
        _controls.SetStartupDelay("LFPG", "27L", TestData.Noon);
        _controls.SetStartupDelay("LFPO", "24", TestData.Noon);

        Assert.Equal(new[] { "27L" }, _controls.StartupDelays("LFPG").Keys);

        _controls.SetStartupDelay("LFPG", "27L", null);
        Assert.Empty(_controls.StartupDelays("LFPG"));
    }
}
