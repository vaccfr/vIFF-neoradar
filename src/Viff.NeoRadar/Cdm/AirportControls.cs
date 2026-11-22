using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NeoRadar.PluginSdk.Settings;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Holds the controller's own sequencing controls: LVO, rate overrides, runway delays, de-icing pads and scratchpad options.
/// </summary>
/// <remarks>
/// Rates and pads persist in the plugin settings; LVO, delays and scratchpad options last for the session, as in the
/// EuroScope plugin. Every member is safe from any thread.
/// </remarks>
internal sealed class AirportControls
{
    private readonly ISettingsApi _settings;

    // Guards every field below; met by the command, window, tag and pool threads.
    private readonly object _lock = new();
    private readonly HashSet<string> _lvo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _startupDelays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _departureDelays = new(StringComparer.OrdinalIgnoreCase);
    private bool _tsatRemarks;
    private bool _ctotRemarks;

    public AirportControls(ISettingsApi settings)
    {
        _settings = settings;
    }

    public bool TsatRemarks
    {
        get { lock (_lock) { return _tsatRemarks; } }
    }

    public bool CtotRemarks
    {
        get { lock (_lock) { return _ctotRemarks; } }
    }

    public bool ToggleTsatRemarks()
    {
        lock (_lock)
        {
            _tsatRemarks = !_tsatRemarks;
            return _tsatRemarks;
        }
    }

    public bool ToggleCtotRemarks()
    {
        lock (_lock)
        {
            _ctotRemarks = !_ctotRemarks;
            return _ctotRemarks;
        }
    }

    public bool IsLvo(string icao)
    {
        lock (_lock)
        {
            return _lvo.Contains(icao);
        }
    }

    /// <summary>
    /// Switches LVO for an airport and returns the new state.
    /// </summary>
    public bool ToggleLvo(string icao)
    {
        lock (_lock)
        {
            if (_lvo.Remove(icao))
            {
                return false;
            }

            _lvo.Add(icao);
            return true;
        }
    }

    /// <summary>
    /// Stores rate overrides per hour for an airport; 0 clears one, so vIFF's rate applies again.
    /// </summary>
    public void SetRates(string icao, int normal, int lvo) =>
        _settings.Set(RateKey(icao), string.Create(CultureInfo.InvariantCulture, $"{normal}/{lvo}"));

    /// <summary>
    /// Gets the rate overrides per hour for an airport, 0 where none is set.
    /// </summary>
    public (int Normal, int Lvo) GetRates(string icao)
    {
        string? stored = _settings.Get(RateKey(icao));
        if (string.IsNullOrEmpty(stored))
        {
            return (0, 0);
        }

        string[] parts = stored.Split('/');
        return (ParseOrZero(parts[0]), parts.Length > 1 ? ParseOrZero(parts[1]) : 0);
    }

    /// <summary>
    /// Gets the departure rate to sequence with, or 0 when none is known.
    /// </summary>
    /// <param name="icao">The airport.</param>
    /// <param name="viffRate">The rate vIFF's CAD holds, or 0.</param>
    /// <param name="lvoRateMissing">True when LVO is on and no LVO rate is set, so the normal rate is used.</param>
    public int EffectiveRate(string icao, int viffRate, out bool lvoRateMissing)
    {
        (int normal, int lvo) = GetRates(icao);
        int normalRate = normal > 0 ? normal : viffRate;
        lvoRateMissing = IsLvo(icao) && lvo <= 0;
        return IsLvo(icao) && lvo > 0 ? lvo : normalRate;
    }

    /// <summary>
    /// Sets the earliest TSAT for a runway, or clears it with null.
    /// </summary>
    public void SetStartupDelay(string icao, string runway, DateTimeOffset? earliest) =>
        SetDelay(_startupDelays, icao, runway, earliest);

    /// <summary>
    /// Sets the earliest TTOT for a runway, or clears it with null.
    /// </summary>
    public void SetDepartureDelay(string icao, string runway, DateTimeOffset? earliest) =>
        SetDelay(_departureDelays, icao, runway, earliest);

    /// <summary>
    /// Gets the earliest TSAT set per runway of an airport.
    /// </summary>
    public IReadOnlyDictionary<string, DateTimeOffset> StartupDelays(string icao) => DelaysOf(_startupDelays, icao);

    /// <summary>
    /// Gets the earliest TTOT set per runway of an airport.
    /// </summary>
    public IReadOnlyDictionary<string, DateTimeOffset> DepartureDelays(string icao) => DelaysOf(_departureDelays, icao);

    /// <summary>
    /// Stores an airport's remote de-icing pads, such as "REM1=10,REM2=8", in minutes of extra taxi.
    /// </summary>
    public bool SetDeIcePads(string icao, string definition)
    {
        if (ParsePads(definition) is null)
        {
            return false;
        }

        _settings.Set(PadsKey(icao), definition);
        return true;
    }

    /// <summary>
    /// Gets an airport's remote de-icing pads and their extra taxi minutes, in the order they were defined.
    /// </summary>
    public IReadOnlyList<(string Name, int ExtraTaxiMinutes)> DeIcePads(string icao) =>
        ParsePads(_settings.Get(PadsKey(icao)) ?? string.Empty) ?? Array.Empty<(string, int)>();

    /// <summary>
    /// Parses a pad list such as "REM1=10,REM2"; a pad with no minutes adds none. Null when it is malformed.
    /// </summary>
    public static IReadOnlyList<(string Name, int ExtraTaxiMinutes)>? ParsePads(string definition)
    {
        List<(string, int)> pads = new();
        foreach (string entry in definition.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split('=', StringSplitOptions.TrimEntries);
            string name = parts[0].ToUpperInvariant();
            if (name.Length == 0 || name.Length > 8 || name.Any(c => !char.IsLetterOrDigit(c)) || name == DeIce.StandLabel)
            {
                return null;
            }

            int minutes = 0;
            if (parts.Length > 2 ||
                (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes > 60)))
            {
                return null;
            }

            pads.Add((name, minutes));
        }

        return pads;
    }

    private void SetDelay(Dictionary<string, DateTimeOffset> delays, string icao, string runway, DateTimeOffset? earliest)
    {
        string key = DelayKey(icao, runway);
        lock (_lock)
        {
            if (earliest is null)
            {
                delays.Remove(key);
            }
            else
            {
                delays[key] = earliest.Value;
            }
        }
    }

    private IReadOnlyDictionary<string, DateTimeOffset> DelaysOf(Dictionary<string, DateTimeOffset> delays, string icao)
    {
        string prefix = icao.ToUpperInvariant() + "/";
        Dictionary<string, DateTimeOffset> byRunway = new(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (KeyValuePair<string, DateTimeOffset> delay in delays)
            {
                if (delay.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    byRunway[delay.Key[prefix.Length..]] = delay.Value;
                }
            }
        }

        return byRunway;
    }

    private static string DelayKey(string icao, string runway) => $"{icao.ToUpperInvariant()}/{runway.ToUpperInvariant()}";

    private static string RateKey(string icao) => "rate." + icao.ToUpperInvariant();

    private static string PadsKey(string icao) => "deicePads." + icao.ToUpperInvariant();

    private static int ParseOrZero(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;
}
