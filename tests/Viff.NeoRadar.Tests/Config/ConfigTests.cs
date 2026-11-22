using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Config;
using Viff.NeoRadar.Tests.Fakes;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Config;

public sealed class ConfigTests
{
    // The options the CDM plugin's CDMconfig.xml carries, as a vACC ships it.
    private const string VaccConfig = """
        <?xml version="1.0"?>
        <CDM>
            <rate ops="30" />
            <rateLvo ops="10" />
            <bmi mode="true" />
            <invalidateAtTsat mode="true" />
            <readySetTsac mode="true" />
            <Rates url="https://example.org/cdm/rate.txt" />
            <Taxizones url="" />
            <Slots url="" />
            <sidInterval url="" />
            <DefaultTaxiTime minutes="12" />
            <DeIceTimes light="5" medium="9" heavy="12" super="15"/>
            <DeIceRemTaxi rem1Name= "REM1" rem1="8" rem2Name= "" rem2="" />
            <reqTobtPriority mode="false" />
            <eventPriority mode="true" />
            <ftpPassword password="" />
        </CDM>
        """;

    [Fact]
    public void CdmConfig_ReadsTheCdmPluginOptions()
    {
        List<string> warnings = new();

        CdmConfig config = CdmConfig.Parse(VaccConfig, warnings);

        Assert.Empty(warnings);
        Assert.Equal((30, 10, 12, true), (config.Rate, config.RateLvo, config.DefaultTaxiMinutes, config.Bmi));
        Assert.Equal(TsatInvalidation.Always, config.InvalidateAtTsat);
        Assert.Equal((false, true, true), (config.ReqTobtPriority, config.EventPriority, config.ReadySetTsac));
        Assert.Equal("https://example.org/cdm/rate.txt", config.RatesUrl);
        Assert.Equal(string.Empty, config.TaxiZonesUrl);
        Assert.Equal(9, config.DeIceMinutes["M"]);
        Assert.Equal(("REM1", 8), Assert.Single(config.RemotePads));
        Assert.Equal(-1, config.RunwayPressure);
    }

    [Fact]
    public void CdmConfig_FallsBackToDefaultsOnBadXmlOrNumbers()
    {
        List<string> warnings = new();
        Assert.Equal(15, CdmConfig.Parse("<CDM>", warnings).DefaultTaxiMinutes);
        Assert.Single(warnings);

        warnings.Clear();
        CdmConfig config = CdmConfig.Parse("<CDM><rate ops=\"fast\" /></CDM>", warnings);
        Assert.Equal(0, config.Rate);
        Assert.Contains("not a number", Assert.Single(warnings));
    }

    [Fact]
    public void RateTable_FindsTheFirstLineForTheRunwayAndActiveArrivals()
    {
        List<string> warnings = new();
        RateTable table = RateTable.Parse("""
            # comment
            LFBO:A:32L:32R:D:32R,32L:*:32R,32L:23_10
            LFBO:A:32R:32L:D:32R:32L:*:20_8
            LFPG:A:*:*:D:27L,26R:*:*:30_10,40_15_5
            not a line
            """, warnings);

        Assert.Equal(3, table.Count);
        Assert.Single(warnings);

        RunwayRate? both = table.Find("LFBO", "32R", string.Empty, new[] { "32L" }, new[] { "32R", "32L" });
        Assert.Equal((23, 10), (both!.Rate, both.LvoRate));
        Assert.Equal(new[] { "32R", "32L" }, both.Dependent);

        // 32R arrivals rule the first line out, and 32L departures the second.
        Assert.Null(table.Find("LFBO", "32R", string.Empty, new[] { "32R" }, new[] { "32R", "32L" }));
        Assert.Equal(20, table.Find("LFBO", "32R", string.Empty, new[] { "32R" }, new[] { "32R" })!.Rate);

        RunwayRate? second = table.Find("LFPG", "26R", string.Empty, Array.Empty<string>(), new[] { "27L", "26R" });
        Assert.Equal((40, 15, 5), (second!.Rate, second.LvoRate, second.Pressure));
        Assert.Equal(30, table.Find("LFPG", "27L", string.Empty, Array.Empty<string>(), new[] { "27L" })!.Rate);
        Assert.Null(table.Find("LFMN", "04R", string.Empty, Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void RateTable_LendsTheDependenciesOfALineNamingTheSid()
    {
        RateTable table = RateTable.Parse("""
            LFPG:A:*:*:D:26R:*:OPALE%A:30_10
            LFPG:A:*:*:D:27L:*:*:30_10
            """, new List<string>());

        Assert.Equal(new[] { "OPALE%A" }, table.Find("LFPG", "27L", "OPALE5A", Array.Empty<string>(), new[] { "27L" })!.Dependent);
        Assert.Empty(table.Find("LFPG", "27L", "NURMO5A", Array.Empty<string>(), new[] { "27L" })!.Dependent);
    }

    [Theory]
    [InlineData("OPALE%A", "OPALE5A", true)]
    [InlineData("OPALE*A", "opale5a", true)]
    [InlineData("OPALE%A", "OPALE5B", false)]
    [InlineData("27L", "27L", true)]
    [InlineData("27%", "27LX", false)]
    public void PatternMatches_IsOneCharacterPerWildcard(string pattern, string value, bool matches) =>
        Assert.Equal(matches, RateTable.PatternMatches(pattern, value));

    [Fact]
    public void TaxiZones_FindTheZoneAnAircraftStandsIn()
    {
        List<string> warnings = new();
        TaxiZones zones = TaxiZones.Parse("""
            LFBO:14L:43.62:1.37:43.63:1.37:43.63:1.38:43.62:1.38:9
            LFBO:32R:N043.37.12.000:E001.22.12.000:N043.37.48.000:E001.22.12.000:N043.37.48.000:E001.22.48.000:N043.37.12.000:E001.22.48.000:13:18,20
            LFBO:14L:bad
            """, warnings);

        Assert.Equal(2, zones.Count);
        Assert.Single(warnings);
        Assert.Equal(9, zones.Find("LFBO", "14L", 43.625, 1.375)!.TaxiMinutes);
        Assert.Null(zones.Find("LFBO", "14L", 43.64, 1.375));
        Assert.Null(zones.Find("LFBO", "14R", 43.625, 1.375));
        TaxiZone dms = zones.Find("LFBO", "32R", 43.625, 1.375)!;
        Assert.Equal(13, dms.TaxiMinutes);
        Assert.Equal(new[] { 18, 20 }, dms.RemoteTaxiMinutes);
        Assert.True(zones.HasAirport("lfbo"));
    }

    [Theory]
    [InlineData("N049.00.36.000", 49.01)]
    [InlineData("W001.30.00.000", -1.5)]
    [InlineData("2.5", 2.5)]
    public void ReadCoordinate_ReadsDecimalAndEuroScopeForms(string text, double expected) =>
        Assert.Equal(expected, TaxiZones.ReadCoordinate(text)!.Value, 6);

    [Fact]
    public void SidIntervals_MatchEitherOrderAndTheOtherRunway()
    {
        List<string> warnings = new();
        SidIntervals intervals = SidIntervals.Parse("""
            # LFBO
            LFBO,14L,FISTO,GAUDE,3
            LFBO,14L,LACOU,14R,RIBOU,2.5
            LFBO,14L,BROKEN
            """, warnings);

        Assert.Single(warnings);
        Assert.Equal(TimeSpan.FromMinutes(3), intervals.Between("LFBO", "14L", "GAUDE5B", "14R", "FISTO5B"));
        Assert.Equal(TimeSpan.FromMinutes(2.5), intervals.Between("LFBO", "14L", "LACOU5B", "14R", "RIBOU5B"));
        Assert.Equal(TimeSpan.Zero, intervals.Between("LFBO", "14L", "LACOU5B", "14L", "RIBOU5B"));
        Assert.Equal(TimeSpan.Zero, intervals.Between("LFBO", "14R", "FISTO5B", "14R", "GAUDE5B"));
        Assert.Equal("LARPA", SidIntervals.PointOf("LARPA4Q"));
    }

    [Fact]
    public async Task ConfigLoader_ReadsLocalFilesBesideTheConfigAndPrefersThePackage()
    {
        using TempFolder package = new();
        using TempFolder documents = new();
        string cdm = Directory.CreateDirectory(Path.Combine(package.Path, ConfigLoader.PackageFolder)).FullName;
        File.WriteAllText(Path.Combine(cdm, ConfigLoader.ConfigFileName), "<CDM><rate ops=\"25\" /><Rates url=\"\" /><Slots url=\"\" /></CDM>");
        File.WriteAllText(Path.Combine(cdm, "rate.txt"), "LFPG:A:*:*:D:27L:*:*:30_10\n");
        File.WriteAllText(Path.Combine(cdm, "ctot.txt"), "123456,1230\n");
        Directory.CreateDirectory(Path.Combine(documents.Path, ConfigLoader.DocumentsFolder));
        File.WriteAllText(Path.Combine(documents.Path, ConfigLoader.DocumentsFolder, ConfigLoader.ConfigFileName), "<CDM><rate ops=\"5\" /></CDM>");
        EventSlots slots = new();
        ConfigLoader loader = new(() => package.Path, documents.Path, new FakeRemoteText(), slots, new FakeLogger());

        await loader.LoadAsync(CancellationToken.None);

        Assert.True(loader.Current.Loaded);
        Assert.Equal(25, loader.Current.Config.Rate);
        Assert.Equal(1, loader.Current.Rates.Count);
        Assert.Equal(1, slots.Count);
        Assert.False(loader.SourceChanged());

        File.Delete(Path.Combine(cdm, ConfigLoader.ConfigFileName));
        Assert.True(loader.SourceChanged());
        await loader.LoadAsync(CancellationToken.None);
        Assert.Equal(5, loader.Current.Config.Rate);
    }

    [Fact]
    public async Task ConfigLoader_DownloadsUrlsAndFallsBackToTheLastCopy()
    {
        using TempFolder documents = new();
        string folder = Directory.CreateDirectory(Path.Combine(documents.Path, ConfigLoader.DocumentsFolder)).FullName;
        File.WriteAllText(Path.Combine(folder, ConfigLoader.ConfigFileName), "<CDM><Rates url=\"https://example.org/rate.txt\" /></CDM>");
        FakeRemoteText remote = new();
        remote.Files["https://example.org/rate.txt"] = "LFPG:A:*:*:D:27L:*:*:30_10\n";
        FakeLogger logger = new();
        ConfigLoader loader = new(() => null, documents.Path, remote, new EventSlots(), logger);

        await loader.LoadAsync(CancellationToken.None);
        Assert.Equal(1, loader.Current.Rates.Count);

        remote.Files.Clear();
        await loader.LoadAsync(CancellationToken.None);
        Assert.Equal(1, loader.Current.Rates.Count);
        Assert.Contains(logger.Lines, l => l.Contains("using the copy downloaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfigLoader_RunsOnVIffAloneWithoutAConfig()
    {
        using TempFolder documents = new();
        ConfigLoader loader = new(() => throw new InvalidOperationException("no package"), documents.Path, new FakeRemoteText(), new EventSlots(), new FakeLogger());

        await loader.LoadAsync(CancellationToken.None);

        Assert.False(loader.Current.Loaded);
    }

    private sealed class FakeRemoteText : IRemoteText
    {
        public Dictionary<string, string> Files { get; } = new();

        public Task<string?> GetAsync(Uri url, CancellationToken cancellationToken) =>
            Task.FromResult(Files.TryGetValue(url.ToString(), out string? text) ? text : null);
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "viff-tests-" + Guid.NewGuid().ToString("N"))).FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
