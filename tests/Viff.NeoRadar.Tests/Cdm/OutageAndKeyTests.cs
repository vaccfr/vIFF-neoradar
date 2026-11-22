using System;
using System.Threading;
using System.Threading.Tasks;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Tests.Fakes;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Cdm;

public sealed class OutageAndKeyTests
{
    private readonly FakeViffApi _api = new();
    private readonly CdmStore _store = new();
    private readonly ManualClock _clock = new(TestData.Noon);
    private readonly CdmSync _sync;

    public OutageAndKeyTests()
    {
        AirportControls controls = new(new FakeSettings());
        MasterSequencing master = new(_api, _store, controls, new FakeLogger(), _ => { });
        _sync = new CdmSync(_api, _store, controls, master, new ConnectionGate(FakeFsd.LiveAtc("LFPG_DEL")), new FakeAirportApi(),
            new FakeFlightplanApi(), new FakeControllerDataApi(), _clock);
    }

    [Fact]
    public async Task RunAsync_BacksOffWhileVIffIsDownAndRecovers()
    {
        _api.Down = true;
        await _sync.RunAsync(CancellationToken.None);
        _clock.Now = _clock.Now.AddSeconds(15);
        await _sync.RunAsync(CancellationToken.None);
        Assert.Equal(1, _api.MasterReads);

        _clock.Now = _clock.Now.AddSeconds(16);
        await _sync.RunAsync(CancellationToken.None);
        Assert.Equal(2, _api.MasterReads);

        _api.Down = false;
        _clock.Now = _clock.Now.AddSeconds(61);
        await _sync.RunAsync(CancellationToken.None);
        _clock.Now = _clock.Now.AddSeconds(15);
        await _sync.RunAsync(CancellationToken.None);
        Assert.Equal(4, _api.MasterReads);
    }

    [Fact]
    public void Claim_SaysTheKeyWasRefusedRatherThanThatSomeoneHoldsTheRole()
    {
        _api.Accept = false;
        _api.KeyRejected = true;
        _store.SetCapacity("LFPG", new AirportCapacity("LFPG", 60, 18, true), TestData.Noon);
        FakeChat chat = new();
        JobScheduler scheduler = new(_clock, new FakeLogger());
        MasterRole role = new(_api, _store, _sync, new ConnectionGate(FakeFsd.LiveAtc("LFPG_DEL")), scheduler,
            new Notifier(chat, new FakeFeedback()), () => { });

        role.Claim("LFPG");
        Assert.True(scheduler.WaitForIdle(TimeSpan.FromSeconds(5)));

        Assert.Contains(chat.Messages, m => m.Contains("refused the API key", StringComparison.Ordinal));
        Assert.DoesNotContain(chat.Messages, m => m.Contains("another controller", StringComparison.Ordinal));
    }
}
