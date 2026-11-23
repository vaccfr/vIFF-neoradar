# vIFF-neoradar

The [NeoRadar](https://docs.neoradar.app) port of the EuroScope [CDM plugin](https://github.com/rpuig2001/CDM),
backed by the [vIFF](https://viffsys.com) system.

It works as the CDM plugin does: the same times and colours, master and slave roles, sequencing rules, configuration
files, commands and vIFF messages. For what CDM does and how to work with it, read the
[CDM plugin's documentation](https://github.com/rpuig2001/CDM#readme) and your vACC's procedures. This page covers
what is specific to NeoRadar.

## Installing

A package ships the plugin as a `.nrplugin` in its `plugins` folder (see [Packing](#packing)); the controller
approves it in Plugin Management. For a test, add a locally built `Viff.NeoRadar.dll` there instead. The plugin is
one managed assembly with no native code, so the same build runs on Windows, macOS and Linux.

The package binds the items below in its lists and tags. `Package/systems/` beside the DLL holds examples: the full
start-up list, the compact NOW, LATER and TOPDOWN lists, and label items.

## What is different on NeoRadar

**Data and roles**

- **vIFF is the shared state.** Every controller reads their airports from vIFF every 15 seconds, and the display
  shows what vIFF holds: a change appears once vIFF has stored it, normally within a second or two. While vIFF does
  not answer, the plugin keeps the last data and retries after 30 seconds, then up to every 2 minutes.
- **The master still sequences.** As with the CDM plugin, vIFF stores the times but does not calculate them: TSATs
  and TTOTs exist only at airports with a master online, whose client sequences and writes them to vIFF.
- **Slave actions are requests.** A controller who is not master sends TOBT and start-up changes as the requests the
  CDM plugin's master applies (`REQTOBT`, `REQASRT`), so a NeoRadar slave works with a EuroScope master and the other
  way round. At an airport with no master, changes are written directly.
- **The master role is released** when the controller disconnects or the plugin unloads.
- **Suspensions are read back from vIFF.** An `FLS-CDM` suspension the master sets (`SUSP`) is taken from vIFF, so it
  survives a master restart or handover.
- **CTOTs from anywhere.** vIFF's network-wide CTOT list is read each cycle, so every flight the client holds that
  has not departed shows its CTOT and regulation, whichever airport's measure set it. Departures from other airports
  with a vIFF master show their TOBT, TSAT and TTOT too, read once a minute, without menus.
- **Writes are gated.** Nothing is written to vIFF unless you are connected as ATC on the live network.

**Configuration**

- **`CDMconfig.xml` comes with the package**, not the plugin folder; see [CDM configuration](#cdm-configuration).
- **Runways, SIDs, ground status, positions and wake categories come from NeoRadar.** The active runway configuration
  picks the `rate.txt` line, and the aircraft position picks the taxi zone.

**Strips**

- **Strips carry the CDM plugin's box 0.** The plugin reads and writes flight strip box 0 in the CDM plugin's format
  (`ASRT/TSAC/TOBT/TSAT/TTOT/deIce/ecfmpId/manualCtot/CTOC/setBy/autoStartup/ATOT`), so TSAC, CTOC, TOBT source and
  automatic start-up travel with the strip between NeoRadar and EuroScope. The master keeps box 0 of its airports'
  flights in step with vIFF. NeoRadar writes one strip a second and pushes a strip on handoff; unlike the CDM plugin
  it does not push every strip to every controller, so a EuroScope controller gets the CDM data with the handoff.
- **Automatic start-up** is done by the master's client: it sets the ground status to start-up for an armed, cleared
  flight in its TSAT window that no other controller tracks.

**Display**

- **Lists cannot hide or group columns on NeoRadar**, so the plugin adds compact items (a named current time, one
  status, the take-off order) and a stage expression that splits the departures into short lists. See [Items](#items).
- **CDM Airport Panel** is a small NeoRadar window, one chip per CDM airport: **CDM Airport Panel** in the Insets &
  Plugin Windows menu, or `.cdm panel`. Left click connects or releases master, right click switches LVO.
- **ATFCM Flight List** is a NeoRadar window too (`.cdm atfcm`, or the menu): vIFF's regulated, suspended and
  re-slotted flights departing your active airports (NeoRadar's `isMyDeparture`), narrowed by an optional prefix
  filter. Double click selects the aircraft.
- **Logging.** The client log has a line for every chat message, toast, command, master or capacity change, config
  load and vIFF outage; per-cycle detail is at Debug.

## CDM configuration

The plugin reads the CDM plugin's own `CDMconfig.xml`, so a vACC ships the file it already has. It looks, first found:

1. in the package, `cdm/CDMconfig.xml` (the package's root, beside `manifest.json`);
2. in the controller's NeoRadar folder, `Documents/NeoRadar/vIFF CDM/CDMconfig.xml`.

As in the CDM plugin, the `url` of `Rates`, `Taxizones`, `Slots` and `sidInterval` is downloaded, and a blank `url`
reads `rate.txt`, `taxizones.txt`, `ctot.txt` or `sidInterval.txt` from beside `CDMconfig.xml`. The last copy
downloaded is kept under `Documents/NeoRadar/vIFF CDM/cache` for when a download fails. The setup reloads every 30
minutes, when the package changes, and with `.cdm reload`; event slots every 5 minutes. A line the plugin cannot read
is skipped and logged. Without a `CDMconfig.xml` the plugin sequences on vIFF's rates and taxi times.

The options read are `rate`, `rateLvo`, `DefaultTaxiTime`, `DeIceTimes`, `DeIceRemTaxi`, `bmi`, `invalidateAtTsat`,
`readySetTsac`, `reqTobtPriority` and `eventPriority`; they and the files work as in the CDM plugin: per-runway rates
and dependent runways, LVO rates, BMI, SID intervals, runway pressure from `rate.txt`, taxi zones, de-icing, and the
taxi time sent to vIFF for departures from airports without CDM. A departure's rate is, first found: `.cdm rate`, the
`rate.txt` line of its runway, vIFF's rate for the airport, then `rate`.

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

Items the CDM plugin does not have, for short lists on small screens:

| Tag item | Shows |
|---|---|
| `TAG_TIME` | The time that matters now, named: `TOBT 1210` (`EOBT` while there is no TOBT) until a TSAT exists, `TSAT 1215` until start-up, then `TTOT 1225`; a suspended flight shows its TOBT. Coloured as that time's own item |
| `TAG_TIME S` | The same with a one-letter name: `B1210` TOBT (off-block), `S1215` TSAT (start-up), `T1225` TTOT (take-off), `E1210` EOBT |
| `TAG_STATUS` | The status that matters most: `SUSP`, `EXP` (TSAT expired), `REA`, `RDY` |
| `TAG_SEQ` | Take-off order at the airport by TTOT, `01` first (started flights counted, suspended ones not); sort a list by it |

Expressions for package conditions: `EXP_Listed` (a departure from one of your airports, not airborne),
`EXP_Is CDM`, `EXP_Ready`, `EXP_Has CTOT`, `EXP_Suspended`, `EXP_Informed`, `EXP_ETFMS`, `EXP_TSAT Window`
(`pending`, `future`, `window`, `closing`, `expired`, `suspended`) and `EXP_Stage`: `now` (start-up within 10
minutes, or ready, REA, suspended or expired), `started` (start-up approved, still on the ground) or `later`; empty
for a flight that is not a listed CDM departure.

## Commands

| Command | Does |
|---|---|
| `.cdm master ICAO` / `.cdm slave [ICAO]` | Take or release the master role (every airport when none is given) |
| `.cdm startupdelay ICAO/RWY HHMM` / `.cdm departuredelay ICAO/RWY HHMM` | No TSAT / TTOT before that time (`MM` for now + minutes, `9999` removes it) |
| `.cdm lvo [ICAO]` | Low-visibility operations, and so the LVO rate, on or off |
| `.cdm rate ICAO NORMAL [LVO]` | Override the departure rate per hour of `rate.txt` or vIFF (`0` removes the override) |
| `.cdm deicepads ICAO REM1=10,REM2=8` | Remote de-icing pads and their extra taxi minutes |
| `.cdm remarks` / `.cdm rmkctot` | Write the TSAT / CTOT (`.CHHMM`) into the scratchpad |
| `.cdm recover ICAO` / `.cdm refresh` | Read vIFF again |
| `.cdm reload` | Read `CDMconfig.xml` and its files again |
| `.cdm panel` / `.cdm atfcm` | Open the CDM Airport Panel / the ATFCM Flight List |
| `.cdm status` | The CDM setup in use and the master of each watched airport |
| `.cdm apikey KEY` | Set the vIFF API key on this machine (`.cdm apikey default` removes it) |

## Not supported yet

- **Sending the VDGS private message (MSG).** NeoRadar's SDK cannot send a private message, so `TAG_MSG` shows
  whether the pilot was informed but cannot inform them.
- **The BMI and monitoring windows** (BMI sequencing itself works), the commands `ctot`, `event`, `runwaypressure`,
  `realmode`, `refreshtime`, `resetmaster`, `nvo`, `server`, `save`, `load`, `data` and `debug`, and the FTP and VDGS
  file exports.
- **`CDMconfig.xml` options not listed above** (such as `pilotTobt`, `invalidateAtTobt`, `expiredCtot`,
  `flashingMode`, `remarksOptionCtot`, `customRestricted`) are ignored.
- **ASAT** is the time this client saw start-up approved, as vIFF does not store it.

## Settings

Optional plugin settings: `apiUrl` (default `https://viff-system.network/`), `apiKey` (set by `.cdm apikey`),
`refreshSeconds` (default 15, at least 5), `readySetTsac` (`true` or `false`, overrides `CDMconfig.xml`) and
`atfcmFilter` (the ATFCM list's last filter).

## The vIFF API key

Writes to vIFF need an API key, built in at build time. Put it in a `.env` file at the repository root (gitignored):

```bash
VIFF_API_KEY=...
```

or set the `VIFF_API_KEY` environment variable before building. Without it the plugin only reads vIFF.

### Packing

```bash
scripts/pack-nrplugin.sh --with-key <package>/plugins   # key built in (from VIFF_API_KEY or .env)
scripts/pack-nrplugin.sh <package>/plugins              # no key: reads only until a controller sets one
```

Both write `<package>/plugins/vIFF-CDM-alpha.nrplugin` with the same assembly for Windows, macOS and Linux. They need
the .NET SDK, Python and `neoradar-cli`.

## Building

```bash
git clone --recurse-submodules https://github.com/vaccfr/vIFF-neoradar.git
dotnet build vIFF.NeoRadar.slnx -c Release
dotnet test vIFF.NeoRadar.slnx -c Release --no-build
```

The SDK is the `extern/plugin-sdk` submodule, pinned to `v0.1.1` and referenced compile-only: the client supplies it
at run time, so no copy may ship with the plugin. `VIFF_LIVE_TESTS=1 dotnet test` also runs read-only checks against
the live vIFF API and the French vACC's CDM files. CI builds and tests every push without a key.
