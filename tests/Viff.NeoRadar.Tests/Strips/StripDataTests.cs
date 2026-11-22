using System.Linq;
using Viff.NeoRadar.Strips;
using Xunit;

namespace Viff.NeoRadar.Tests.Strips;

public sealed class StripDataTests
{
    [Fact]
    public void Parse_ReadsTheCdmPluginsBoxZero()
    {
        StripData strip = StripData.Parse("1158/1205/1200/120500/121500/STND///1203/A/1/");

        Assert.Equal("1158", strip[StripData.Asrt]);
        Assert.Equal("1205", strip[StripData.Tsac]);
        Assert.Equal("1200", strip[StripData.Tobt]);
        Assert.Equal("120500", strip[StripData.Tsat]);
        Assert.Equal("121500", strip[StripData.Ttot]);
        Assert.Equal("STND", strip[StripData.DeIce]);
        Assert.Equal("1203", strip[StripData.Ctoc]);
        Assert.Equal("A", strip[StripData.SetBy]);
        Assert.Equal(StripData.AutoStartupArmed, strip[StripData.AutoStartup]);
        Assert.Equal(string.Empty, strip[StripData.Atot]);
    }

    [Fact]
    public void Parse_ReadsAnEmptyOrShortBoxAsEmptyFields()
    {
        Assert.Equal(StripData.Empty, StripData.Parse(null));
        Assert.Equal(StripData.Empty, StripData.Parse(string.Empty));
        Assert.Equal("1158", StripData.Parse("1158")[StripData.Asrt]);
        Assert.Equal(string.Empty, StripData.Parse("1158")[StripData.Tsat]);
    }

    [Fact]
    public void Format_WritesAllTwelveFieldsAsTheCdmPluginDoes()
    {
        string box = StripData.Empty.With(StripData.Tsac, "1205").With(StripData.AutoStartup, "1").Format();

        Assert.Equal("/1205/////////1/", box);
        Assert.Equal(11, box.Count(c => c == '/'));
        Assert.Equal(string.Empty, StripData.Empty.Format());
    }

    [Fact]
    public void With_DropsCharactersTheBoxCannotHold()
    {
        StripData strip = StripData.Empty.With(StripData.DeIce, "RE/M:1");

        Assert.Equal("REM1", strip[StripData.DeIce]);
    }

    [Fact]
    public void Format_DropsAtotThenTheEcfmpIdToStayWithinFiftyCharacters()
    {
        StripData strip = StripData.Parse("1158/1205/1200/120500/121500/STND/ECFMP123//1203/A/1/CDM_ATOT|1215|AFR1~LFPG~EGLL~1200");

        string box = strip.Format();

        Assert.True(box.Length <= 50);
        Assert.Equal(string.Empty, StripData.Parse(box)[StripData.Atot]);
        Assert.Equal("121500", StripData.Parse(box)[StripData.Ttot]);
        Assert.Equal(StripData.AutoStartupArmed, StripData.Parse(box)[StripData.AutoStartup]);
    }

    [Fact]
    public void Equality_ComparesTheFields()
    {
        Assert.Equal(StripData.Parse("1158/1205"), StripData.Parse("1158/1205//"));
        Assert.NotEqual(StripData.Parse("1158/1205"), StripData.Parse("1158/1206"));
    }
}
