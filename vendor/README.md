# Vendored files

Two folders of binaries this repo carries so that `setup.ps1` works from a clean game install
without hunting through a Unity installation.

## `corlib/` — the unstripped BCL (Mono, MIT)

Eight assemblies from the Mono class libraries as shipped in the **Unity 6000.3.10f1** editor:

```
Editor/Data/MonoBleedingEdge/lib/mono/unityjit-win32/
```

| | |
|---|---|
| Licence | MIT — see `LICENSE-Mono`. Mono's runtime and class libraries are MIT. |
| Why these eight | They are the assemblies BepInEx and Harmony reference that the game's stripped `Managed\` copy cannot satisfy. Measured, not guessed: of everything BepInEx core needs, these are the ones missing. |
| Why not all 140 | The game ships a copy of everything else it uses in `Managed\`, so the override only has to supply what is missing. The full folder is 42MB of assemblies this game never loads. |

These are reference-quality copies of the BCL, not the game's files. The game's own assemblies —
`Assembly-CSharp.dll`, `UniTask.dll`, `Newtonsoft.Json.dll` and the rest — are **not** here and must
not be added. They are not ours to redistribute.

If the game is updated to a different Unity version, re-seed from that version's editor and re-run
`tools\find-vtable-breaks.ps1`. See "After a game update" in the main README.

## `doorstop/` — Unity Doorstop 4.5.0 (LGPL-2.1)

`winhttp.dll`, `.doorstop_version`, and a `doorstop_config.ini` already set to

```ini
dll_search_path_override=unstripped_corlib
```

which is the setting this game needs and the only reason Doorstop is here. See `LICENSE`.

The original `doorstop_config.ini` from the BepInEx pack is unmodified apart from that one line and
some trimmed comments.

**BepInEx itself is not vendored.** `install-bepinex.ps1` fetches
[BepInEx 5.4.23.5 win_x64](https://github.com/BepInEx/BepInEx/releases) (LGPL-2.1) and installs only
`BepInEx\core`, since the loader half comes from `doorstop\` here. Pass `-Offline` to skip the
download, or `-BepInExZip` to install from a zip you already have.