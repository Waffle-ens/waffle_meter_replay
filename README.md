# waffle_meter_replay

The **movement / positional replay engine** for [waffle_meter](https://github.com/Waffle-ens/waffle_meter) —
a WCL-style replay of where every combat participant stood during a battle, on a 2D map, with
play/pause + scrub.

It decodes AION2's `0x37xx` entity-transform packets (including the dense `0x371D` movement stream) and
reconstructs a smooth, world-anchored path from them.

MIT licensed, same as the meter.

## Why it is a separate repository

Not for secrecy — this repo is public. It stays separate because the engine is **not a compile-time
dependency** of the app: the meter depends only on the `WaffleMeter.Replay.Abstractions` contract, and
`ReplayEngineLoader.TryLoad()` discovers this engine by reflection at runtime. A build without the DLL
runs fine and simply reports that replay is unavailable. Keeping the implementation in its own repo is
what makes that seam real rather than notional.

## How it plugs in

```
                 waffle_meter                                  waffle_meter_replay (THIS)
  ┌────────────────────────────────────────────┐        ┌──────────────────────────────────────┐
  │ WaffleMeter.Replay.Abstractions             │        │ WaffleMeter.Replay  (the engine)       │
  │   model (ReplayRecording/Track/Point)        │◄───────│   MovementParser     0x37xx / 0x371D   │
  │   ReplaySerializer                           │  impl  │   MovementReconstructor                │
  │   IReplayEngine / IReplayEngineFactory        │        │   MovementCaptureService : IReplayEngine
  │   ReplayEngineLoader  (reflection probe) ─────┼──load──┤   ReplayEngineFactory  (public entry)  │
  │ App.Core / App.Wpf  → depend on Abstractions │        └──────────────────────────────────────┘
  └────────────────────────────────────────────┘                 built + injected at release
```

- The app calls `ReplayEngineLoader.TryLoad()` at runtime. If `WaffleMeter.Replay.dll` is beside the exe,
  replay works; if not, replay is simply unavailable.
- The release CI (`release-dotnet.yml` in the meter repo) clones this repo, builds the engine, and drops
  the DLL into the publish folder before packing — so **every shipped release includes replay**. There is
  no gate and no token on that step: what ships is decided by the source alone.

## Building

This engine references the assemblies (`Abstractions`, `Capture`, `Data`) from the `waffle_meter`
repo **checked out as a sibling directory**:

```
projects/
  waffle_meter
  waffle_meter_replay   # this
```

Then:

```
dotnet build dotnet/src/WaffleMeter.Replay -c Release
dotnet test  dotnet/tests/WaffleMeter.Replay.Tests -c Release
```

## Layout

- `dotnet/src/WaffleMeter.Replay/` — the engine.
- `dotnet/tests/WaffleMeter.Replay.Tests/` — engine tests. Serializer/model tests live in `waffle_meter`.
- `dotnet/tools/` — `ReplayGenCli` (corpus → replay JSON) and `MovementProbe` (diagnostic spike).
