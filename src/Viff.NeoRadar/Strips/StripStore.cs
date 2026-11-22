using System;
using System.Collections.Generic;
using System.Linq;
using NeoRadar.PluginSdk.ControllerData;

namespace Viff.NeoRadar.Strips;

/// <summary>
/// Holds each aircraft's strip box 0 as last read from the client, so tags never read the host per frame.
/// </summary>
internal sealed class StripStore
{
    private readonly IControllerDataApi _controllerData;

    // Guards _strips; strip events, the sync job and the tag job meet here.
    private readonly object _lock = new();
    private readonly Dictionary<string, StripData> _strips = new(StringComparer.OrdinalIgnoreCase);

    public StripStore(IControllerDataApi controllerData)
    {
        _controllerData = controllerData;
    }

    public StripData Get(string callsign)
    {
        lock (_lock)
        {
            return _strips.TryGetValue(callsign, out StripData? strip) ? strip : StripData.Empty;
        }
    }

    /// <summary>
    /// Reads the aircraft's strip from the client, as after an annotation changed or a strip was pushed.
    /// </summary>
    public void Refresh(string callsign)
    {
        IReadOnlyList<string>? boxes = _controllerData.GetFlightStripAnnotations(callsign);
        StripData strip = StripData.Parse(boxes is { Count: > 0 } ? boxes[0] : null);
        lock (_lock)
        {
            _strips[callsign] = strip;
        }
    }

    /// <summary>
    /// Reads the strips of the aircraft not read yet, and forgets the others.
    /// </summary>
    public void Track(IEnumerable<string> callsigns)
    {
        HashSet<string> wanted = new(callsigns, StringComparer.OrdinalIgnoreCase);
        List<string> missing;
        lock (_lock)
        {
            foreach (string gone in _strips.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                _strips.Remove(gone);
            }

            missing = wanted.Where(c => !_strips.ContainsKey(c)).ToList();
        }

        foreach (string callsign in missing)
        {
            Refresh(callsign);
        }
    }

    /// <summary>
    /// Records a strip this client wrote.
    /// </summary>
    public void Set(string callsign, StripData strip)
    {
        lock (_lock)
        {
            _strips[callsign] = strip;
        }
    }
}
