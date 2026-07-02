# waffle_meter_replay (private)

The **private movement/positional-replay engine** for waffle_meter — a WCL-style replay of where every
combat participant stood during a battle, on a 2D map, with play/pause + scrub.

This repo is private on purpose. It holds the **reverse-engineered** part of the feature: how to decode
the game's `0x37xx` entity-transform packets (including the dense `0x371D` movement stream) and
reconstruct a smooth path from them. Shipping this as source in the public `waffle_meter` repo would make
the release obfuscation pointless, so the engine lives here and is bolted into the release as a DLL. The
public app never references it at compile time — it discovers it at runtime.

## How it plugs in

```
                 waffle_meter (PUBLIC)                         waffle_meter_replay (THIS, PRIVATE)
  ┌────────────────────────────────────────────┐        ┌──────────────────────────────────────┐
  │ WaffleMeter.Replay.Abstractions             │        │ WaffleMeter.Replay  (the engine)       │
  │   model (ReplayRecording/Track/Point)        │◄───────│   MovementParser     0x37xx / 0x371D   │
  │   ReplaySerializer                           │  impl  │   MovementReconstructor                │
  │   IReplayEngine / IReplayEngineFactory        │        │   MovementCaptureService : IReplayEngine
  │   ReplayEngineLoader  (reflection probe) ─────┼──load──┤   ReplayEngineFactory  (public entry)  │
  │ App.Core / App.Wpf  → depend on Abstractions │        └──────────────────────────────────────┘
  └────────────────────────────────────────────┘                 built + injected at release
```

- The app calls `ReplayEngineLoader.TryLoad()` at runtime. If `WaffleMeter.Replay.dll` (this engine) is
  beside the exe, replay works; if not (e.g. an open-source build), replay is simply unavailable.
- The release CI (`release-dotnet.yml` in the public repo) clones this repo, builds the engine, and drops
  the DLL into the publish folder before packing — so **every shipped release includes replay**.

## Building

This engine references the public assemblies (`Abstractions`, `Capture`, `Data`) from the `waffle_meter`
repo **checked out as a sibling directory**:

```
projects/
  waffle_meter          # public
  waffle_meter_replay   # this
```

Then:

```
dotnet build dotnet/src/WaffleMeter.Replay -c Release
dotnet test  dotnet/tests/WaffleMeter.Replay.Tests -c Release
```

## Layout

- `dotnet/src/WaffleMeter.Replay/` — the engine.
- `dotnet/tests/WaffleMeter.Replay.Tests/` — engine tests (they encode the wire format in fixtures, so
  they stay private). Public serializer/model tests remain in `waffle_meter`.
- `dotnet/tools/` — `ReplayGenCli` (corpus → replay JSON) and `MovementProbe` (feasibility/diagnostic spike).
- `docs/` — the RE writeup (`0x371D-movement-protocol.md`), the feature plan, and the dev log.

## Docs

- [`docs/0x371D-movement-protocol.md`](docs/0x371D-movement-protocol.md) — the reverse-engineered wire
  format + reconstruction algorithm (the secret).
- [`docs/replay-feature-plan.md`](docs/replay-feature-plan.md) — the full feature plan.
- [`docs/dev-log.md`](docs/dev-log.md) — chronological development history.
