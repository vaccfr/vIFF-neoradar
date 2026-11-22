using System;
using System.Collections.Generic;
using NeoRadar.PluginSdk.ControllerData;
using NeoRadar.PluginSdk.Logger;

namespace Viff.NeoRadar.Strips;

/// <summary>
/// Writes strip box 0, one aircraft per call to <see cref="Tick"/>, as the client allows one strip write a second.
/// </summary>
/// <remarks>
/// A change is folded into what is already waiting for that aircraft, so only the latest strip is written. A
/// controller's own change goes ahead of the master's background copy of the vIFF times.
/// </remarks>
internal sealed class StripWriter
{
    private const int MaxAttempts = 5;

    private readonly StripStore _store;
    private readonly IControllerDataApi _controllerData;
    private readonly ILoggerApi _logger;

    // Guards _pending, _queue and _attempts; action, sync and tag threads meet here.
    private readonly object _lock = new();
    private readonly Dictionary<string, StripData> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _queue = new();
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public StripWriter(StripStore store, IControllerDataApi controllerData, ILoggerApi logger)
    {
        _store = store;
        _controllerData = controllerData;
        _logger = logger;
    }

    /// <summary>
    /// Gets the strip as it will be once what is waiting is written.
    /// </summary>
    public StripData Current(string callsign)
    {
        lock (_lock)
        {
            return _pending.TryGetValue(callsign, out StripData? pending) ? pending : _store.Get(callsign);
        }
    }

    /// <summary>
    /// Queues a change to an aircraft's strip; <paramref name="urgent"/> writes it before background copies.
    /// </summary>
    public void Change(string callsign, Func<StripData, StripData> change, bool urgent)
    {
        lock (_lock)
        {
            StripData basis = _pending.TryGetValue(callsign, out StripData? pending) ? pending : _store.Get(callsign);
            StripData wanted = change(basis);
            if (wanted.Equals(_store.Get(callsign)))
            {
                _pending.Remove(callsign);
                _queue.Remove(callsign);
                return;
            }

            _pending[callsign] = wanted;
            if (urgent)
            {
                _queue.Remove(callsign);
                _queue.AddFirst(callsign);
            }
            else if (!_queue.Contains(callsign))
            {
                _queue.AddLast(callsign);
            }
        }
    }

    /// <summary>
    /// Writes the next waiting strip, and returns whether one was written.
    /// </summary>
    public bool Tick()
    {
        string callsign;
        StripData wanted;
        lock (_lock)
        {
            if (_queue.First is null)
            {
                return false;
            }

            callsign = _queue.First.Value;
            _queue.RemoveFirst();
            wanted = _pending[callsign];
        }

        bool written = _controllerData.SetFlightStripAnnotation(callsign, 0, wanted.Format());
        lock (_lock)
        {
            if (written)
            {
                _store.Set(callsign, wanted);
                _attempts.Remove(callsign);
                if (_pending.TryGetValue(callsign, out StripData? latest) && latest.Equals(wanted))
                {
                    _pending.Remove(callsign);
                }
                else if (_pending.ContainsKey(callsign))
                {
                    _queue.AddLast(callsign);
                }

                return true;
            }

            int attempts = _attempts.GetValueOrDefault(callsign) + 1;
            if (attempts >= MaxAttempts)
            {
                _attempts.Remove(callsign);
                _pending.Remove(callsign);
                _logger.Debug($"Strip of {callsign} not written after {attempts} attempts; the client refused it");
            }
            else
            {
                _attempts[callsign] = attempts;
                _queue.AddLast(callsign);
            }

            return false;
        }
    }

    /// <summary>
    /// Gets how many strips wait to be written.
    /// </summary>
    public int Waiting
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }
}
