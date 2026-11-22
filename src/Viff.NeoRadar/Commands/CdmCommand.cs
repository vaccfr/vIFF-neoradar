using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NeoRadar.PluginSdk.Chat;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Panel;

namespace Viff.NeoRadar.Commands;

/// <summary>
/// The <c>.cdm</c> chat command, with the subcommands of the French vACC CDM documentation.
/// </summary>
internal sealed class CdmCommand : ICommandProvider
{
    private static readonly string[] s_subcommands =
    {
        "master", "slave", "startupdelay", "departuredelay", "lvo", "rate", "deicepads",
        "remarks", "rmkctot", "recover", "refresh", "panel", "status", "help",
    };

    private static readonly string[] s_help =
    {
        ".cdm master ICAO - connect as CDM master of an airport",
        ".cdm slave [ICAO] - release the master role (every airport when none is given)",
        ".cdm startupdelay ICAO/RWY HHMM - no TSAT before that time (MM for now + minutes, 9999 removes it)",
        ".cdm departuredelay ICAO/RWY HHMM - no TTOT before that time (MM for now + minutes, 9999 removes it)",
        ".cdm lvo [ICAO] - switch low-visibility operations, and so the LVO rate, on or off",
        ".cdm rate ICAO NORMAL [LVO] - override vIFF's departure rate per hour (0 returns to vIFF's)",
        ".cdm deicepads ICAO REM1=10,REM2=8 - remote de-icing pads and their extra taxi minutes",
        ".cdm remarks / .cdm rmkctot - write the TSAT / CTOT (.CHHMM) into the scratchpad",
        ".cdm recover ICAO - read the airport's CDM data from vIFF again",
        ".cdm panel - open the CDM Airport Panel",
        ".cdm status - show the master of each watched airport",
    };

    private readonly CdmStore _store;
    private readonly CdmSync _sync;
    private readonly AirportControls _controls;
    private readonly MasterRole _master;
    private readonly AirportPanel _panel;
    private readonly ConnectionGate _gate;
    private readonly Notifier _notifier;
    private readonly TimeProvider _clock;
    private readonly Action _requestSync;

    public CdmCommand(
        CdmStore store,
        CdmSync sync,
        AirportControls controls,
        MasterRole master,
        AirportPanel panel,
        ConnectionGate gate,
        Notifier notifier,
        TimeProvider clock,
        Action requestSync)
    {
        _store = store;
        _sync = sync;
        _controls = controls;
        _master = master;
        _panel = panel;
        _gate = gate;
        _notifier = notifier;
        _clock = clock;
        _requestSync = requestSync;
    }

    public static CommandDefinition Definition { get; } = new(
        "vIFF CDM: master, slave, startupdelay, departuredelay, lvo, rate, deicepads, remarks, rmkctot, recover, refresh, panel, status, help",
        new[]
        {
            new CommandParameter("action", ParameterType.String, Required: true, AllowedValues: s_subcommands),
            new CommandParameter("airport", ParameterType.String, MaxLength: 12),
            new CommandParameter("value", ParameterType.String, MaxLength: 64),
            new CommandParameter("lvo", ParameterType.String, MaxLength: 4),
        });

    // Client threads.
    public CommandResult Execute(string commandId, IReadOnlyList<string> args)
    {
        string action = args.Count > 0 ? args[0].ToLowerInvariant() : "help";
        string first = args.Count > 1 ? args[1].Trim().ToUpperInvariant() : string.Empty;
        string second = args.Count > 2 ? args[2].Trim() : string.Empty;
        string third = args.Count > 3 ? args[3].Trim() : string.Empty;

        switch (action)
        {
            case "master":
                if (!IsIcao(first))
                {
                    return Fail("Usage: .cdm master ICAO");
                }

                _master.Claim(first);
                return Ok($"Connecting as CDM master of {first}...");

            case "slave":
                if (first.Length > 0 && !IsIcao(first))
                {
                    return Fail("Usage: .cdm slave [ICAO]");
                }

                _master.Release(first);
                return Ok(null);

            case "startupdelay":
            case "departuredelay":
                return SetDelay(action == "startupdelay", first, second);

            case "lvo":
                return ToggleLvo(first);

            case "rate":
                return SetRate(first, second, third);

            case "deicepads":
                if (!IsIcao(first) || !_controls.SetDeIcePads(first, second))
                {
                    return Fail("Usage: .cdm deicepads ICAO REM1=10,REM2=8 (pad names of letters and digits, minutes 0 to 60)");
                }

                _requestSync();
                return Ok($"De-icing pads for {first}: {(second.Length == 0 ? "none" : second.ToUpperInvariant())}.");

            case "remarks":
                return Ok($"TSAT scratchpad remarks {(_controls.ToggleTsatRemarks() ? "on" : "off")} for airports you are master of.");

            case "rmkctot":
                return Ok($"CTOT scratchpad remarks {(_controls.ToggleCtotRemarks() ? "on" : "off")} for airports you are master of.");

            case "recover":
                if (first.Length > 0 && !IsIcao(first))
                {
                    return Fail("Usage: .cdm recover ICAO");
                }

                if (first.Length > 0)
                {
                    _sync.Watch(first);
                }

                _requestSync();
                return Ok("CDM data is always read from vIFF; reading it again now.");

            case "refresh":
                _requestSync();
                return Ok("Reading vIFF now.");

            case "panel":
                _panel.Open();
                return Ok(null);

            case "status":
                SayStatus();
                return Ok(null);

            default:
                foreach (string line in s_help)
                {
                    _notifier.Say(line);
                }

                return Ok(null);
        }
    }

    private CommandResult SetDelay(bool startUp, string airportRunway, string time)
    {
        string usage = $"Usage: .cdm {(startUp ? "startupdelay" : "departuredelay")} ICAO/RWY HHMM";
        string[] parts = airportRunway.Split('/');
        if (parts.Length != 2 || !IsIcao(parts[0]) || parts[1].Length == 0)
        {
            return Fail(usage);
        }

        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset? earliest;
        if (time == "9999")
        {
            earliest = null;
        }
        else if (time.Length is 1 or 2 && int.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
        {
            earliest = CdmTime.CeilToMinute(now.AddMinutes(minutes));
        }
        else if (time.Length == 4 && CdmTime.ParseTimeOfDay(time) is { } timeOfDay)
        {
            earliest = CdmTime.ResolveNear(timeOfDay, now);
        }
        else
        {
            return Fail(usage);
        }

        if (startUp)
        {
            _controls.SetStartupDelay(parts[0], parts[1], earliest);
        }
        else
        {
            _controls.SetDepartureDelay(parts[0], parts[1], earliest);
        }

        _requestSync();
        string what = startUp ? "TSAT" : "TTOT";
        return Ok(earliest is null
            ? $"{airportRunway}: earliest {what} removed."
            : $"{airportRunway}: no {what} before {CdmTime.ToHhmm(earliest.Value)}Z.");
    }

    private CommandResult ToggleLvo(string icao)
    {
        IReadOnlyList<string> airports = icao.Length > 0 ? new[] { icao } : _store.AirportsMasteredBy(_gate.Callsign);
        if ((icao.Length > 0 && !IsIcao(icao)) || airports.Count == 0)
        {
            return Fail("Usage: .cdm lvo ICAO (or be master of an airport)");
        }

        List<string> states = airports.Select(a => $"{a} {(_controls.ToggleLvo(a) ? "on" : "off")}").ToList();
        _requestSync();
        return Ok("LVO " + string.Join(", ", states) + ". Check the runway configuration matches.");
    }

    private CommandResult SetRate(string icao, string normal, string lvo)
    {
        if (!IsIcao(icao) ||
            !int.TryParse(normal, NumberStyles.None, CultureInfo.InvariantCulture, out int normalRate) ||
            (lvo.Length > 0 && !int.TryParse(lvo, NumberStyles.None, CultureInfo.InvariantCulture, out _)) ||
            normalRate > 120)
        {
            return Fail("Usage: .cdm rate ICAO NORMAL [LVO], departures per hour, 0 returns to vIFF's rate");
        }

        int lvoRate = lvo.Length > 0 ? int.Parse(lvo, CultureInfo.InvariantCulture) : _controls.GetRates(icao).Lvo;
        _controls.SetRates(icao, normalRate, Math.Min(lvoRate, 120));
        _requestSync();
        return Ok($"{icao} rate {(normalRate > 0 ? normalRate + "/h" : "from vIFF")}, LVO {(lvoRate > 0 ? lvoRate + "/h" : "not set")}.");
    }

    private void SayStatus()
    {
        string me = _gate.Callsign;
        IReadOnlyList<AirportView> airports = _store.Airports(_sync.WatchedAirports());
        if (airports.Count == 0)
        {
            _notifier.Say("No airports watched. Add airports to your airport configuration, or use .cdm master ICAO.");
            return;
        }

        foreach (AirportView airport in airports)
        {
            string master = !airport.HasMaster ? "no master"
                : string.Equals(airport.MasterPosition, me, StringComparison.OrdinalIgnoreCase) ? "you are master"
                : $"master {airport.MasterPosition}";
            string cdm = airport.Capacity is null ? "vIFF data pending" : airport.IsCdm ? "CDM" : "not CDM";
            _notifier.Say($"{airport.Icao}: {cdm}, {master}{(_controls.IsLvo(airport.Icao) ? ", LVO" : string.Empty)}");
        }
    }

    private static bool IsIcao(string text) => text.Length == 4 && text.All(char.IsLetterOrDigit);

    private static CommandResult Ok(string? message) => new(true, message);

    private static CommandResult Fail(string message) => new(false, message);
}
