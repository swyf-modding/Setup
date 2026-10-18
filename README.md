<div align="center">

# ScamWYF.Setup

![license](https://img.shields.io/badge/license-MIT-blue)
![game build](https://img.shields.io/badge/game-0.1.71--swyf-blue)
![Unity](https://img.shields.io/badge/Unity-6000.3.10f1-blue)
![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-blue)

*Built with:*

![.NET](https://img.shields.io/badge/.NET%2010-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.5-14172c?style=for-the-badge)
![Doorstop](https://img.shields.io/badge/Doorstop-4.5.0-14172c?style=for-the-badge)
![Mono.Cecil](https://img.shields.io/badge/Mono.Cecil-0.11.6-9a3d3d?style=for-the-badge)

> ⚠️ **Linux support is VERY experimental.** The Windows flow is the mature one. The Linux/Proton
> flow is validated on exactly one machine (NVIDIA + Wayland + current Proton builds) and nowhere
> else. Expect machine-specific quirks; `swyf-setup doctor` reports what your setup looks like,
> and the Linux section below lists the known traps (`winhttp=n,b` — never `n` alone, `-whisper-cpu`,
> and re-running setup after every game update).

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
> [Why the Corlib Override Is Not Optional](#why-the-corlib-override-is-not-optional).

> [!NOTE]
> `swyf-setup` modifies files in your game install and downloads BepInEx from GitHub. Every step prints
> what it is about to do, and `--offline` skips the download. Nothing is deleted at any point.

> [!NOTE]
> **On Linux the game runs under Proton.** Doorstop and BepInEx are therefore the Windows flavour, and
> Wine has to be told to prefer the game's own `winhttp.dll` or the loader never starts. See
> [Linux (Proton)](#linux-proton). Nothing else about the payload changes.

This project is not affiliated with or endorsed by the developers or publisher of Scam With Your Friends.

---

## Getting Started

### Prerequisites

- A legally installed copy of **Scam With Your Friends**, with the build listed under
  [Compatibility](#compatibility)
- Network access on the first run, to fetch BepInEx from GitHub. Not needed after that, or at all with
  `--offline`
- Nothing else. The download is one self-contained executable with the .NET runtime and the vendored
  Doorstop and unstripped corlib compiled inside it, on Windows and Linux alike

### Installation

Download `swyf-setup.exe` (Windows) or `swyf-setup` (Linux) from the releases, then run it:

```sh
./swyf-setup                 # finds the game, installs and repairs (the default command)
./swyf-setup doctor          # where things are, and what is missing
./swyf-setup verify          # check without changing anything
```

It finds the game (or take `--game-dir`, or set `SWYG_GAME_DIR`), then:

1. installs the loader — Doorstop from [`vendor/doorstop`](vendor/README.md), BepInEx 5.4.23.5
   fetched from [GitHub releases](https://github.com/BepInEx/BepInEx/releases)
2. seeds `unstripped_corlib/` from [`vendor/corlib`](vendor/README.md), the unstripped BCL
3. restores the awaiter methods Unity's stripper deleted
4. patches the remaining vtable breakages
5. verifies, and exits non-zero if anything is still wrong

Then build the mods — each is its own repo, with its own build script:

* [AI-Backend](https://github.com/swyf-modding/AI-Backend)
* [Mod-Handler](https://github.com/swyf-modding/Mod-Handler)

Or use [Launcher](https://github.com/swyf-modding/Launcher), which runs setup for you and lists the
mods afterwards.

Launch the game once so BepInEx writes `BepInEx/config/*.cfg`, edit the AI config, relaunch.

Safe to re-run. Steps 1 and 2 leave existing files alone, steps 3 and 4 rewrite only their own output,
and `Managed/` is never written to at any point.

### Developer Setup

```sh
git clone https://github.com/swyf-modding/Setup.git
cd Setup
./build.sh                   # one self-contained binary per platform, into dist/
dotnet run --project src/ScamWYF.Setup.Cli -- doctor
```

The logic is a plain .NET class library — `src/ScamWYF.Setup.Core` — that both this CLI and
[Launcher](https://github.com/swyf-modding/Launcher) call **in process**. There is nothing to install
but the .NET SDK. `Mono.Cecil` comes from NuGet, so the patching steps no longer depend on the game's
`BepInEx/core` already existing, and they work before the game has been touched at all.

### Usage

```sh
swyf-setup install --game-dir "/path/to/Scam With Your Friends"   # skip auto-detection
swyf-setup --offline                                              # do not download BepInEx
swyf-setup --bepinex-zip ~/BepInEx_win_x64_5.4.23.5.zip
swyf-setup play                                                   # start through Steam
```

| Command | What it does |
|---|---|
| *(none)* / `install` | Install or repair. The default |
| `verify` | Check an install without changing it. Exits non-zero on problems |
| `doctor` | Show every path it looked in, and which piece is missing |
| `play` | Start the game — through Steam, or directly through Proton with `--direct` |
| `unstrip` | Just the awaiter clone |
| `vtable` | Scan for vtable breakages; `--fix` patches them |
| `scan` | List BCL references an assembly needs that the stripped corlib lacks |
| `compare` | Diff the shipped BCL against the override |
| `patch-preloader` / `revert-preloader` | The diagnostic preloader patch |
| `payload` | Show which Doorstop and corlib binaries are in use |

| Option | What it does |
|---|---|
| `--game-dir` | Use this folder rather than searching Steam. Never second-guessed |
| `--offline` | Do not download BepInEx. Fails if it is not already installed |
| `--bepinex-zip` | Install BepInEx from a zip you already have |
| `--payload-dir` | Take Doorstop and corlib from a `vendor/` folder instead of the embedded copy |
| `--force` | Replace files that are already present |
| `--assemblies` | Restrict a patch step to these assemblies |
| `--no-awaiters`, `--no-fix` | Skip a patch step |
| `--direct` | `play`: launch through Proton directly rather than asking Steam |

`SWYG_GAME_DIR` is read as a fallback after `--game-dir` and before the usual Steam locations, and
`SWYG_SETUP_PAYLOAD` does the same for `--payload-dir`.

### Testing

`swyf-setup`'s final step is its own test: `verify` re-runs the vtable scan and fails the whole run if
anything remains.

```sh
swyf-setup && echo ready || echo "see the problems listed above"
```

Each step can also be run on its own against an existing install — `swyf-setup vtable` in report mode is
the useful one, since it is what `verify` reads.

To confirm the loader itself works, launch the game and check `BepInEx/LogOutput.log` exists — see
[Testing Without the Game Taking Over Your Screen](#testing-without-the-game-taking-over-your-screen).

No game DLL belongs in this repository or in a GitHub release.

---

## Compatibility

| Component | Verified Version |
|---|---|
| Scam With Your Friends | `0.1.71-swyf` |
| Unity | `6000.3.10f1`, Mono backend, managed stripping **on** |
| BepInEx | `5.4.23.5`, Mono preloader |
| Doorstop | `4.5.0` |
| Mono.Cecil | `0.11.6` (NuGet) |
| Runtime | .NET 10 (self-contained; nothing to install) |
| Platform | Windows x64, and Linux x64 under Steam Proton |

Other game builds are untested. The tool locates the game by its `Managed/mscorlib.dll` rather than
assuming a fixed layout, and reports what it actually finds.

---

## Linux (Proton)

**The game is a Windows Unity build, so on Linux it runs under Proton, and the loader is the Windows
one.** `winhttp.dll`, `BepInEx_win_x64_5.4.23.5.zip` and the `unityjit-win32` unstripped corlib are all
exactly what Windows uses. What changes is only the tooling: where Steam keeps its libraries, and how
the game is started.

There is one extra thing Wine needs. Wine prefers its own builtin `winhttp.dll` over the copy Doorstop
puts in the game folder, so unless it is told otherwise the loader is **never started and the game runs
completely unmodded with no error anywhere**. Tell it to prefer the game's copy, and keep the builtin
as fallback — `n` alone makes the game crash in Unity's window code a minute or so into startup:

```
WINEDLLOVERRIDES="winhttp=n,b" %command% -whisper-cpu
```

Set that as the game's Steam launch option (Properties → Launch Options). `swyf-setup play` writes it
into the prefix for you where it can, and `swyf-setup doctor` reports whether it is set.

```sh
swyf-setup doctor            # confirm the Steam library, the prefix and the override
swyf-setup                   # install or repair
swyf-setup play              # ask Steam to start the game
```

Steam is searched first at `~/.steam/steam`, `~/.steam/root` and `~/.local/share/Steam`, then whatever
`libraryfolders.vdf` names, so a game on a second drive is found the same way it is found on Windows. A
direct launch through a Proton build is the fallback for Lutris, Heroic or a terminal
(`swyf-setup play --direct`).

**No native Linux game build, and no Linux Doorstop.** `libdoorstop_x64.so` injects into a Linux ELF and
cannot load this game; there is nothing to port there.

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

Re-run `swyf-setup`. It is idempotent and reports anything it cannot fix.

If the game moved to a **different Unity version**, `vendor/corlib/` is stale: its assemblies must
come from the matching editor. Replace the eight files in `vendor/corlib/` with the same eight from

```
<that editor>\Editor\Data\MonoBleedingEdge\lib\mono\unityjit-win32\
```

and re-run. Nothing else in `vendor/` needs to change.

Then re-run `swyf-setup scan` to see whether a new BepInEx would still work, and expect
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

`swyf-setup unstrip` gives each affected awaiter an `OnCompleted(Action)` by **cloning the IL
body of its `UnsafeOnCompleted`**. In UniTask those two methods are implemented identically, so the
clone is faithful rather than a stub.

Awaiters are only one instance. **Any** interface whose contract widened can strand an implementor.
`swyf-setup vtable` does the whole job in two steps:

1. diff every interface in the shipped corlib against the unstripped one, keeping those whose
   required method set grew (new methods, or new base interfaces)
2. find every type in the game implementing one of those and missing the added methods, walking base
   classes across assemblies so inherited implementations do not count as missing

```sh
swyf-setup vtable                                    # report
swyf-setup vtable --fix                              # patch into the override folder
swyf-setup vtable --fix --assemblies a.dll,b.dll
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

`--fix` adds the missing methods as bodies that throw `NotImplementedException`. That is safe
precisely because the stripper only removes what nothing reachable calls: the method exists to
satisfy vtable layout, and if one ever does fire you get a clear exception naming it rather than
silent corruption from a faked return value. Where a faithful twin exists — as with
`UnsafeOnCompleted` → `OnCompleted` — prefer `swyf-setup unstrip`, which clones the real body.

---

## Tools

The individual steps behind `install`, each runnable on its own. They are the same code the install
runs, not a reimplementation, and they all use the `Mono.Cecil` the tool carries rather than the game's
copy — so they work before BepInEx is installed.

| Command | What it does |
|---|---|
| `swyf-setup unstrip` | Clones `UnsafeOnCompleted` into `OnCompleted` for the awaiters that need it. Run by `install`. |
| `swyf-setup vtable [--fix]` | Finds every type that cannot build a vtable against the unstripped corlib; `--fix` patches them. Run by `install`. |
| `swyf-setup compare` | Diffs the shipped BCL against an unstripped one — missing types, members and forwarders. |
| `swyf-setup scan` | Lists every BCL member/type an assembly needs that the game's stripped corlib lacks. Run it on `BepInEx/core` before trusting a new BepInEx version. |
| `swyf-setup patch-preloader` / `revert-preloader` | Rewrites `PlatformUtils.SetPlatform` to drop the `GetPEKind` call. Only useful for diagnosis. |

```sh
swyf-setup scan --managed "/path/to/Scam With Your Friends_Data/Managed" --dir "/path/to/BepInEx/core"
```

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
src/
|-- ScamWYF.Setup.Core/    The setup logic, as a library - discovery, loader, corlib, patches, verify
|   |-- GameInstall.cs     Finding and probing an install (Windows + Linux Steam)
|   |-- SteamEnvironment.cs Where Steam keeps its libraries and the Proton prefix
|   |-- LoaderInstaller.cs Doorstop from the payload, BepInEx from GitHub
|   |-- CorlibSeeder.cs    Seeds unstripped_corlib
|   |-- SetupRunner.cs     The five steps, in order
|   |-- SetupVerifier.cs   The verification pass
|   |-- ProtonIntegration.cs  The winhttp override and starting the game on Linux
|   `-- Patching/          The Mono.Cecil steps: awaiters, vtable breaks, diagnostics
`-- ScamWYF.Setup.Cli/     `swyf-setup` - the command-line front-end
vendor/                    Doorstop and the unstripped BCL, embedded into the published binary
build.sh                   One self-contained binary per platform, into dist/
```

Superseded, and kept only until the launcher port lands: `setup.ps1`,
`install-bepinex.ps1`, `GameDir.ps1` and `tools/*.ps1`. The library above is the source of truth now;
the scripts are what it was ported from.

---

## Continuous Builds

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push and pull request. Nothing here
needs the game, so a hosted runner is enough — unlike the mod repos, whose compile needs the game's own
assemblies.

| Job | What |
|---|---|
| `dotnet` | The projects build, and `swyf-setup install --offline` installs into a fabricated game folder and is a no-op on a second run. Runs on Linux |
| `vendor` | The eight corlib assemblies and the Doorstop files are present, non-empty, and carry their licence text |
| `parse` | Every `.ps1` parses cleanly. Superseded by the .NET tool; kept while the scripts are still in the tree |
| `loader-smoke` | `install-bepinex.ps1` installs the loader half into a fabricated game folder, and is a no-op on a second run. Superseded by `dotnet` |

The CI cannot check the parts that need the real install — the vtable scan against the game's real
assemblies, and whether the loader actually boots the game. Those need a machine with the game
installed, which is what `swyf-setup`'s own final `verify` step is for.

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