using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Tags;
using Viff.NeoRadar.Tests.Fakes;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Viff;

/// <summary>
/// Reads the live vIFF API, so these run only with VIFF_LIVE_TESTS=1. They only read; nothing is written.
/// </summary>
public sealed class LiveViffTests
{
    private static readonly bool s_enabled = Environment.GetEnvironmentVariable("VIFF_LIVE_TESTS") == "1";

    [Fact]
    public async Task LiveApi_ReadsMastersCapacityAndDepartures()
    {
        if (!s_enabled)
        {
            return;
        }

        FakeLogger logger = new();
        using ViffApiClient api = new(new Uri("https://viff-system.network/"), "TEST", "vIFF-neoradar/tests", logger);

        IReadOnlyList<MasterAirport>? masters = await api.GetMastersAsync(CancellationToken.None);
        AirportCapacity? capacity = await api.GetCapacityAsync("LFPG", CancellationToken.None);
        IReadOnlyList<DepartureRecord>? departures = await api.GetDeparturesAsync("LFPG", CancellationToken.None);
        IReadOnlyList<RestrictedFlight>? restricted = await api.GetRestrictedAsync(CancellationToken.None);

        Assert.NotNull(masters);
        Assert.NotNull(capacity);
        Assert.True(capacity.IsCdm);
        Assert.True(capacity.Rate > 0);
        Assert.NotNull(departures);
        Assert.NotNull(restricted);
        Assert.All(restricted, r => Assert.Equal(4, r.Ctot.Length));
        Assert.All(departures, d => Assert.Equal("LFPG", d.Departure));
        Assert.DoesNotContain(logger.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));
        Console.WriteLine($"masters {masters.Count}, LFPG rate {capacity.Rate} taxi {capacity.TaxiMinutes}, " +
                          $"departures {departures.Count}, with TSAT {departures.Count(d => d.Cdm.Tsat.Length > 0)}, " +
                          $"network CTOTs {restricted.Count}, on the ground {restricted.Count(r => r.Atot.Length == 0)}");
    }

    [Fact]
    public async Task LiveApi_ReadsTheAtfcmListStatusesAndTheSlotsFile()
    {
        if (!s_enabled)
        {
            return;
        }

        FakeLogger logger = new();
        using ViffApiClient api = new(new Uri("https://viff-system.network/"), "TEST", "vIFF-neoradar/tests", logger);
        using RemoteText remote = new(new Uri(RemoteText.DefaultBaseUrl), "vIFF-neoradar/tests", logger);

        IReadOnlyList<RelevantFlight>? relevant = await api.GetRelevantAsync(string.Empty, CancellationToken.None);
        IReadOnlyDictionary<string, string>? statuses = await api.GetAllStatusAsync(CancellationToken.None);
        IReadOnlyDictionary<string, string>? onTime = await api.GetAllOnTimeAsync(CancellationToken.None);
        string? slotsFile = await remote.GetAsync("ctot.txt", CancellationToken.None);

        Assert.NotNull(relevant);
        Assert.NotNull(statuses);
        Assert.NotNull(onTime);
        Assert.NotNull(slotsFile);
        Assert.DoesNotContain(logger.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));
        NetworkStatuses network = new();
        network.Replace(statuses, onTime);
        int parsedOnTime = onTime.Keys.Count(c => network.OnTimeOf(c) is not null);
        Console.WriteLine($"ATFCM list {relevant.Count}, statuses {statuses.Count}, on-time {onTime.Count} ({parsedOnTime} parsed), " +
                          $"event slots {EventSlots.Parse(slotsFile).Count}");
    }

    [Fact]
    public async Task LiveApi_ShowsLfpgDeparturesAsTheTagsWouldShowThem()
    {
        if (!s_enabled)
        {
            return;
        }

        using ViffApiClient api = new(new Uri("https://viff-system.network/"), "TEST", "vIFF-neoradar/tests", new FakeLogger());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CdmStore store = new();
        store.SetMasters((await api.GetMastersAsync(CancellationToken.None))!);
        store.SetNetworkCtots((await api.GetRestrictedAsync(CancellationToken.None))!);
        store.SetCapacity("LFPG", await api.GetCapacityAsync("LFPG", CancellationToken.None), now);
        store.ReplaceDepartures("LFPG", (await api.GetDeparturesAsync("LFPG", CancellationToken.None))!, now);

        foreach (FlightView flight in store.Flights().OrderBy(f => f.Record.Cdm.Ttot))
        {
            FlightTags tags = CdmTagFormatter.Format(flight, now, flashOn: true);
            string Show(CdmItem item) => tags.Items[item].Text;
            Console.WriteLine(
                $"{flight.Record.Callsign,-8} {flight.Record.Arrival,-4} E/TOBT {Show(CdmItem.Tobt),-4} TSAT {Show(CdmItem.Tsat),-4} " +
                $"TTOT {Show(CdmItem.Ttot),-4} CTOT {Show(CdmItem.Ctot),-4} REG {Show(CdmItem.Regulation),-14} " +
                $"ETFMS {Show(CdmItem.Etfms),-8} RDY {(tags.Ready ? "set" : "-")} window {tags.Tsat}");
        }
    }
}
