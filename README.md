# PinSentinel

Per-pin 12V-2x6 power monitoring, imbalance warning and safety shutdown for the
ASUS ROG Astral GeForce RTX 5090.

Status: research / planning. No code yet.

## Why

The RTX 5090 pulls up to 575 W (more on the Astral OC with a raised power limit)
through a single 12V-2x6 connector with six +12 V pins.

- 575 W / 12 V = ~48 A, or ~8.0 A per pin when the load is shared perfectly.
- Each pin is rated for 9.5 A. That is ~19 % headroom at stock power.
- The card joins all six pins into one rail and does not balance them. Current
  splits according to contact and wire resistance only, so one worn or badly
  seated contact pushes its share onto the other pins. der8auer measured over
  20 A on a single wire with others near 2 A, and ~150 °C at the PSU-side plug.
- Nothing in the stock power path reacts to this. The PSU sees a normal total
  load, so OCP never trips.

## What the Astral gives us

The ROG Astral has a shunt per +12 V pin and an ITE IT8915FN monitoring chip.
It reports voltage (mV) and current (mA) for each of the six pins.

| Item | Value |
|---|---|
| Target card | ROG Astral RTX 5090 OC, PCI subsystem `1043:89E3` (the card in this PC) |
| Access | NVAPI I2C read through the NVIDIA driver (`nvapi64.dll`, I2CReadEx `0x4D7B0709`) |
| I2C port / address / register | port `1`, 7-bit address `0x2B`, register `0x80` |
| Frame | 24 bytes, 6 pins x 4 bytes: u16 BE voltage mV, u16 BE current mA |
| Pin order | reversed: bytes 0-3 are pin 6, bytes 20-23 are pin 1 |

The protocol is reverse-engineered by the community, not documented by ASUS.
It must be confirmed on this card before anything depends on it. Reads only:
writing to the wrong device on a GPU I2C bus can damage hardware, so the write
call is never bound.

ASUS's own handling (GPU Tweak III Power Detector+) warns when a pin reads 0 A
or goes above ~9.2 A, and the card blinks its red power LED. It only warns. It
needs GPU Tweak III running, takes no action, and has no shutdown.

## Prior art

| Project | Platform | What it does | Gap |
|---|---|---|---|
| [12vhpwr-guard](https://github.com/humza-khalid/12vhpwr-guard) | Windows, Python, MIT | Direct I2C read, tiered overcurrent response (9.5 A/15 s, 13 A/3 s, 16 A/1 s), GPU throttle then forced shutdown, flight recorder | Absolute thresholds only. Runs as a logon task, not a service |
| [GPUPinMonitor](https://github.com/xsmphr/GPUPinMonitor) | Windows, C#, MIT | Desktop widget via ASUS `ExpanModule.dll` | Display only, needs GPU Tweak III |
| [astral-hwmon](https://github.com/ksokolowski/astral-hwmon) | Linux kernel driver | hwmon sensors plus `astral-guard` checks (imbalance, zero pin, voltage sag) | Linux only, reports rather than protects by default |
| [astral-power-monitor](https://github.com/EthDevOps/astral-power-monitor) | Linux | Per-pin service | Linux only |

Overcurrent shutdown on Windows already exists. Simply installing
12vhpwr-guard gets most of the headline feature today. PinSentinel is worth
building for what it does not cover, listed below.

## Where protection can be improved

1. **Load-relative imbalance detection.** An absolute 9.5 A limit says nothing
   at partial load. At 300 W a healthy pin carries ~4.2 A; one pin at 8 A with
   the rest near 3.4 A is a failing contact, and no absolute threshold fires.
   Compare each pin to the mean of the six and alert on deviation, gated on a
   minimum total current so idle noise does not trigger it.
2. **Open-pin detection.** A pin near 0 A while the others carry load means a
   contact has gone open and five pins are doing the work of six.
3. **Use the per-pin voltage.** Spread between pin voltages under load reflects
   differences in cable and contact resistance. Sag against idle voltage
   reflects the whole path. Both rise as a connector degrades.
4. **Baseline and drift.** Record each pin's share of total current per load
   band and alert when it drifts over days or weeks. Connector damage is
   usually progressive, and this gives warning long before a threshold trips.
5. **Graduated response.** Toast and sound, then cut GPU power limit and clocks,
   then forced shutdown if the fault persists. Dropping GPU load removes the
   hazard within a second, so shutdown is the backstop rather than the first
   move. Short hot-path thresholds for severe faults (e.g. 16 A) skip straight
   to shutdown.
6. **Run as a Windows service.** Starts at boot before logon, runs as SYSTEM so
   it can always throttle and shut down, restarts on crash, and does not die
   with a user session.
7. **Fail safe on sensor loss.** If reads fail or return garbage for several
   polls while the GPU is under load, treat it as a fault and warn rather than
   silently reporting nothing.
8. **Evidence.** Rolling per-pin log and a pre-event flight recorder, written to
   disk before shutdown, plus Windows Event Log entries. Useful for diagnosing
   a cable and for an RMA.

## Proposed thresholds (starting points, to be tuned on real data)

| Condition | Warn | Throttle | Shutdown |
|---|---|---|---|
| Any pin current | >= 9.0 A for 10 s | >= 9.5 A for 10 s | >= 13 A for 3 s, or >= 16 A for 1 s |
| Pin deviation from mean (total > 15 A) | > 20 % for 30 s | > 35 % for 15 s | > 50 % for 15 s after throttle |
| Pin < 0.5 A while total > 15 A | 5 s | 15 s | 30 s after throttle |
| Pin voltage spread under load | > 150 mV | > 250 mV | n/a |
| Sensor unreadable under load | 5 polls | n/a | n/a |

All configurable. The imbalance and voltage numbers are estimates and need a
week or two of baseline logging on this card before shutdown is armed on them.

## Limits of a software guard

- It sees only the six +12 V pins at the card. Not the ground pins, not the
  PSU-side plug, and no temperatures. der8auer's 150 °C reading was at the PSU end.
- Polling is ~2-5 Hz. It catches sustained faults, not transients.
- It depends on Windows and the NVIDIA driver being alive. A hung system is
  unprotected.
- Possible I2C contention if GPU Tweak III or HWiNFO poll the same chip at the
  same time. To be tested.
- BTF variants powered through the GC-HPWR slot bypass the shunts. Not relevant
  to this card.

Physical measures that complement it: a native ATX 3.1 12V-2x6 PSU cable
rather than an adapter, fully seated with no bend close to the plug, few
re-plugs (the connector is rated for about 30 mating cycles), and a modest
power limit. A hardware in-line monitor such as Thermal Grizzly WireView Pro II
covers the cases software cannot.

## Proposed architecture

- .NET 8 Windows service (`PinSentinel.Service`): P/Invoke NVAPI read, rule
  engine, responder (NVML/NVAPI power limit, `InitiateSystemShutdownEx`),
  logging.
- Tray app (`PinSentinel.Tray`): live per-pin bars, toasts, history, config.
  Talks to the service over a named pipe.
- Rule engine kept free of hardware dependencies so thresholds can be unit
  tested against recorded and synthetic pin traces.

## Roadmap

0. Read-only probe: confirm the I2C frame on this card and compare against
   GPU Tweak III / HWiNFO under load.
1. Logger: service that records per-pin V/I, establish a baseline.
2. Rule engine with tests, warnings only.
3. Throttle and shutdown responders, with a dry-run mode.
4. Tray UI, drift analysis, installer.

## Sources

- [ASUS: How Power Detector+ alerts you to abnormal current](https://rog.asus.com/articles/guides/how-gpu-tweaks-power-detector-alerts-you-to-abnormal-current-on-your-rog-astral-graphics-card/)
- [LACT issue 906: Astral per-pin monitoring via I2C](https://github.com/ilya-zlobintsev/LACT/issues/906)
- [Igor's Lab: 12VHPWR plug reaches 150 °C on the PSU side](https://www.igorslab.de/en/12vhpwr-plug-reaches-150c-on-the-psu-side-when-connected-to-the-geforce-rtx-5090-design-problem-instead-of-user-error/)
- [Igor's Lab: 12V-2x6 connector](https://www.igorslab.de/en/rest-in-peace-12vhpwr-connector-welcome-12v-2x6-connector/)
- [Guru3D: open-source 12VHPWR Guard](https://www.guru3d.com/story/opensource-12vhpwr-guard-can-shut-down-asus-rtx-5090-systems-before-sustained-connector-overcurrent/)
