using System;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Feedback;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Carries out what a controller does to one flight from its tag or list.
/// </summary>
/// <remarks>
/// When this client is master of the departure airport, or the airport has none, the change is written to vIFF at once.
/// When another controller is master, it is sent as a request (REQTOBT, REQASRT) that their plugin applies, so an
/// EuroScope master hears it too. Each method returns at once; the network work runs on the scheduler.
/// </remarks>
internal sealed class FlightActions
{
    private readonly IViffApi _api;
    private readonly CdmStore _store;
    private readonly ConnectionGate _gate;
    private readonly JobScheduler _scheduler;
    private readonly Notifier _notifier;
    private readonly TimeProvider _clock;
    private readonly Action _changed;

    public FlightActions(
        IViffApi api,
        CdmStore store,
        ConnectionGate gate,
        JobScheduler scheduler,
        Notifier notifier,
        TimeProvider clock,
        Action changed)
    {
        _api = api;
        _store = store;
        _gate = gate;
        _scheduler = scheduler;
        _notifier = notifier;
        _clock = clock;
        _changed = changed;
    }

    /// <summary>
    /// Sets the TOBT (the EOBT at a non-CDM airport) to now, and sends REA when the flight has a CTOT.
    /// </summary>
    public void ReadyTobt(string callsign)
    {
        if (!TryBegin(callsign, out DepartureRecord record, out AirportView airport, out bool direct))
        {
            return;
        }

        string now = CdmTime.ToHhmm(_clock.GetUtcNow());
        bool rea = record.EffectiveCtot.Length > 0;
        if (!airport.IsCdm)
        {
            Run($"Ready EOBT {callsign}", ct => SendAllAsync(callsign, ct, $"EOBT/{now}", rea ? "REA/1" : null), null);
        }
        else if (direct)
        {
            CdmDataUpdate update = CdmDataUpdate.From(record) with
            {
                Tobt = now + "00",
                Tsat = string.Empty,
                Ttot = string.Empty,
                Asrt = record.Cdm.Asrt.Length > 0 ? record.Cdm.Asrt : now,
            };
            Run($"Ready TOBT {callsign}", ct => WriteAsync(update, ct, rea ? "REA/1" : null), () =>
            {
                _store.Update(callsign, update.ApplyTo);
                _store.NoteTobtSource(callsign, "A", update.Tobt);
            });
        }
        else
        {
            Run($"Ready TOBT {callsign}", ct => SendAllAsync(callsign, ct, $"REQTOBT/{now}/ATC", rea ? "REA/1" : null), null);
        }
    }

    /// <summary>
    /// Sets the TOBT (the EOBT at a non-CDM airport) to a time the controller typed, "HHMM"; empty clears the TOBT.
    /// </summary>
    public void EditTobt(string callsign, string? input)
    {
        string text = (input ?? string.Empty).Trim();
        if (text.Length > 0 && (text.Length != 4 || CdmTime.ParseTimeOfDay(text) is null))
        {
            _notifier.Toast($"{text} is not a time; enter four digits, HHMM UTC.", FeedbackKind.Warning);
            return;
        }

        if (!TryBegin(callsign, out DepartureRecord record, out AirportView airport, out bool direct))
        {
            return;
        }

        if (!airport.IsCdm)
        {
            if (text.Length == 0)
            {
                _notifier.Toast("Enter the new EOBT as HHMM.", FeedbackKind.Warning);
                return;
            }

            Run($"EOBT {text} {callsign}", ct => SendAllAsync(callsign, ct, $"EOBT/{text}"), null);
        }
        else if (direct)
        {
            CdmDataUpdate update = CdmDataUpdate.From(record) with
            {
                Tobt = text.Length == 0 ? string.Empty : text + "00",
                Tsat = string.Empty,
                Ttot = string.Empty,
            };
            Run($"TOBT {(text.Length == 0 ? "cleared" : text)} {callsign}", ct => WriteAsync(update, ct), () =>
            {
                _store.Update(callsign, update.ApplyTo);
                if (text.Length > 0)
                {
                    _store.NoteTobtSource(callsign, "A", update.Tobt);
                }
            });
        }
        else if (text.Length == 0)
        {
            _notifier.Toast($"Only the master ({airport.MasterPosition}) can clear a TOBT.", FeedbackKind.Warning);
        }
        else
        {
            Run($"TOBT {text} {callsign}", ct => SendAllAsync(callsign, ct, $"REQTOBT/{text}/ATC"), null);
        }
    }

    /// <summary>
    /// Sets RDY (the ASRT) to now, or clears it when set; <paramref name="withRea"/> also sends REA when setting it.
    /// </summary>
    public void ToggleReady(string callsign, bool withRea)
    {
        if (!TryBegin(callsign, out DepartureRecord record, out AirportView airport, out bool direct))
        {
            return;
        }

        bool setting = record.Cdm.Asrt.Length == 0;
        string now = CdmTime.ToHhmm(_clock.GetUtcNow());
        string? rea = setting && withRea ? "REA/1" : null;
        if (direct)
        {
            CdmDataUpdate update = CdmDataUpdate.From(record) with { Asrt = setting ? now : string.Empty };
            Run($"RDY {(setting ? "set" : "cleared")} {callsign}", ct => WriteAsync(update, ct, rea), () => _store.Update(callsign, update.ApplyTo));
        }
        else if (!setting)
        {
            _notifier.Toast($"Only the master ({airport.MasterPosition}) can clear RDY.", FeedbackKind.Warning);
        }
        else
        {
            Run($"RDY {callsign}", ct => SendAllAsync(callsign, ct, $"REQASRT/{now}", rea), null);
        }
    }

    /// <summary>
    /// Sends or withdraws a Ready (REA) message, asking ATFCM for an earlier CTOT.
    /// </summary>
    public void SetRea(string callsign, bool ready)
    {
        if (TryBegin(callsign, out _, out _, out _))
        {
            Run($"REA {(ready ? "sent" : "cancelled")} {callsign}", ct => SendAllAsync(callsign, ct, ready ? "REA/1" : "REA/0"), null);
        }
    }

    /// <summary>
    /// Sets the de-icing choice: empty for none, STND, or a remote pad's name.
    /// </summary>
    public void SetDeIce(string callsign, string label)
    {
        if (!TryBeginMasterOnly(callsign, "de-icing", out DepartureRecord record))
        {
            return;
        }

        CdmDataUpdate update = CdmDataUpdate.From(record) with { Deice = DeIce.Stored(label) };
        Run($"DEICE {(label.Length == 0 ? "none" : label)} {callsign}", ct => WriteAsync(update, ct), () => _store.Update(callsign, update.ApplyTo));
    }

    /// <summary>
    /// Sets a manual CTOT, "HHMM", that the sequencer holds the take-off to; empty removes it.
    /// </summary>
    public void SetManualCtot(string callsign, string? input)
    {
        string text = (input ?? string.Empty).Trim();
        if (text.Length > 0 && (text.Length != 4 || CdmTime.ParseTimeOfDay(text) is null))
        {
            _notifier.Toast($"{text} is not a time; enter four digits, HHMM UTC.", FeedbackKind.Warning);
            return;
        }

        if (!TryBeginMasterOnly(callsign, "a manual CTOT", out DepartureRecord record))
        {
            return;
        }

        if (record.Ctot.Length > 0)
        {
            _notifier.Toast($"{callsign} has an ATFCM CTOT; submit a new TOBT to have it revised.", FeedbackKind.Warning);
            return;
        }

        CdmDataUpdate update = CdmDataUpdate.From(record) with
        {
            Ctot = text,
            Reason = text.Length == 0 ? string.Empty : CdmDataUpdate.ManualReason,
            Tsat = string.Empty,
            Ttot = string.Empty,
        };
        Run($"Manual CTOT {(text.Length == 0 ? "removed" : text)} {callsign}", ct => WriteAsync(update, ct), () => _store.Update(callsign, update.ApplyTo));
    }

    private bool TryBegin(string callsign, out DepartureRecord record, out AirportView airport, out bool direct)
    {
        record = null!;
        airport = null!;
        direct = false;
        if (!_gate.CanWrite(out string me, out string reason))
        {
            _notifier.Toast(reason, FeedbackKind.Warning);
            return false;
        }

        DepartureRecord? found = _store.Find(callsign);
        if (found is null)
        {
            _notifier.Toast($"{callsign} is not a departure vIFF knows at a watched airport.", FeedbackKind.Warning);
            return false;
        }

        record = found;
        airport = _store.Airport(found.Departure);
        direct = !airport.HasMaster || string.Equals(airport.MasterPosition, me, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private bool TryBeginMasterOnly(string callsign, string what, out DepartureRecord record)
    {
        if (!TryBegin(callsign, out record, out AirportView airport, out bool direct))
        {
            return false;
        }

        if (!direct)
        {
            _notifier.Toast($"Only the master ({airport.MasterPosition}) can set {what}.", FeedbackKind.Warning);
            return false;
        }

        return true;
    }

    private async Task<bool> WriteAsync(CdmDataUpdate update, CancellationToken cancellationToken, string? thenDpi = null)
    {
        if (!await _api.SetCdmDataAsync(update, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return thenDpi is null || await _api.SendDpiAsync(update.Callsign, thenDpi, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendAllAsync(string callsign, CancellationToken cancellationToken, params string?[] values)
    {
        foreach (string? value in values)
        {
            if (value is not null && !await _api.SendDpiAsync(callsign, value, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private void Run(string what, Func<CancellationToken, Task<bool>> work, Action? applied)
    {
        _scheduler.RunOnce(what, async ct =>
        {
            if (await work(ct).ConfigureAwait(false))
            {
                applied?.Invoke();
                _notifier.Toast(what, FeedbackKind.Success);
                _changed();
            }
            else
            {
                _notifier.Toast($"vIFF did not accept: {what}", FeedbackKind.Error);
            }
        });
    }
}
