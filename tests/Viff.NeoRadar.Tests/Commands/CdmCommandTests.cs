using System;
using System.Collections.Generic;
using NeoRadar.PluginSdk.Chat;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Commands;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Panel;
using Viff.NeoRadar.Tests.Fakes;
using Xunit;

namespace Viff.NeoRadar.Tests.Commands;

public sealed class CdmCommandTests
{
    private readonly FakeLogger _logger = new();
    private readonly List<string?> _keys = new();
    private int _reloads;
    private readonly CdmCommand _command;

    public CdmCommandTests()
    {
        FakeViffApi api = new();
        CdmStore store = new();
        AirportControls controls = new(new FakeSettings());
        ManualClock clock = new(TestData.Noon);
        ConnectionGate gate = new(FakeFsd.LiveAtc("LFPG_DEL"));
        MasterSequencing sequencing = new(api, store, controls, new FakeLogger(), _ => { });
        CdmSync sync = new(api, store, controls, sequencing, gate, new FakeAirportApi(), new FakeFlightplanApi(), new FakeControllerDataApi(), clock);
        JobScheduler scheduler = new(clock, new FakeLogger());
        Notifier notifier = new(new FakeChat(), new FakeFeedback());
        MasterRole master = new(api, store, sync, gate, scheduler, notifier, () => { });
        AirportPanel panel = new(new FakeWindows(), store, sync, controls, master, gate, notifier);
        _command = new CdmCommand(store, sync, controls, master, panel, gate, notifier, clock, () => { }, _keys.Add,
            reloadConfig: () => _reloads++, logger: _logger);
    }

    [Fact]
    public void Execute_NeverLogsTheApiKey()
    {
        CommandResult result = _command.Execute("cdm", new[] { "apikey", "s3cret-key-value", string.Empty, string.Empty });

        Assert.True(result.Success);
        Assert.Equal("s3cret-key-value", Assert.Single(_keys));
        Assert.DoesNotContain(_logger.Lines, l => l.Contains("s3cret", StringComparison.Ordinal));
        Assert.Contains(_logger.Lines, l => l.Contains(".cdm apikey (hidden)", StringComparison.Ordinal));
    }

    [Fact]
    public void Execute_LogsOtherCommandsAndAsksForAReload()
    {
        _command.Execute("cdm", new[] { "reload" });

        Assert.Equal(1, _reloads);
        Assert.Contains(_logger.Lines, l => l.Contains(".cdm reload: done", StringComparison.Ordinal));
    }
}
