<div align="center">

# ScamWYF.Setup

![license](https://img.shields.io/badge/license-MIT-blue)
![game build](https://img.shields.io/badge/game-v82--playtest-blue)
![Unity](https://img.shields.io/badge/Unity-6000.3.10f1-blue)

*Built with:*

![PowerShell](https://img.shields.io/badge/PowerShell-512BD4?style=for-the-badge&logo=powershell&logoColor=white)
![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.5-14172c?style=for-the-badge)
![Doorstop](https://img.shields.io/badge/Doorstop-4.5.0-14172c?style=for-the-badge)
![Mono.Cecil](https://img.shields.io/badge/Mono.Cecil-0.11.6-9a3d3d?style=for-the-badge)

</div>

---

## Table of Contents

- [Overview](#overview)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [Installation](#installation)
  - [Developer Setup](#developer-setup)
  - [Usage](#usage)
  - [Testing](#testing)
- [Compatibility](#compatibility)
- [What Is Vendored Here](#what-is-vendored-here)
- [Why the Corlib Override Is Not Optional](#why-the-corlib-override-is-not-optional)
- [After a Game Update](#after-a-game-update)
- [Why UniTask, Newtonsoft and the Desktop Broke](#why-unitask-newtonsoft-and-the-desktop-broke)
- [Tools](#tools)
- [Testing Without the Game Taking Over Your Screen](#testing-without-the-game-taking-over-your-screen)
- [Project Structure](#project-structure)
- [Continuous Builds](#continuous-builds)
- [Older Notes](#older-notes)
- [Security](#security)
- [License](#license)
- [Related Projects](#related-projects)

---

## Overview

**ScamWYF.Setup** is an unofficial, community-built installer and repair script for **Scam With Your
Friends**. It makes the game loadable under BepInEx, and keeps it loadable after a game update. No mods
are built here — each is its own repo.

**Key Features:**

- **One command from a clean install.** Loader, BepInEx, the corlib override, the stripped-method
  restore and the vtable patches, in that order, with a verification pass that exits non-zero if anything
  is still wrong
- **Puts the loader back after a game update.** Re-run it and it reports anything it cannot fix
- **Never writes to `Managed\`.** Steps 3 and 4 only ever write to `unstripped_corlib\`, so a failed
  repair cannot leave the game's own assemblies modified
- **No redistributed game DLLs, decompiled game source, telemetry, or credential collection**

> [!IMPORTANT]
> This game ships a **stripped** `mscorlib`, and BepInEx cannot start without an unstripped one. The
> corlib override in step 2 is not an optimisation or a workaround — see
> [Why the Corlib Override Is Not Optional](#why-the-corlib-override-is-not-optional). If you only want
> the loader and accept that the game will not boot, use
> [`install-bepinex.ps1`](install-bepinex.ps1) on its own.

> [!NOTE]
> `.\setup.ps1` modifies files in your game install and downloads BepInEx from GitHub. Every step prints
> what it is about to do, and `-Offline` skips the download. Nothing is deleted at any point.

This project is not affiliated with or endorsed by the developers or publisher of Scam With Your Friends.

---

## Getting Started

### Prerequisites

- A legally installed copy of **Scam With Your Friends**, with the build listed under
  [Compatibility](#compatibility)
- Windows x64 with Windows PowerShell 5.1 or PowerShell 7+ — the only runtime these scripts need
- Network access on the first run, to fetch BepInEx from GitHub. Not needed after that, or at all with
  `-Offline`

### Installation

From a clean game install, one command:

```powershell
git clone https://github.com/swyf-modding/Setup.git
cd Setup
.\setup.ps1
```

It finds the game (or take `-GameDir`, or set `SWYG_GAME_DIR`), then:

1. installs the loader — Doorstop from [`vendor\doorstop`](vendor/README.md), BepInEx 5.4.23.5
   fetched from [GitHub releases](https://github.com/BepInEx/BepInEx/releases)
2. seeds `unstripped_corlib\` from [`vendor\corlib`](vendor/README.md), the unstripped BCL
3. restores the awaiter methods Unity's stripper deleted
4. patches the remaining vtable breakages
5. verifies, and exits non-zero if anything is still wrong

Then build the mods — each is its own repo, with its own `build.ps1`:

* [AI-Backend](https://github.com/swyf-modding/AI-Backend)
* [Mod-Handler](https://github.com/swyf-modding/Mod-Handler)

Or use [Launcher](https://github.com/swyf-modding/Launcher), which runs `setup.ps1` for you and lists the
mods afterwards.

Launch the game once so BepInEx writes `BepInEx\config\*.cfg`, edit the AI config, relaunch.

Safe to re-run. Steps 1 and 2 leave existing files alone, steps 3 and 4 rewrite only their own output,
and `Managed\` is never written to at any point.

### Developer Setup

There is nothing to build. Clone and run:

```powershell
git clone https://github.com/swyf-modding/Setup.git
cd Setup
.\setup.ps1 -GameDir "C:\...\steamapps\common\Scam With Your Friends"
```

No submodule, no NuGet, no `.csproj`. The only dependencies are `Mono.Cecil` — which the tools load from
the game's `BepInEx\core`, and which is not needed until BepInEx is installed — and PowerShell itself.

### Usage

```powershell
.\setup.ps1 -GameDir "C:\...\Scam With Your Friends"   # skip auto-detection
.\setup.ps1 -Offline                                    # do not download BepInEx
.\setup.ps1 -BepInExZip C:\downloads\BepInEx_win_x64_5.4.23.5.zip
.\install-bepinex.ps1                                  # just the loader, if that is all you need
```

| Flag | Script | What it does |
|---|---|---|
| `-GameDir` | all | Use this folder rather than searching Steam. Never second-guessed |
| `-Offline` | `setup.ps1`, `install-bepinex.ps1` | Do not download BepInEx. Fails if it is not already installed |
| `-BepInExZip` | `setup.ps1`, `install-bepinex.ps1` | Install BepInEx from a zip you already have |
| `-Fix` | `tools\find-vtable-breaks.ps1` | Patch the breakages instead of only reporting them |
| `-Revert` | `tools\patch-preloader.ps1` | Undo the diagnostic preloader patch |

`SWYG_GAME_DIR` is read as a fallback after `-GameDir` and before the usual Steam locations, so it works
across all the scripts at once.

### Testing

`setup.ps1` is its own test: step 5 re-runs the vtable scan and fails the whole run if anything remains.

```powershell
.\setup.ps1
if ($LASTEXITCODE -eq 0) { 'ready' } else { 'see the problems listed above' }
```

The individual tools can be run against an existing install without re-running the whole thing —
`tools\find-vtable-breaks.ps1` in report mode is the useful one, since it is what step 5 reads.

To confirm the loader itself works, launch the game and check `BepInEx\LogOutput.log` exists — see
[Testing Without the Game Taking Over Your Screen](#testing-without-the-game-taking-over-your-screen).

No game DLL belongs in this repository or in a GitHub release.

---

## Compatibility

| Component | Verified Version |
|---|---|
| Scam With Your Friends | `v82-playtest` |
| Unity | `6000.3.10f1`, Mono backend, managed stripping **on** |
| BepInEx | `5.4.23.5`, Mono preloader |
| Doorstop | `4.5.0` |
| Mono.Cecil | `0.11.6` (from `BepInEx\core`) |
| PowerShell | 5.1 or 7+ |
| Platform | Windows x64 |

Other game builds are untested. The scripts locate the game by its `Managed\mscorlib.dll` rather than
assuming a fixed layout, and report what they actually find.

---

## What Is Vendored Here

`vendor\` carries the two sets of binaries that are ours to give away, so that installing does not mean
hunting through a Unity installation:

| | |
|---|---|
| `vendor\corlib\` | 8 unstripped BCL assemblies, from Mono's MIT-licensed class libraries as shipped in the 6000.3.10f1 editor. 11.8MB. |
| `vendor\doorstop\` | Doorstop 4.5.0, with `doorstop_config.ini` already pointing at the override folder. 26KB of binaries, LGPL-2.1. |

BepInEx itself is fetched rather than vendored, and **the game's own assemblies are not here at
all** — `Assembly-CSharp.dll`, `UniTask.dll` and the rest are not ours to redistribute, so they stay
in your install and get patched in place. See [`vendor\README.md`](vendor/README.md) for how the
eight were chosen and what to do when Unity changes version.

---

## Why the Corlib Override Is Not Optional

This build has Unity managed stripping enabled, so the shipped `mscorlib.dll` is missing API that
BepInEx and Harmony need. Measured against the shipped BCL, BepInEx's core assemblies reference
**142** members/types that simply are not there, including:

```
MethodBase.GetMethodBody          Module.ResolveType / ResolveField / ResolveMember
RuntimeMethodHandle.GetFunctionPointer   RuntimeHelpers.PrepareMethod
Reflection.Emit.DynamicMethod (ctors)    Reflection.Emit.SignatureHelper
Assembly.LoadFile                 Module.GetPEKind
```

Those are the foundation of runtime patching — there is no patching your way around them.
`tools\patch-preloader.ps1` fixes the *first* crash (`Module.GetPEKind`) for curiosity's sake, but
the next one is `Assembly.LoadFile` and it does not end. **Use the corlib override**, which is what
`dll_search_path_override=unstripped_corlib` in the vendored `doorstop_config.ini` does.

---

## After a Game Update

Re-run `setup.ps1`. It is idempotent and reports anything it cannot fix.

If the game moved to a **different Unity version**, `vendor\corlib\` is stale: its assemblies must
come from the matching editor. Replace the eight files in `vendor\corlib\` with the same eight from

```
<that editor>\Editor\Data\MonoBleedingEdge\lib\mono\unityjit-win32\
```

and re-run. Nothing else in `vendor\` needs to change.

Then re-run `tools\scan-missing-apis.ps1` to see whether a new BepInEx would still work, and expect
the mod repos to need a rebuild — they compile against the game's own assemblies on purpose.

---

## Why UniTask, Newtonsoft and the Desktop Broke

The cause is **not** a missing API, and not metadata drift. Unity's stripper edits `mscorlib` **and
every other assembly together, as a matched pair**. Restoring only `mscorlib` breaks that pact.

Concretely, in the shipped build — re-verified directly against this game's assemblies:

| | shipped (stripped) | unstripped |
|---|---|---|
| `INotifyCompletion` | **deleted entirely** | exists, `OnCompleted(Action)` |
| `ICriticalNotifyCompletion` | extends **nothing**, only `UnsafeOnCompleted(Action)` | extends `INotifyCompletion` |

Because nothing could call `OnCompleted` any more, the stripper also deleted `OnCompleted(Action)`
from **every awaiter type in the game**. Put the unstripped `mscorlib` back and
`ICriticalNotifyCompletion` demands `OnCompleted` again — which those awaiters no longer have. Mono
cannot build their vtable:

```
TypeLoadException: Generic type definition failed to init, due to:
VTable setup of type Cysharp.Threading.Tasks.UniTask`1+Awaiter[T] failed
```

That kills **every `await` in the game**, which is why the desktop renders empty and why hosting
crashes in `ReviewWhiteboardController.InitializeAsync`.

### The fix, and the general case

`tools\unstrip-awaiters.ps1` gives each affected awaiter an `OnCompleted(Action)` by **cloning the IL
body of its `UnsafeOnCompleted`**. In UniTask those two methods are implemented identically, so the
clone is faithful rather than a stub.

Awaiters are only one instance. **Any** interface whose contract widened can strand an implementor.
`tools\find-vtable-breaks.ps1` does the whole job in two steps:

1. diff every interface in the shipped corlib against the unstripped one, keeping those whose
   required method set grew (new methods, or new base interfaces)
2. find every type in the game implementing one of those and missing the added methods, walking base
   classes across assemblies so inherited implementations do not count as missing

```powershell
.\tools\find-vtable-breaks.ps1                       # report
.\tools\find-vtable-breaks.ps1 -Fix                  # patch into the override folder
.\tools\find-vtable-breaks.ps1 -Fix -Assemblies a.dll,b.dll
```

On this build it restores the six UniTask awaiters and then patches four assemblies:

| Assembly | Missing |
|---|---|
| `System.Text.Json.dll` | `Utf8JsonWriter.DisposeAsync`, `ArrayBufferWriter<T>.GetSpan`, async-enumerable `DisposeAsync` |
| `System.IO.Pipelines.dll` | `PipeWriter.GetSpan` |
| `Unity.InputSystem.dll` | `RemoteInputPlayerConnection.OnError` / `.OnCompleted` |
| `Newtonsoft.Json.dll` | interface methods lost with the same stripper |

All four are the **game's own assemblies**, copied into the override folder with the missing methods
added. `System.Data.dll` also needs 24 on `DataView` / `DataRowView`, which the game almost
certainly never loads; it is left alone by default.

`-Fix` adds the missing methods as bodies that throw `NotImplementedException`. That is safe
precisely because the stripper only removes what nothing reachable calls: the method exists to
satisfy vtable layout, and if one ever does fire you get a clear exception naming it rather than
silent corruption from a faked return value. Where a faithful twin exists — as with
`UnsafeOnCompleted` → `OnCompleted` — prefer `unstrip-awaiters.ps1`, which clones the real body.

---

## Tools

| Script | What it does |
|---|---|
| `tools\unstrip-awaiters.ps1` | Clones `UnsafeOnCompleted` into `OnCompleted` for the six awaiters that need it. Run by `setup.ps1`. |
| `tools\find-vtable-breaks.ps1` | Finds every type that cannot build a vtable against the unstripped corlib; `-Fix` patches them. Run by `setup.ps1`. |
| `tools\compare-corlib.ps1` | Diffs the shipped BCL against an unstripped one — missing types, members and forwarders. |
| `tools\scan-missing-apis.ps1` | Lists every BCL member/type an assembly needs that the game's stripped corlib lacks. Run it on `BepInEx\core` before trusting a new BepInEx version. |
| `tools\patch-preloader.ps1` | Rewrites `PlatformUtils.SetPlatform` to drop the `GetPEKind` call. Only useful for diagnosis; `-Revert` undoes it. |

```powershell
.\tools\scan-missing-apis.ps1 `
  -Cecil   "$GameDir\BepInEx\core\Mono.Cecil.dll" `
  -Managed "$GameDir\Scam With Your Friends_Data\Managed" `
  -ScanDir "$GameDir\BepInEx\core"
```

All five need Mono.Cecil, which they load from `BepInEx\core`.

---

## Testing Without the Game Taking Over Your Screen

```powershell
& ".\Scam With Your Friends.exe" -batchmode -nographics
```

BepInEx still initialises and writes `BepInEx\LogOutput.log`, so plugin loading and Harmony patching
can be verified headlessly. A preloader crash lands in `preloader_*.log` in the game root.

---

## Project Structure

```text
setup.ps1                  The one-command install and repair
GameDir.ps1                Finding the game install, shared by every script
install-bepinex.ps1        Just the loader: Doorstop from vendor\, BepInEx from GitHub
tools/
|-- unstrip-awaiters.ps1   Clones the awaiter methods the stripper deleted
|-- find-vtable-breaks.ps1  General version of the above, plus -Fix
|-- compare-corlib.ps1     Shipped BCL vs unstripped
|-- scan-missing-apis.ps1  What an assembly needs that the shipped corlib lacks
`-- patch-preloader.ps1    Diagnostic preloader patch
vendor/                    Doorstop and the unstripped BCL
```

---

## Continuous Builds

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push and pull request. Nothing here
needs the game, so a hosted runner is enough — unlike the mod repos, whose compile needs the game's own
assemblies.

| Job | What |
|---|---|
| `parse` | Every `.ps1` parses cleanly. A syntax error is the only way a script here fails without the game |
| `vendor` | The eight corlib assemblies and the Doorstop files are present, non-empty, and carry their licence text |
| `loader-smoke` | `install-bepinex.ps1` installs the loader half into a fabricated game folder, and is a no-op on a second run |

The CI cannot check the parts that need the real install — the vtable scan, the stripped-method restore,
and whether the loader actually boots the game. Those need a machine with the game installed, which is
what `setup.ps1`'s own step 5 is for.

---

## Older Notes

Ruled out along the way, kept because it saves repeating the work.

### What was *not* the cause of the `System.Text.Json` failure

Direct comparison of the shipped BCL against the unstripped one:

* not a Unity version mismatch — the unstripped set is from the same 6000.3.10f1 installer
* not missing members — the unstripped set is a strict **superset**; the only two absences are
  Unity's own `$__Stripped*` stubs on `IRestrictedErrorInfo`
* not missing types, and not missing **type forwarders** — both sides are identical
  (`System` 6, `System.Core` 20, everything else 0)
* not newly duplicated types between the override folder and the facades left in `Managed\`

`System.Text.Json` **was** genuinely broken — just as a second, independent instance of the same
vtable bug, not as the cause of the first. UniTask was what killed the desktop.

### Files that must not be copied into the override folder

Relevant if you ever re-seed from an editor install instead of using `vendor\corlib\`. These four
were moved out of the override folder by hand, because they break references the game never had:

| Do not copy | Why it is safe to leave out |
|---|---|
| `System.Runtime.Serialization.dll` | references `System.ServiceModel.Internals`, which is in neither `Managed\` nor the override folder; the game's own stripped copy has no such dependency |
| `System.Json.dll`, `System.Json.Microsoft.dll` | never shipped with the game; the second also drags in `System.Runtime.Serialization` |
| `System.ComponentModel.DataAnnotations.dll` | never shipped with the game |

`System.Security.dll` and `Mono.Security.dll` **do** belong there — the unstripped `System.dll` and
`System.Configuration.dll` genuinely reference them, and both are in `vendor\corlib\`.

### Lead — `AsyncMethodBuilderAttribute` is defined twice (unverified)

`System.Runtime.CompilerServices.AsyncMethodBuilderAttribute` is defined in **both** `mscorlib` and
`UniTask.dll`. Every `async UniTask<T>` method binds to one of them, and which one wins depends on
assembly binding order — which is exactly what swapping the corlib changes. That fits the evidence
but is not confirmed; it needs a real exception from a running session.

To capture one: set `[Logging.Disk] LogLevel = All` in `BepInEx\config\BepInEx.cfg`, reproduce the
broken desktop, and read `BepInEx\LogOutput.log`.

**Worth testing next:** the game's own `Newtonsoft.Json.dll` is now shadowed by a patched copy in the
override folder. If the desktop works, try deleting that patched copy and letting the game's
original load. Change one thing at a time.

---

## Security

Please do not publish suspected vulnerabilities or private game data in a public issue. Please do not
paste `BepInEx\LogOutput.log` contents in public: mods in this ecosystem configure API keys with it.

These scripts write into your game install, replace files in the game root (`winhttp.dll`,
`doorstop_config.ini`), and seed an override folder that the loader will prefer over `Managed\`. That is
what makes the game loadable, but it does mean the scripts need to be run from a source you trust —
clone from the repository rather than downloading a copy from elsewhere.

BepInEx is downloaded over HTTPS from
[its GitHub releases](https://github.com/BepInEx/BepInEx/releases) and pinned to 5.4.23.5. Use `-Offline`
or `-BepInExZip` if you would rather supply it yourself.

---

## License

MIT — Copyright © 2026 Ras_rap. See [LICENSE](LICENSE).

That covers the scripts and tools. The third-party binaries under `vendor\` keep their own terms:
`vendor\corlib\` is MIT (Mono) and `vendor\doorstop\` is LGPL-2.1, each with its licence text beside it.
The game's own assemblies are not redistributed here at all.

---

## Related Projects

| Project | What it is |
|---|---|
| [mod-lib](https://github.com/swyf-modding/mod-lib) | Shared library: base class, menu, config editor, hot reload, patch coordinator |
| [AI-Backend](https://github.com/swyf-modding/AI-Backend) | Routes the game's AI calls to your own LLM |
| [Mod-Handler](https://github.com/swyf-modding/Mod-Handler) | The in-game **Plugins** tab — turn mods off without leaving a session |
| [Launcher](https://github.com/swyf-modding/Launcher) | Installs, launches, and manages mods from outside the game |