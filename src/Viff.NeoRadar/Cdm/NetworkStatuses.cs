using System;
using System.Collections.Generic;
using System.Globalization;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Holds every flight's network status and on-time deviation from vIFF, for the compliance items of airborne flights.
/// </summary>
internal sealed class NetworkStatuses
{
    // Guards both maps, swapped whole by the sync job and read by the tag job.
    private readonly object _lock = new();
    private IReadOnlyDictionary<string, string> _statuses = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _onTime = new Dictionary<string, string>();

    public void Replace(IReadOnlyDictionary<string, string>? statuses, IReadOnlyDictionary<string, string>? onTime)
    {
        lock (_lock)
        {
            _statuses = statuses ?? _statuses;
            _onTime = onTime ?? _onTime;
        }
    }

    /// <summary>
    /// Gets a flight's network status, such as COMPLY or AIRB, or empty.
    /// </summary>
    public string StatusOf(string callsign)
    {
        lock (_lock)
        {
            return _statuses.TryGetValue(callsign, out string? status) ? status.ToUpperInvariant() : string.Empty;
        }
    }

    /// <summary>
    /// Gets a flight's deviation from its CTOT or ETOT in whole minutes, positive when late, or null when vIFF has none.
    /// </summary>
    public int? OnTimeOf(string callsign)
    {
        string? text;
        lock (_lock)
        {
            if (!_onTime.TryGetValue(callsign, out text))
            {
                return null;
            }
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) && double.IsFinite(minutes)
            ? (int)Math.Round(minutes, MidpointRounding.AwayFromZero)
            : null;
    }
}
