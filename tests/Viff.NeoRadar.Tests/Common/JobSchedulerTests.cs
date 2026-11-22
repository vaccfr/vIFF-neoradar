using System;
using System.Threading;
using System.Threading.Tasks;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Tests.Fakes;
using Xunit;

namespace Viff.NeoRadar.Tests.Common;

public sealed class JobSchedulerTests
{
    private readonly ManualClock _clock = new(TestData.Noon);
    private readonly FakeLogger _logger = new();

    [Fact]
    public void Tick_RunsAPeriodicJobOnceThenAgainAfterItsPeriod()
    {
        JobScheduler scheduler = new(_clock, _logger);
        int runs = 0;
        scheduler.Every("count", TimeSpan.FromSeconds(10), _ =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });

        TickAndSettle(scheduler);
        TickAndSettle(scheduler);
        Assert.Equal(1, runs);

        _clock.Now = _clock.Now.AddSeconds(10);
        TickAndSettle(scheduler);
        Assert.Equal(2, runs);
        scheduler.Stop(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Tick_NeverOverlapsAJobWithItself()
    {
        JobScheduler scheduler = new(_clock, _logger);
        using ManualResetEventSlim release = new();
        int running = 0;
        int overlaps = 0;
        scheduler.Every("slow", TimeSpan.Zero, _ =>
        {
            if (Interlocked.Increment(ref running) > 1)
            {
                Interlocked.Increment(ref overlaps);
            }

            release.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref running);
            return Task.CompletedTask;
        });

        for (int i = 0; i < 5; i++)
        {
            scheduler.Tick();
        }

        release.Set();
        scheduler.Stop(TimeSpan.FromSeconds(5));
        Assert.Equal(0, overlaps);
    }

    [Fact]
    public void TriggerSoon_MakesAJobDueOnTheNextTick()
    {
        JobScheduler scheduler = new(_clock, _logger);
        int runs = 0;
        JobScheduler.PeriodicJob job = scheduler.Every("sync", TimeSpan.FromMinutes(5), _ =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });
        TickAndSettle(scheduler);

        scheduler.TriggerSoon(job);
        TickAndSettle(scheduler);

        Assert.Equal(2, runs);
        scheduler.Stop(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Stop_PreventsFurtherWorkAndLogsAFailingJob()
    {
        JobScheduler scheduler = new(_clock, _logger);
        scheduler.RunOnce("boom", _ => throw new InvalidOperationException("boom"));
        scheduler.Stop(TimeSpan.FromSeconds(5));

        bool ran = false;
        scheduler.RunOnce("late", _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.False(ran);
        Assert.Contains(_logger.Lines, l => l.Contains("[boom]", StringComparison.Ordinal));
    }

    private static void TickAndSettle(JobScheduler scheduler)
    {
        scheduler.Tick();
        Assert.True(scheduler.WaitForIdle(TimeSpan.FromSeconds(5)));
    }
}
