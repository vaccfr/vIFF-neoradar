# vIFF-neoradar

The [NeoRadar](https://docs.neoradar.app) port of the EuroScope [CDM plugin](https://github.com/rpuig2001/CDM),
backed by the [vIFF](https://viffsys.com) system.

It behaves as the CDM plugin does: the same times, colours, master and slave roles, commands and vIFF messages.
For what CDM does and how to work with it, read the [CDM plugin's documentation](https://github.com/rpuig2001/CDM#readme)
and your vACC's procedures. This page covers only what is specific to NeoRadar.

## Installing

Add `Viff.NeoRadar.dll` (from the latest CI run, or a local build) in Plugin Management, or ship it in a package as a
`.nrplugin` (see the API key section). Then bind the items below in your package; `Package/` beside the DLL holds an
example list and label items. The plugin is one managed assembly, so the same build runs on Windows, macOS and Linux;
the packed `.nrplugin` carries it for all three.

## CDM configuration

The plugin reads the CDM plugin's own `CDMconfig.xml`, so a vACC ships the file it already has. It looks, first found:

1. in the package, `cdm/CDMconfig.xml` (the package's root, beside `manifest.json`);
2. in the controller's NeoRadar folder, `Documents/NeoRadar/vIFF CDM/CDMconfig.xml`.

As in the CDM plugin, the `url` of `Rates`, `Taxizones`, `Slots` and `sidInterval` is downloaded (and the last copy
kept under `Documents/NeoRadar/vIFF CDM/cache` for when the download fails), and a blank `url` reads `rate.txt`,
`taxizones.txt`, `ctot.txt` or `sidInterval.txt` from beside `CDMconfig.xml`. The setup reloads every 30 minutes,
when the package changes, and with `.cdm reload`; event slots every 5 minutes. The options read are `rate`, `rateLvo`,
`DefaultTaxiTime`, `DeIceTimes`, `DeIceRemTaxi`, `bmi`, `invalidateAtTsat`, `readySetTsac`,
`reqTobtPriority` and `eventPriority`; they and the files work as in the CDM plugin. A line the plugin cannot read is
skipped and logged. Without a `CDMconfig.xml` the plugin sequences on vIFF's rates and taxi times.

A departure's rate is, first found: `.cdm rate`, the `rate.txt` line of its runway, vIFF's rate, then `rate`.

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
- **Configuration from the package.** `CDMconfig.xml` comes with the package instead of the plugin folder (see above);
  runways, SIDs, ground status, positions and wake categories come from NeoRadar.
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

For small screens the plugin adds items the CDM plugin does not have, so a list needs fewer columns:

| Tag item | Shows |
|---|---|
| `TAG_TIME` | The time that matters now, named: `TOBT 1210` (or `EOBT`) until a TSAT exists, `TSAT 1215` until start-up, then `TTOT 1225`; a suspended flight shows its TOBT. Coloured as that time's own item |
| `TAG_TIME S` | The same with a one-letter name: `B1210` TOBT (off-block), `S1215` TSAT (start-up), `T1225` TTOT (take-off), `E` EOBT |
| `TAG_STATUS` | The status that matters most: `SUSP`, `EXP` (TSAT expired), `REA`, `RDY` |
| `TAG_SEQ` | Take-off order at the airport by TTOT, `01` first (started flights counted, suspended ones not); sort a list by it |

`TAG_COMPLY` and `TAG_ONTIME` are pushed for every flight the client holds, airborne ones included. The CTOT menu also
holds Ignore/Use CTOT (the CDM plugin's disable and enable CTOT, master only) and, for an event slot, Use as CTOT.

Expressions for package conditions: `EXP_Listed` (a departure from one of your airports, not airborne),
`EXP_Is CDM`, `EXP_Ready`, `EXP_Has CTOT`, `EXP_Suspended`, `EXP_Informed`, `EXP_ETFMS`, `EXP_TSAT Window`
(`pending`, `future`, `window`, `closing`, `expired`, `suspended`) and `EXP_Stage`, which splits the listed CDM
departures for short lists: `now` (start-up within 10 minutes, or ready, REA, suspended or expired), `started`
(start-up approved, still on the ground) and `later`. `Package/systems/viff-cdm-lists.yaml` has a full list and the
compact NOW, LATER and TOPDOWN lists built on them.

## Commands

`.cdm master`, `slave`, `startupdelay`, `departuredelay`, `lvo`, `remarks`, `rmkctot`, `recover`, `refresh`, `reload`
and `help` work as in the CDM plugin. `recover` and `refresh` read vIFF again. NeoRadar adds:

| Command | Does |
|---|---|
| `.cdm rate ICAO NORMAL [LVO]` | Override the departure rate per hour, and the LVO rate (0 removes the override) |
| `.cdm deicepads ICAO REM1=10,REM2=8` | Remote de-icing pads and their extra taxi minutes |
| `.cdm panel` | Open the CDM Airport Panel |
| `.cdm atfcm` | Open the ATFCM Flight List |
| `.cdm apikey KEY` | Set the vIFF API key on this machine (`default` removes it) |
| `.cdm status` | The CDM setup in use and the master of each watched airport |

## Not supported yet

- **Sending the VDGS private message (MSG).** NeoRadar's SDK cannot send a private message, so `TAG_MSG` shows
  whether the pilot was informed but cannot inform them.
- **The BMI and monitoring windows**, the commands `ctot`, `event`, `runwaypressure`, `realmode`, `refreshtime`,
  `resetmaster`, `nvo`, `server`, `save`, `load`, `data`, `debug`, and the FTP and VDGS file exports.
- **ASAT** is the time this client saw start-up approved, as vIFF does not store it.

## Settings

Optional plugin settings: `apiUrl` (default `https://viff-system.network/`), `apiKey`, `refreshSeconds` (default 15,
at least 5), `readySetTsac` (`true` or `false`, overrides `CDMconfig.xml`) and `atfcmFilter` (the ATFCM list's last
filter). The client log has a line for every state change, chat message, toast and command (never the API key);
per-cycle detail is logged at Debug.

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

The SDK is the `extern/plugin-sdk` submodule, pinned to `v0.1.1` and referenced compile-only.
`VIFF_LIVE_TESTS=1 dotnet test` also runs read-only checks against the live vIFF API.
