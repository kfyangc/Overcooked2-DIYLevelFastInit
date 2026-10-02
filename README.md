# DIYLevelFastInit

A startup-acceleration patch for the [OC2DIYLevel](https://github.com/gua248/Overcooked2-LevelEditor) custom-level plugin of *Overcooked! 2*: **the main menu no longer freezes for 20+ seconds** — community level bundles load in the background with a live progress indicator.

[中文说明](README_CN.md)

## The problem

When the main menu loads your save, the OC2DIYLevel plugin (via a Harmony postfix on `MetaGameProgress.ByteLoad` → `DIYLevelAssetBundleManager.Initialize()`) does all of the following synchronously on the main thread:

- `AssetBundle.LoadFromFile` on every `common*` dependency bundle and every level set's `info_*` bundle under `levels/` (with a large collection — e.g. 135 files / 353 MB — this blocks for 20–30 seconds, during which the game appears frozen);
- no idempotence guard, so a second save load reloads everything;
- `commonW1`/`commonW2`, when already kept resident by another loader (e.g. OC2DIYLevelRuntimeWLoader), are still read in full before Unity rejects the duplicate — a couple of wasted seconds;
- the level-set menu's button list is a snapshot taken when the menu is first created (an `if (menu != null) return` guard), so entries that finish loading later never appear.

The more level sets you install, the longer the freeze.

## How it works

This plugin replaces `DIYLevelAssetBundleManager.Initialize` with a Harmony prefix:

1. **The cheap part stays synchronous** (the small `common` bundle, static fields, DLC data — milliseconds), so `IsInitialized` becomes true immediately and every downstream patch behaves exactly as before.
2. **The heavy lifting moves to a coroutine**: `common*` and all `info_*` bundles load via `LoadFromFileAsync`; info bundles are prefetched in parallel (all read requests queued at once) and consumed in directory order, so the level-set order stays stable (the arcade mod's positional mapping depends on it).
3. **Already-resident bundles are skipped** (matched by bundle name, saving the duplicate re-read); any async request that doesn't finish within 15 seconds falls back to the synchronous `LoadFromFile`/`LoadAsset` path, which is what the original plugin used.
4. **Idempotence fixed**: repeated `ByteLoad` calls no longer reload everything.
5. **Self-healing UI**: an `AddUI` postfix rebuilds the level-set button list whenever it is out of sync with the loaded list; while loading, new entries are appended one by one without flicker.
6. **Progress indicator**: an on-screen badge `DIY levels loading N/M` in the top-right corner, and the "More Levels" menu title shows live progress.

## Result

- The main menu is interactive right after launch — no more freeze.
- Level bundles finish loading in the background (duration depends on your collection; ~20 s for 353 MB in testing), with progress on screen.
- Once loading completes, the "More Levels" list is complete and usable.

## Install / Uninstall

Requirements: BepInEx 5.4.x and the [OC2DIYLevel plugin](https://github.com/gua248/Overcooked2-LevelEditor/releases) (`dev.gua.overcooked.diylevel`; tested with 0.10.0).

- Install: drop `DIYLevelFastInit.dll` into `BepInEx/plugins/`.
- Uninstall: delete the file. Nothing about OC2DIYLevel itself or your saves is modified.

## Building

You need a .NET SDK and a game installation with BepInEx + OC2DIYLevel:

```bash
dotnet build -c Release -p:GameDir="C:\Path\To\Overcooked! 2"
```

If `GameDir` is omitted, the default in the `.csproj` is used (the author's machine path — pass the property or edit the file on a different machine). Output: `bin/Release/DIYLevelFastInit.dll`.

## Disclaimer

Third-party community patch; not affiliated with Team17 or gua248. Developed through interop analysis of the OC2DIYLevel plugin; contains none of its original code.
