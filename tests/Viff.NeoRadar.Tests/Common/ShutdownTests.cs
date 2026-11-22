using System;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Feedback;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Tests.Fakes;
using Xunit;

namespace Viff.NeoRadar.Tests.Common;

/// <summary>
/// Nothing reaches the client once shutdown starts: work finishing on the pool must not call a client that is closing.
/// </summary>
public sealed class ShutdownTests
{
    [Fact]
    public void MutedNotifier_PostsNothing()
    {
        FakeChat chat = new();
        FakeFeedback feedback = new();
        Notifier notifier = new(chat, feedback);

        notifier.Mute();
        notifier.Say("Released the CDM master role of LFPO.");
        notifier.Toast("RDY set", FeedbackKind.Success);

        Assert.Empty(chat.Messages);
        Assert.Empty(feedback.Toasts);
    }

    [Fact]
    public async Task CancelledSyncCycle_NeitherReadsNorPublishes()
    {
        FakeViffApi api = new();
        CdmStore store = new();
        AirportControls controls = new(new FakeSettings());
        CdmSync sync = new(api, store, controls, new MasterSequencing(api, store, controls, new FakeLogger(), _ => { }),
            new ConnectionGate(FakeFsd.LiveAtc("LFPG_DEL")), new FakeAirportApi(), new FakeFlightplanApi(), new FakeControllerDataApi(),
            new ManualClock(TestData.Noon));
        bool synced = false;
        sync.Synced += () => synced = true;
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await sync.RunAsync(cancelled.Token);

        Assert.Equal(0, api.MasterReads);
        Assert.False(synced);
    }

    [Fact]
    public void Scheduler_ReportsWhenStopped()
    {
        JobScheduler scheduler = new(new ManualClock(TestData.Noon), new FakeLogger());
        Assert.False(scheduler.IsStopped);

        scheduler.Stop(TimeSpan.FromSeconds(1));

        Assert.True(scheduler.IsStopped);
    }
}
