using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Feedback;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Claims and releases the CDM master role of airports on vIFF, which allows one master per airport.
/// </summary>
internal sealed class MasterRole
{
    private readonly IViffApi _api;
    private readonly CdmStore _store;
    private readonly CdmSync _sync;
    private readonly ConnectionGate _gate;
    private readonly JobScheduler _scheduler;
    private readonly Notifier _notifier;
    private readonly Action _changed;

    // Guards _pending and _lastCallsign; met by the command, window, event and pool threads.
    private readonly object _lock = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private string _lastCallsign = string.Empty;

    public MasterRole(IViffApi api, CdmStore store, CdmSync sync, ConnectionGate gate, JobScheduler scheduler, Notifier notifier, Action changed)
    {
        _api = api;
        _store = store;
        _sync = sync;
        _gate = gate;
        _scheduler = scheduler;
        _notifier = notifier;
        _changed = changed;
    }

    /// <summary>
    /// Returns whether a claim or release of the airport is in flight, for the panel's Connecting state.
    /// </summary>
    public bool IsPending(string icao)
    {
        lock (_lock)
        {
            return _pending.Contains(icao);
        }
    }

    /// <summary>
    /// Remembers the connected callsign, so the master role can be released after the connection drops.
    /// </summary>
    public void NoteConnected()
    {
        string callsign = _gate.Callsign;
        if (callsign.Length > 0)
        {
            lock (_lock)
            {
                _lastCallsign = callsign;
            }
        }
    }

    public void Claim(string icao)
    {
        icao = icao.ToUpperInvariant();
        if (!_gate.CanWrite(out string me, out string reason))
        {
            _notifier.Say(reason);
            return;
        }

        AirportView airport = _store.Airport(icao);
        if (airport.HasMaster)
        {
            _notifier.Say(string.Equals(airport.MasterPosition, me, StringComparison.OrdinalIgnoreCase)
                ? $"You are already CDM master of {icao}."
                : $"{airport.MasterPosition} is CDM master of {icao}. Wait until they enter .cdm slave.");
            return;
        }

        if (!BeginPending(icao))
        {
            return;
        }

        NoteConnected();
        _sync.Watch(icao);
        _changed();
        _scheduler.RunOnce($"claim {icao}", async ct =>
        {
            try
            {
                AirportCapacity? capacity = airport.Capacity ?? await _api.GetCapacityAsync(icao, ct).ConfigureAwait(false);
                if (capacity is not null && !capacity.IsCdm)
                {
                    _notifier.Say($"{icao} is not a CDM airport on vIFF; there is no sequence to master.");
                    return;
                }

                if (await _api.SetMasterAsync(icao, me, ct).ConfigureAwait(false))
                {
                    _store.SetMaster(icao, me);
                    _notifier.Say($"Connected as CDM master of {icao}.");
                    _notifier.Toast($"CDM master of {icao}", FeedbackKind.Success);
                }
                else if (_api.KeyRejected)
                {
                    _notifier.Say(KeyRejectedMessage, urgent: true);
                }
                else
                {
                    _notifier.Say($"vIFF refused the master role of {icao}; another controller may hold it.", urgent: true);
                }
            }
            finally
            {
                EndPending(icao);
                _changed();
            }
        });
    }

    /// <summary>
    /// Releases the master role of one airport, or of every airport held when <paramref name="icao"/> is empty.
    /// </summary>
    public void Release(string icao)
    {
        string me = _gate.Callsign;
        IReadOnlyList<string> held = _store.AirportsMasteredBy(me);
        List<string> targets = icao.Length == 0
            ? held.ToList()
            : held.Where(a => string.Equals(a, icao, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0)
        {
            _notifier.Say(icao.Length == 0 ? "You are not CDM master of any airport." : $"You are not CDM master of {icao.ToUpperInvariant()}.");
            return;
        }

        foreach (string airport in targets)
        {
            if (!BeginPending(airport))
            {
                continue;
            }

            _changed();
            _scheduler.RunOnce($"release {airport}", async ct =>
            {
                try
                {
                    await ReleaseOneAsync(airport, me, ct).ConfigureAwait(false);
                }
                finally
                {
                    EndPending(airport);
                    _changed();
                }
            });
        }
    }

    /// <summary>
    /// Releases every master role held, waiting at most <paramref name="timeout"/>; used at shutdown and on disconnect.
    /// </summary>
    public void ReleaseAll(TimeSpan timeout)
    {
        string me;
        lock (_lock)
        {
            me = _lastCallsign;
        }

        using CancellationTokenSource cancel = new(timeout);
        try
        {
            ReleaseAllAsync(me, cancel.Token).Wait(timeout);
        }
        catch (AggregateException)
        {
            // A release that timed out is logged by the client; vIFF also drops masters that leave the network.
        }
    }

    /// <summary>
    /// Releases every master role held, stopping when <paramref name="cancellationToken"/> is cancelled; used on disconnect.
    /// </summary>
    public Task ReleaseAllAsync(CancellationToken cancellationToken)
    {
        string me;
        lock (_lock)
        {
            me = _lastCallsign;
        }

        return ReleaseAllAsync(me, cancellationToken);
    }

    private Task ReleaseAllAsync(string me, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> held = me.Length == 0 ? Array.Empty<string>() : _store.AirportsMasteredBy(me);
        return Task.WhenAll(held.Select(a => ReleaseOneAsync(a, me, cancellationToken)));
    }

    private async Task ReleaseOneAsync(string icao, string me, CancellationToken cancellationToken)
    {
        if (await _api.RemoveMasterAsync(icao, me, cancellationToken).ConfigureAwait(false))
        {
            _store.SetMaster(icao, null);
            _notifier.Say($"Released the CDM master role of {icao}.");
        }
        else
        {
            _notifier.Say(_api.KeyRejected ? KeyRejectedMessage : $"vIFF did not release the master role of {icao}; try .cdm slave {icao} again.", urgent: true);
        }
    }

    /// <summary>
    /// Gets what to tell the controller when vIFF refuses the API key.
    /// </summary>
    public const string KeyRejectedMessage =
        "vIFF refused the API key, so nothing can be written (master, TOBT, RDY). Set the vIFF key with .cdm apikey <key>.";

    private bool BeginPending(string icao)
    {
        lock (_lock)
        {
            return _pending.Add(icao);
        }
    }

    private void EndPending(string icao)
    {
        lock (_lock)
        {
            _pending.Remove(icao);
        }
    }
}
