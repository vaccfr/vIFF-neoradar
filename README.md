# vIFF-neoradar

A-CDM and ATFCM departure management for [NeoRadar](https://docs.neoradar.app), backed by the
[vIFF](https://viffsys.com) system. It replaces the EuroScope [CDM plugin](https://github.com/rpuig2001/CDM)
for the operational procedures in the French vACC
[CDM & ATFCM documentation](https://doc.vatsim.fr/en/atc/documentation/tools/cdm-homepage).

## How it works

vIFF is the shared state. Every controller's plugin reads the departures of their airports from vIFF every
15 seconds, and every change a controller makes is written to vIFF, so it reaches every other controller,
on NeoRadar or EuroScope.

vIFF does not build departure sequences itself: TSATs and TTOTs exist only at airports with a **master**.
One controller per airport, normally the one issuing IFR clearances, connects as master; their plugin
calculates the sequence and writes it to vIFF. The master role is held on the vIFF server, so a reconnecting
master gets it back and it is released on `.cdm slave`, on disconnect and when the plugin unloads.

The display always follows vIFF: nothing a controller does is shown until vIFF has stored it. A successful change
asks for an immediate read, so it appears within a second or two.

When a controller who is not master changes a flight, the change goes to the master as a request
(`REQTOBT`, `REQASRT`), exactly as the EuroScope plugin does, so it works whichever client the master uses.
At an airport with no master, changes are written directly.

### Flights from other airports

Every CTOT on the network is read each cycle from vIFF's `/etfms/restricted` list, so any flight the client holds
shows its CTOT and regulation, wherever it departs from. The same list fills in a CTOT the departure airport's feed
lacks, and the master's sequencer always holds the take-off to it. For departures from other airports that have a
vIFF master, such as an inbound from Geneva or Frankfurt, the plugin also reads that airport once a minute and shows
its TOBT, TSAT and TTOT. These flights get tags but no menus, as their data belongs to another airport's master, and
`EXP_Listed` stays false for them so they stay out of the start-up list.

No local configuration files are needed: rates, taxi times and the list of CDM airports come from vIFF;
runways, SIDs, ground status and wake categories come from NeoRadar.

### The sequencer

- A TSAT is generated 35 minutes before the E/TOBT; before that the TSAT shows `~`.
- One departure queue per airport at vIFF's rate (or the `.cdm rate` override, or the LVO rate under `.cdm lvo`).
- EXOT is vIFF's taxi time for the flight, plus de-icing (L 5, M 9, H 12, J 15 minutes) and the remote pad's extra taxi.
- Flights with a CTOT, ATFCM or manual, are held to it; a flight that can no longer make CTOT +10 is flagged in the vIFF chat channel.
- Confirmed TOBTs (pilot via VDGS, or set by ATC) and flights that reported ready are placed before unconfirmed ones.
- Flights whose start-up was approved keep their times.
- A flight that is not started and not ready by TSAT +6 is suspended on vIFF (`SUSP`, shown as `FLS-CDM`) and
  loses its place until its TOBT changes; the new TOBT is resequenced and its OBT sent so vIFF lifts the suspension.
  The suspension is read back from vIFF, so it survives a master restart or handover.
- A flight plan suspended by the network (`FLS-NRA`, `FLS-MR`, `FLS-GS`) is left out of the sequence and untouched.
- `.cdm startupdelay` and `.cdm departuredelay` hold back TSATs and TTOTs per runway.
- Pilot TOBT and start-up requests from the VDGS are applied by the master and acknowledged to vIFF.

## The French vACC start-up list

| Column | Tag item | Click |
|---|---|---|
| BY | `TAG_BY`: P pilot, A ATC, blank unconfirmed | |
| DEICE | `TAG_DEICE` | `ACTION_DEICE Menu`: none, STND, remote pads |
| TTG | `TAG_TTG`: minutes to TSAT | |
| E/TOBT | `TAG_TOBT`: TOBT at CDM airports, EOBT elsewhere, red while the flight plan is suspended | `ACTION_TOBT Menu`: Ready TOBT (+ REA with a CTOT), Edit TOBT. Also `ACTION_TOBT Ready`, `ACTION_TOBT Edit` |
| TSAT | `TAG_TSAT`: `~`, light green before TSAT −5, green in the window, flashing at TSAT +5, red once expired or suspended | |
| ASAT | `TAG_ASAT` | |
| TTOT | `TAG_TTOT` | `ACTION_TTOT Menu`: hold the take-off to a time |
| CTOT | `TAG_CTOT`: green ATFCM, orange manual, red when it can no longer be met | `ACTION_CTOT Menu`: new TOBT for a revised CTOT, REA, manual CTOT |
| ETFMS | `TAG_ETFMS`: a suspension first (FLS-CDM, FLS-NRA, FLS-MR, FLS-GS) in red, then REA, SAM, SRM, SLC, DES, ACT | `ACTION_ETFMS Menu`: send or cancel REA |
| MSG | `TAG_MSG`: green once the pilot was informed | |
| RDY | `TAG_RDY`: red, green once set | `ACTION_RDY` sets or clears it, `ACTION_RDY REA` sets it and sends REA |
| ASRT | `TAG_ASRT` | |

Ids are prefixed `plugin:vIFF CDM:`, for example `plugin:vIFF CDM:TAG_TSAT`. `TAG_REG` (the regulation) and
`TAG_EOBT` are also available.

Expressions for package conditions: `EXP_Listed` (a watched departure, not airborne, for list filters),
`EXP_Is CDM`, `EXP_Ready`, `EXP_Has CTOT`, `EXP_Informed`, `EXP_Suspended`, `EXP_TSAT Window`
(`pending`, `future`, `window`, `closing`, `expired`, `suspended`) and `EXP_ETFMS`.

`Package/` beside the built plugin holds an example start-up list and label items to copy into a package.

## CDM Airport Panel

**CDM Airport Panel** in the Insets & Plugin Windows menu, or `.cdm panel`, lists the watched airports (your
airport configuration, plus any named in `.cdm master`) in the documented colours: red not connected as master,
yellow connecting, green connected as master, purple someone else is master. It connects and releases the
master role, switches LVO, and shows the rate in use.

## Commands

| Command | Does |
|---|---|
| `.cdm master ICAO` | Connect as CDM master |
| `.cdm slave [ICAO]` | Release the master role, of every airport when none is given |
| `.cdm startupdelay ICAO/RWY HHMM` | No TSAT before that time (`MM` for now + minutes, `9999` removes it) |
| `.cdm departuredelay ICAO/RWY HHMM` | No TTOT before that time |
| `.cdm lvo [ICAO]` | Switch LVO, and so the LVO rate |
| `.cdm rate ICAO NORMAL [LVO]` | Override vIFF's rate per hour, and set the LVO rate; 0 returns to vIFF's |
| `.cdm deicepads ICAO REM1=10,REM2=8` | Remote de-icing pads and their extra taxi minutes |
| `.cdm remarks`, `.cdm rmkctot` | Write the TSAT, or the CTOT as `.CHHMM`, into the scratchpad |
| `.cdm recover ICAO`, `.cdm refresh` | Read vIFF again now |
| `.cdm status`, `.cdm help` | |

## Writes are gated

Nothing is written to vIFF unless you are connected as ATC on the live network, so a sweatbox or observer
session never changes live CDM data.

## Settings

Optional plugin settings: `apiUrl` (default `https://viff-system.network/`), `apiKey` and `refreshSeconds`
(default 15, at least 5). Rates and de-icing pads set by command are stored there too.

### The API key

vIFF does not issue keys per user today. The EuroScope plugin sends the fixed key `TEST` from its public source,
and the plugin does the same by default. Reads do not check the key at all: no key, `TEST` and an invented key all
return the same data. vIFF's API description declares the `x-api-key` header for every endpoint, so writes may check
it; this has not been tested, to avoid writing to live data. Ask the vIFF team before relying on another key.

## Known gaps

- **MSG cannot send the VDGS private message.** SDK 0.1.0 has no way to send a private message, so the column
  shows whether the pilot was informed but cannot inform them. It needs a chat API addition in the SDK.
- **ASAT is what this client saw**: vIFF does not store it, so a controller who connects late sees no ASAT for
  flights already started.
- **BY** is P or A while the plugin saw the request; afterwards a pilot-confirmed TOBT reads P and an ATC one blank.
- One queue per airport at the airport rate; independent runway rates (rate.txt) are not modelled.
- `REQASRT/HHMM` from a non-master follows the format the master acknowledges (`REQASRT/NULL`) but has not been
  checked against vIFF; confirm it live before relying on it.
- `.cdm remarks` writes through NeoRadar's scratchpad command line; check live that a bare `HHMM` is not read as a command.

## Building

```bash
git clone --recurse-submodules https://github.com/vaccfr/vIFF-neoradar.git
dotnet build vIFF.NeoRadar.slnx -c Release
dotnet test vIFF.NeoRadar.slnx -c Release --no-build
```

The plugin is `src/Viff.NeoRadar/bin/Release/net10.0/Viff.NeoRadar.dll`; add it in Plugin Management. The SDK
is the `extern/plugin-sdk` submodule pinned to `v0.1.0`, referenced compile-only, so the client's own copy is
used at run time. `VIFF_LIVE_TESTS=1 dotnet test` also runs a read-only check against the live vIFF API.
