using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
}
