# vIFF-neoradar

The [NeoRadar](https://docs.neoradar.app) port of the EuroScope [CDM plugin](https://github.com/rpuig2001/CDM),
backed by the [vIFF](https://viffsys.com) system.

It behaves as the CDM plugin does: the same times, colours, master and slave roles, commands and vIFF messages.
For what CDM does and how to work with it, read the [CDM plugin's documentation](https://github.com/rpuig2001/CDM#readme)
and your vACC's procedures. This page covers only what is specific to NeoRadar.

## Installing

Add `Viff.NeoRadar.dll` (from the latest CI run, or a local build) in Plugin Management, or list it in a package's
`plugins`. Then bind the items below in your package; `Package/` beside the DLL holds an example list and label items.

## What is different on NeoRadar

- **vIFF is the shared state.** Every controller reads their airports from vIFF every 15 seconds, and the display
  shows only what vIFF holds: a change appears once vIFF has stored it, normally within a second or two.
- **Strips carry the CDM plugin's box 0.** The plugin reads and writes flight strip box 0 in the CDM plugin's format
  (`ASRT/TSAC/TOBT/TSAT/TTOT/deIce/ecfmpId/manualCtot/CTOC/setBy/autoStartup/ATOT`), so TSAC, CTOC, TOBT source and
  automatic start-up travel with the strip between NeoRadar and EuroScope. The master keeps box 0 of its airports'
  flights in step with vIFF. NeoRadar writes one strip a second and pushes a strip on handoff; unlike the CDM plugin it
  does not push every strip to every controller, so a EuroScope controller gets the CDM data with the handoff.
- **Slave actions are requests.** A controller who is not master sends TOBT and start-up changes as the requests the
  CDM plugin's master applies (`REQTOBT`, `REQASRT`), so a NeoRadar slave works with a EuroScope master and the other
  way round. At an airport with no master, changes are written directly.
- **No configuration files.** Departure rates, taxi times and the list of CDM airports come from vIFF's CAD; runways,
  SIDs, ground status and wake categories come from NeoRadar. `.cdm rate` and `.cdm deicepads` override what vIFF
  lacks (LVO rates, remote de-icing pads).
- **Suspensions are read back from vIFF.** An `FLS-CDM` suspension the master sets (`SUSP`) is taken from vIFF, so it
  survives a master restart or handover.
- **CTOTs of every flight.** vIFF's network-wide CTOT list is read each cycle, so any flight the client holds shows
  its CTOT and regulation, and a departure always gets its CTOT even if its airport's data lags. Departures from other
  airports with a vIFF master show their TOBT, TSAT and TTOT too, read once a minute; they get no menus.
- **CDM Airport Panel** is a compact NeoRadar window, one chip per CDM airport: **CDM Airport Panel** in the Insets &
  Plugin Windows menu, or `.cdm panel`. Left click connects or releases master, right click switches LVO.
- **ATFCM Flight List** is a NeoRadar window too (`.cdm atfcm`, or the menu): vIFF's regulated, suspended and re-slotted
  flights, filtered by a prefix (by default your callsign's first two letters); double click selects the aircraft.
- **Writes are gated.** Nothing is written to vIFF unless you are connected as ATC on the live network.

## Items

Every id is prefixed `plugin:vIFF CDM:`.

| Tag item | CDM plugin column | Actions |
|---|---|---|
| `TAG_TOBT` | E/TOBT (EOBT at a non-CDM airport) | `ACTION_TOBT Menu`, `ACTION_TOBT Ready`, `ACTION_TOBT Edit` (input) |
| `TAG_BY` | TOBT set by | |
| `TAG_TSAT`, `TAG_TTG` | TSAT, TSAT countdown | |
| `TAG_TTOT` | TTOT | `ACTION_TTOT Menu` |
| `TAG_CTOT`, `TAG_REG` | CTOT, flow measure | `ACTION_CTOT Menu` |
| `TAG_ETFMS` | Network status | `ACTION_ETFMS Menu` |
| `TAG_RDY`, `TAG_ASRT` | Ready start-up, ASRT | `ACTION_RDY`, `ACTION_RDY REA` |
| `TAG_ASAT` | ASAT | |
| `TAG_DEICE` | De-ice | `ACTION_DEICE Menu` |
| `TAG_MSG` | Send status | |
| `TAG_EOBT` | EOBT | |
| `TAG_TSAC` | TSAC | `ACTION_TSAC` (TSAC = TSAT), `ACTION_TSAC Menu` |
| `TAG_CTOC` | CTOC | `ACTION_CTOC` (CTOC = CTOT), `ACTION_CTOC Menu` |
| `TAG_E` | E (P, C, I) | |
| `TAG_DLY` | TSAT minus TOBT | |
| `TAG_AUTO` | autoStartup | `ACTION_AUTO STARTUP` |
| `TAG_EVSLOT` | Event slot | `ACTION_EVSLOT` (use it as the CTOT) |
| `TAG_COMPLY`, `TAG_ONTIME` | Network status airborne, on-time status | |
| | Extra (every action in one menu) | `ACTION_CDM Menu` |

`TAG_COMPLY` and `TAG_ONTIME` are pushed for every flight the client holds, airborne ones included. The CTOT menu also
holds Ignore/Use CTOT (the CDM plugin's disable and enable CTOT, master only) and, for an event slot, Use as CTOT.

Expressions for package conditions: `EXP_Listed` (a departure from one of your airports, not airborne),
`EXP_Is CDM`, `EXP_Ready`, `EXP_Has CTOT`, `EXP_Suspended`, `EXP_Informed`, `EXP_ETFMS` and `EXP_TSAT Window`
(`pending`, `future`, `window`, `closing`, `expired`, `suspended`).

## Commands

`.cdm master`, `slave`, `startupdelay`, `departuredelay`, `lvo`, `remarks`, `rmkctot`, `recover`, `refresh` and `help`
work as in the CDM plugin. `recover` and `refresh` read vIFF again. NeoRadar adds:

| Command | Does |
|---|---|
| `.cdm rate ICAO NORMAL [LVO]` | Override vIFF's departure rate per hour, and set the LVO rate (0 returns to vIFF's) |
| `.cdm deicepads ICAO REM1=10,REM2=8` | Remote de-icing pads and their extra taxi minutes |
| `.cdm panel` | Open the CDM Airport Panel |
| `.cdm atfcm` | Open the ATFCM Flight List |
| `.cdm apikey KEY` | Set the vIFF API key on this machine (`default` removes it) |
| `.cdm status` | The master of each watched airport |

## Not supported yet

- **Sending the VDGS private message (MSG).** NeoRadar's SDK cannot send a private message, so `TAG_MSG` shows
  whether the pilot was informed but cannot inform them.
- **Sequencing options from the vACC's files:** per-runway rates and dependent runways (`rate.txt`; this port runs
  one queue per airport at vIFF's rate), taxi zones (and so EXOT for non-CDM airports), runway pressure, BMI and SID
  intervals. Event slots (`ctot.txt`) are read already.
- **The BMI and monitoring windows**, the commands `ctot`, `event`, `runwaypressure`, `realmode`, `refreshtime`,
  `resetmaster`, `nvo`, `server`, `save`, `load`, `data`, `debug`, and the FTP and VDGS file exports.
- **ASAT** is the time this client saw start-up approved, as vIFF does not store it.

## Settings

Optional plugin settings: `apiUrl` (default `https://viff-system.network/`), `apiKey`, `refreshSeconds` (default 15,
at least 5), `cdmConfigUrl` (where the vACC's CDM files are read, by default the French vACC's
`vaccfr/vIFF-CDM` repository the CDM plugin reads), `readySetTsac` (default true: Ready TOBT sets the TSAC to the TSAT
once it is calculated) and `atfcmFilter` (the ATFCM list's last filter).

### The vIFF API key

Reads need no key; every write (master, TOBT, RDY, REA) does, and vIFF issues it to the plugin, not per controller.
The plugin uses, first found:

1. the key set in NeoRadar with `.cdm apikey KEY` (`.cdm apikey default` removes it);
2. the key built in: `VIFF_API_KEY` from the environment, or `VIFF_API_KEY=...` in a `.env` file at the repository
   root, which is gitignored;
3. the CDM plugin's placeholder `TEST`, which reads only.

The startup log line says which one is in use, never the key. A key built into a DLL can be read back out of it, so
a key-carrying build is for local or private use only. **A plugin for a published package is packed without a key:**

```bash
scripts/pack-nrplugin.sh <package>/plugins          # builds keyless, checks the DLL holds no .env key, packs it
```

## Building

```bash
git clone --recurse-submodules https://github.com/vaccfr/vIFF-neoradar.git
dotnet build vIFF.NeoRadar.slnx -c Release
dotnet test vIFF.NeoRadar.slnx -c Release --no-build
```

The SDK is the `extern/plugin-sdk` submodule, pinned to `v0.1.0` and referenced compile-only.
`VIFF_LIVE_TESTS=1 dotnet test` also runs read-only checks against the live vIFF API.
