# PinSentinel design notes

## Components

```
                 NVAPI I2C (read-only)
  GPU IT8915FN  <--------------------  PinSentinel.Service  (SYSTEM, session 0)
                                         |  AstralSensor -> RuleEngine -> GuardController -> WindowsGuardActions
                                         |  CsvLog, incident recorder
                                         |
                    PinSentinel.Status   |   one JSON line per sample (out)
                    PinSentinel.Control  |   arm / disarm / test / throttle-test (in/out)
                                         v
                                       PinSentinel.Tray  (user session)
                                         DashboardModel -> views; HealthAnalyzer over the CSV logs; NVML telemetry
```

`PinSentinel.Core` holds everything that is not tied to a host: the sensor
reader, parser, rules, guard state machine, CSV format, status message and
health analysis. The service and tray reference it.

## Sensor protocol

ROG Astral cards carry an ITE IT8915FN that measures voltage and current on each
+12 V pin of the 12V-2x6 connector. ASUS does not document it. The details below
come from community reverse engineering and were confirmed on a `1043:89E3` card.

| Item | Value |
|---|---|
| Access | `nvapi64.dll`, functions resolved through `nvapi_QueryInterface` |
| Functions used | Initialize `0x0150E828`, EnumPhysicalGPUs `0xE5AC921F`, GPU_GetPCIIdentifiers `0x2DDFB66E`, I2CReadEx `0x4D7B0709` |
| Struct | `NV_I2C_INFO_V3`, 64 bytes, version `0x00030040` |
| Port | `1` (`bIsPortIdSet = 1`) |
| Device address | 7-bit `0x2B`, passed as `0x56` |
| Register | `0x80`, 24 bytes |
| Layout | 6 blocks of 4 bytes: u16 big-endian millivolts, u16 big-endian milliamps |
| Order | Reversed: bytes 0-3 are pin 6, bytes 20-23 are pin 1 |
| Resolution observed | 8 mV, 20 mA |

A frame is rejected as a bad read if any voltage is outside 6 to 16 V or any
current is above 30 A.

`NvAPI_I2CWriteEx` is never resolved. A write to the wrong device on a GPU I2C
bus can damage the card, so the code has no way to issue one.

The read works without administrator rights and from session 0. Two readers at
once (the service plus a second instance, or GPU Tweak III) coexisted in testing.

## Rule engine

`RuleEngine.Evaluate(PinFrame)` returns a severity (`Ok`, `Warn`, `Throttle`,
`Shutdown`) and the findings behind it. Each rule is a condition plus a hold
time; the engine records when each condition became true and reports it once it
has been true continuously for the hold. A single sample where the condition is
false resets that rule.

Time comes from the frame's timestamp, never the wall clock, so recorded or
synthetic traces drive it deterministically in tests.

Rules:

- **Pin current.** The highest pin against four limits.
- **Imbalance.** `max |I_pin - mean| / mean`, only when total current is at or
  above the load gate. This is the rule that catches a failing contact at
  partial load, where no pin is near its absolute limit.
- **Open pin.** The lowest pin below `OpenPinAmps` while loaded.
- **Voltage spread.** Highest minus lowest pin voltage while loaded.
- **Sensor loss.** Consecutive failed reads. A failed read also restarts all
  in-progress holds, because elapsed time is unknown without samples.

## Guard state machine

`GuardController` turns severities into actions through `IGuardActions`.

```
Normal --Throttle--> Throttled --fault cleared, held >= 2 min, clear >= 1 min--> Normal
   |                    |
   |                    +--fault still present after 10 s, or Shutdown severity--> ShutdownIssued
   +--Shutdown severity (throttle first)-----------------------------------------> ShutdownIssued
   +--Throttle within 10 min of the last release---------------------------------> ShutdownIssued
```

Each finding notifies once when it becomes active. In dry run `Shutdown` returns
false, and the controller rearms once the fault clears so that further events are
still reported.

`WindowsGuardActions` implements the actions:

- **Notify.** `WTSSendMessage` to the active console session, which reaches the
  desktop from session 0.
- **Throttle.** `nvidia-smi -pl <minimum>` and `nvidia-smi -lgc 0,<ThrottleClockMhz>`.
  The minimum power limit on a 5090 is 400 W, so the clock lock does most of the
  work. Release runs `-rgc` and restores the previous power limit.
- **Shutdown.** `shutdown.exe /s /f /t <delay>`.

A real throttle is undone on release even if the guard was disarmed in between.
The throttle test shares the same hardware path under a lock.

## Pipes

| Pipe | Direction | Access | Content |
|---|---|---|---|
| `PinSentinel.Status` | Service to clients | Authenticated users, read | One `StatusMessage` JSON line per sample |
| `PinSentinel.Control` | Request and reply | Interactive users | `arm`, `disarm`, `test`, `throttle-test`; replies `ok` or `error: ...` |

Publishing never blocks the guard loop. Each status client has an eight-entry
drop-oldest queue and its own writer task, so a stalled UI only loses samples.

## Health analysis

`HealthAnalyzer.AnalyzeDay` reduces one day's log to: minutes under load, each
pin's mean share of total current, mean imbalance, maximum pin current, and an
estimated supply-path resistance per pin:

```
R_pin = (V_idle - V_load) / (I_load - I_idle)
```

using the day's mean idle samples (total under 10 A) and loaded samples (total
at or above 15 A). It needs at least 12 of each and a 3 A difference. The figure
includes PSU regulation droop, which is common to all pins, so only differences
between pins and changes over time are meaningful.

`HealthAnalyzer.Report` keeps days with at least 5 minutes under load. With
three or more it compares the latest day to a baseline of the earliest days (up
to five, never including the latest) and reports *Drifting* if any pin's share
moved by 1.0 percentage point, or its resistance rose by 30 % and 3 mΩ.

The tray caches the analysis of finished days and re-reads only today's file.

## Tray app

- The dashboard window exists only while open. A hidden WPF window keeps its
  render resources, so it is closed and the process trims itself afterwards.
- Movement between samples uses WPF animations on transforms and a dash offset,
  which run on the render thread. `OnRender` runs once per sample, not per frame.
- The backdrop is Windows 11 acrylic set through `DwmSetWindowAttribute`, with a
  translucent tint over it.
- `--demo [fault]` substitutes a synthetic feed and health history.
  `--screenshot <file> [fault] [health]` renders the dashboard to a PNG and exits.

## Verification record

On a ROG Astral RTX 5090 OC (`1043:89E3`), October 2026.

| Item | Result |
|---|---|
| Sensor read, non-admin and as a service | Works |
| Readings against GPU Tweak III at about 550 W | Agree to the displayed precision |
| Continuous logging | 20+ hours with no gap over 30 s |
| Desktop alert from the service | Displayed |
| Real throttle, 20 s test | 592 W to 108 W, 577 W after release |
| Warn, throttle and shutdown sequence | Exercised in dry run only |
| Real shutdown | Not exercised |
| Service memory after 20.7 h | 61 MB private, peak 102 MB working set, 0.5 % of one core |

Baseline over 68 minutes under load:

| Measure | Typical | Worst |
|---|---|---|
| Highest pin | 6.16 A | 7.90 A in games, 8.68 A at about 592 W |
| Imbalance | 4.2 % | 10.1 % (single sample near the load gate) |
| Voltage spread | 16 mV | 32 mV |
| Pin shares | 15.96 / 16.74 / 16.81 / 16.97 / 16.84 / 16.68 % | |

## Open items

- Thresholds for imbalance, voltage spread and health drift are reasoned
  estimates. They have not been observed against a failing connector.
- Cards other than `1043:89E3` are recognised by ID but untested.
- Ideas not built: per-session report, temperature and fan guard, energy cost,
  12 V rail trend, in-game overlay.
