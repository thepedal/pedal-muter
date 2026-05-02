# Pedal Muter — ReBuzz Managed Control Machine

A control machine that mutes any number of native or managed machines via
pattern automation or peer control. Each track on Pedal Muter maps to one
or more target machines; setting the track's `Mute` parameter to `1` mutes
them all by writing `IMachine.IsMuted = true`.

The design target is **switching expensive synthesizers in and out** under
peer control. With ReBuzz's *Settings → Audio → Process muted machines*
unchecked, muting a target engages true zero-CPU bypass — the entire
render path for the muted machine is skipped.

---

## Features

| Feature | Notes |
|---|---|
| Up to 64 tracks | Configurable via `MAX_TRACKS` constant |
| Per-track `Mute` parameter | `0` = play, `1` = mute. Pattern-automatable; receives writes from PeerCtrl. |
| Multi-target fan-out | Multiple assignments per track → one PeerCtrl slider mutes a group |
| Native and managed targets | Drives `IMachine.IsMuted` regardless of plugin type |
| State persistence | Saved with the song; ReBuzz `ImportFinished` rename fix-up supported |
| Self-mute prevention | Refuses to mute the controller itself |
| Audio-thread safe | All `IMachine.IsMuted` writes marshalled via `Dispatcher.BeginInvoke` |

---

## Parameters

### Track (per track)

| Name | Range | Default | Description |
|------|-------|---------|-------------|
| Mute | 0 / 1 | 0 | `0` = play; `1` = mute every assigned target on this track |

That's the entire parameter surface. Configuration of *which* target each
track controls is done through the Assignments dialog.

---

## Typical setup with PeerCtrl

```
PeerCtrl (track 0, Value parameter)
   │
   └─→  drives  →  Pedal Muter (track 0, Mute parameter)
                       │
                       ├─→ mutes →  Synth A
                       ├─→ mutes →  Synth B
                       └─→ mutes →  Synth C
```

In PeerCtrl: assign Pedal Muter's Mute parameter (track 0) as the target.
Slide the PeerCtrl fader past the midpoint and Synth A/B/C all mute.

For one fader → one machine, single-assignment per track is fine. For
group muting, fan multiple assignments out from the same Pedal Muter track.

---

## Assignments dialog

Open via right-click → **Assignments…**

```
┌─ Track ─────────────────┐  ┌─ Target machine ─────────────────┐
│  Track: [Track 1 ▾]     │  │  Machine: [Synth A ▾]            │
├─────────────────────────┤  └──────────────────────────────────┘
│  → Synth A              │  ┌─ How it works ───────────────────┐
│  → Synth B              │  │  Each track has a Mute parameter │
│                         │  │  (0=play, 1=mute). Drive it from │
│                         │  │  a PeerCtrl track or pattern...  │
│  [Add] [Delete] [Clear] │  └──────────────────────────────────┘
└─────────────────────────┘
  Track 1: 2/2 resolved · currently playing.                [Close]
```

**Workflow:** pick a Track, **Add** an empty assignment, choose the
**Machine** to mute. Repeat for fan-outs.

The assignment list shows resolution status: orange `[missing]` means the
saved machine name doesn't match anything in the song — usually because
the project was loaded with that machine renamed or removed. Re-pick the
target from the dropdown to re-resolve.

**Delete** and **Clear** automatically unmute orphaned targets so you
don't have to hunt them down by hand.

---

## How it interacts with target machines

### Native machines

Clean. Native machines have no internal mute parameter, so the controller
is authoritative. Toggle `Mute` and the target stays where you put it.

### Managed machines without internal mute logic

Same as native. `IsMuted` is the only mute state and Pedal Muter wins.

### Managed machines with their own `MuteParam` (Pedal Tracker style)

These enforce **one-way authority** from their own internal parameter and
re-assert `IsMuted` from it every Work cycle. If you also drive their
`IsMuted` from Pedal Muter, you'll get a tug-of-war: their internal Mute
wins on each Work cycle and the UI flickers.

If this matters in practice, the cleanest fix is to add a parameter-aware
fallback that drives the target's own `Mute` parameter via
`IParameter.SetValue` instead of `IMachine.IsMuted` — the target's normal
one-way authority handles the rest. (Not implemented in v1.0; the hook is
`ResolveAssignment` if you want to add it.)

---

## Threading model

- **`IMachine.IsMuted` is set via `Dispatcher.BeginInvoke`.** Direct writes
  from the audio thread leave WPF bindings on the wrong thread, so the
  internal state mutates correctly but the machine-view box doesn't darken.
  See `SetMachineMutedUiSafe` in `PedalMuter.cs`.
- **`Buzz.Song.Machines` is never enumerated from `Work()`.** Resolution
  happens on the UI thread (constructor, `ImportFinished`, dialog
  operations); the audio thread reads cached `IMachine` references.
- **The setter dispatches immediately,** because the controller might
  itself be muted. ReBuzz's zero-CPU bypass skips `Work()` entirely while
  `IsMuted == true`, but `Tick()` (and therefore parameter setters) still
  fires. If we deferred mute application to `Work()`, mute changes from
  pattern automation during the controller's own bypass would never reach
  targets. The setter handles this by dispatching `IsMuted` immediately
  on every transition; `Work()` is just a re-assertion safety net.
- **Idempotent re-assertion.** `SetMachineMutedUiSafe` short-circuits when
  `target.IsMuted` already matches, so calling it every `Work()` cycle for
  every assignment costs only a few comparisons in the steady state.
- **Stale-voice flush at mute *and* unmute.** With "Process muted
  machines" off, ReBuzz's bypass freezes the target plugin's voice state
  — Tick and Work both stop, so any voice that was mid-attack or
  mid-sustain when bypass engaged stays paused inside the plugin. On
  unmute, that voice resumes and you hear it as a phantom re-trigger.

  Pedal Muter handles this with a two-sided flush:

  1. **At the mute transition:** write Note-Off (255) to every track of
     each target's note parameter and call `SendControlChanges` —
     delivered before bypass fully engages, so the plugin starts
     releasing the voice instead of freezing it mid-attack.
  2. **For the first ~32 buffers after each unmute transition:**
     `SetMute` arms a per-track counter; `Work()` sends Note-Off +
     `SendControlChanges` on each pass while the counter is non-zero.
     This handles the cases the mute-side flush misses — long-release
     voices that were frozen mid-attack rather than fully decayed, or
     plugins where the mute-time Note-Off didn't reach the voice because
     the target was already excluded from `CollectMachinesThatCanWork`.
     32 buffers is roughly 200–400 ms at typical sample rates: long
     enough to absorb the async `IsMuted=false` dispatch latency *and*
     to guarantee that at least one Note-Off reaches the plugin once it
     resumes rendering. Tunable via `STALE_FLUSH_BUFFERS` in
     `PedalMuter.cs` if very-long-release pads still leak through.

  Trade-off: a fresh note delivered on the same row as the mute or
  unmute transition gets killed. Acceptable in exchange for clean
  resumes across the 30+-tick bypass spans typical of live performance.

---

## Building

### Prerequisites

- Visual Studio 2022 or `dotnet` CLI ≥ 10.0
- ReBuzz installed (`BuzzGUI.Interfaces.dll`, `BuzzGUI.Common.dll`,
  `ReBuzz.dll` ship with every release)
- .NET 10 SDK

### Steps

```powershell
# Default ReBuzzDir is C:\Program Files\ReBuzz — override on the
# command line if your install lives elsewhere.

dotnet build -c Release
# or:
dotnet build -c Release /p:ReBuzzDir="D:\Tools\ReBuzz"
```

Output: `Pedal Muter.NET.dll` → `$(ReBuzzDir)\Gear\Generators\`.

Restart ReBuzz; **Pedal Muter** appears in the Generators tab in the
machine browser (it's a control machine, so OutputCount=0, but it lives
under Generators alongside PeerCtrl).

---

## File overview

| File | Purpose |
|------|---------|
| `PedalMuter.cs` | Machine class, track state, resolution, menu commands |
| `SettingsWindow.cs` | Code-only WPF assignments dialog |
| `PedalMuter.csproj` | .NET 10 / WPF project targeting x64 |
| `Directory.Build.props` | Defines `ReBuzzDir` (default `C:\Program Files\ReBuzz`) |

---

## Licence

MIT.
