using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Chat;
using NeoRadar.PluginSdk.Feedback;
using NeoRadar.PluginSdk.Fsd;
using NeoRadar.PluginSdk.Logger;
using NeoRadar.PluginSdk.Settings;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Tests.Fakes;

internal sealed class ManualClock : TimeProvider
{
    public ManualClock(DateTimeOffset now)
    {
        Now = now;
    }

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeViffApi : IViffApi
{
    private readonly object _lock = new();

    public List<CdmDataUpdate> Writes { get; } = new();

    public List<(string Callsign, string Value)> Dpis { get; } = new();

    public List<(string Icao, string Position, bool Set)> MasterCalls { get; } = new();

    public Dictionary<string, List<DepartureRecord>> Departures { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, AirportCapacity> Capacities { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<MasterAirport> Masters { get; } = new();

    public List<RestrictedFlight> Restricted { get; } = new();

    public List<string> DepartureReads { get; } = new();

    public bool Accept { get; set; } = true;

    public Task<IReadOnlyList<DepartureRecord>?> GetDeparturesAsync(string icao, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            DepartureReads.Add(icao);
        }

        return Task.FromResult<IReadOnlyList<DepartureRecord>?>(Departures.TryGetValue(icao, out List<DepartureRecord>? records) ? records.ToList() : new List<DepartureRecord>());
    }

    public Task<IReadOnlyList<RestrictedFlight>?> GetRestrictedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RestrictedFlight>?>(Restricted.ToList());

    public Task<AirportCapacity?> GetCapacityAsync(string icao, CancellationToken cancellationToken) =>
        Task.FromResult(Capacities.GetValueOrDefault(icao));

    public Task<IReadOnlyList<MasterAirport>?> GetMastersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MasterAirport>?>(Masters.ToList());

    public Task<bool> SetMasterAsync(string icao, string position, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            MasterCalls.Add((icao, position, true));
        }

        return Task.FromResult(Accept);
    }

    public Task<bool> RemoveMasterAsync(string icao, string position, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            MasterCalls.Add((icao, position, false));
        }

        return Task.FromResult(Accept);
    }

    public Task<bool> SetCdmDataAsync(CdmDataUpdate update, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Writes.Add(update);
        }

        return Task.FromResult(Accept);
    }

    public Task<bool> SendDpiAsync(string callsign, string value, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Dpis.Add((callsign, value));
        }

        return Task.FromResult(Accept);
    }
}

internal sealed class FakeSettings : ISettingsApi
{
    private readonly Dictionary<string, string> _values = new();

    public string? ActiveProfile => null;

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public bool Set(string key, string value)
    {
        _values[key] = value;
        return true;
    }

    public bool Remove(string key) => _values.Remove(key);

    public IReadOnlyList<string> Keys() => _values.Keys.ToList();

    public string? GetForProfile(string key) => null;

    public bool SetForProfile(string key, string value) => false;

    public bool RemoveForProfile(string key) => false;
}

internal sealed class FakeFsd : IFsdApi
{
    public ConnectionInfo? Connection { get; set; }

    public static FakeFsd LiveAtc(string callsign) => new()
    {
        Connection = new ConnectionInfo(callsign, 1000000, "Test", NetworkRating.S2, true, NetworkFacility.DEL, ServerType.Live, true,
            Array.Empty<string>(), Array.Empty<int>()),
    };

    public ConnectionInfo? GetConnection() => Connection;
}

internal sealed class FakeLogger : ILoggerApi
{
    public List<string> Lines { get; } = new();

    public void Log(LogLevel level, string message)
    {
        lock (Lines)
        {
            Lines.Add($"{level}: {message}");
        }
    }

    public void Fatal(string message) => Log(LogLevel.Fatal, message);

    public void Error(string message) => Log(LogLevel.Error, message);

    public void Warning(string message) => Log(LogLevel.Warning, message);

    public void Info(string message) => Log(LogLevel.Info, message);

    public void Debug(string message) => Log(LogLevel.Debug, message);

    public void Verbose(string message) => Log(LogLevel.Verbose, message);
}

internal sealed class FakeChat : IChatApi
{
    public List<string> Messages { get; } = new();

    public string RegisterCommand(string name, CommandDefinition definition, ICommandProvider provider) => "cmd";

    public bool UnregisterCommand(string commandId) => true;

    public bool SendClientMessage(ClientTextMessageEvent message)
    {
        lock (Messages)
        {
            Messages.Add(message.Message);
        }

        return true;
    }
}

internal sealed class FakeFeedback : IFeedbackApi
{
    public List<(string Text, FeedbackKind Kind)> Toasts { get; } = new();

    public void Show(string text, FeedbackKind kind = FeedbackKind.Info, int durationMs = 2000)
    {
        lock (Toasts)
        {
            Toasts.Add((text, kind));
        }
    }
}
