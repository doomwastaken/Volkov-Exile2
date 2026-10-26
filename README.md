# VolkovExile

A tiny Path of Exile 2 helper overlay for Windows. One portable `.exe`, ~25 MB RAM, tray icon in the
Volkov Commander style.

- **F8 — Waystone mods.** Hover a waystone; the number of its mods appears right after `(Tier X)`.
- **F7 — Craft lock.** While crafting in the currency tab, blocks your Shift+clicks on the craft slot as soon as
  the item matches your stash search (the gold highlight), so you don't roll over the mod you wanted.

Both work by reading screen pixels only. Nothing is injected into the game and game memory is never read.
The waystone overlay never touches input. The craft lock only *drops* your own clicks; it never sends any.

## Install

1. Download `VolkovExile.exe` (or build it, see below) and put it anywhere.
2. Run it. A tray icon appears (look under `^` if hidden); hover it to see what is on.
3. Optional autostart: `Win+R` → `shell:startup` → put a shortcut to the exe there.

Exit: right-click the tray icon → **Exit**. Only one copy runs at a time.

On first run Windows SmartScreen may warn about an unsigned file: **More info → Run anyway**.

### Game settings it expects

- **English** game client.
- **Fullscreen / windowed fullscreen** on one monitor (any monitor; the window is found by its title
  `Path of Exile 2`).
- Default UI scale. Tested at 2560×1440; other 16:9 resolutions are scaled from it.

## Usage

### F8 — waystone mod count

Hover a rare waystone. The label next to the header shows the state:

| Label | Meaning |
|---|---|
| yellow `... mods` | waystone recognized, counting |
| green `6 mods` | counted and confirmed on a second frame |
| red `? mods` | header found, mod lines unreadable (covered) |

Two-line mods (*Critical Hit Chance + Critical Damage Bonus*, *Ailment Threshold + Stun Threshold*) count as one.
Holding **Alt** (detailed tooltip) pauses it.

### F7 — craft lock

1. Open the currency tab, put the item in the craft slot, type your regex into **Highlight Items**.
2. Press **F7**: a **blue** frame appears around the slot.
3. Hold **Shift** and click the slot as fast as you like (an autoclicker works too).
   - After each click that gets through, further clicks are dropped until the new roll is on screen and checked.
   - On a match: **red** frame + divine orb drop sound, and all clicks are blocked until you release Shift.
4. Wrong match (e.g. wrong skill level)? Release Shift: the frame turns **gold**. The next Shift+click rerolls it.

The lock turns itself off when the craft slot has been off screen for 5 s (currency tab closed), so it doesn't
keep running if you forget it. Press F7 again to re-arm.

### F9 — anti-landmine (merchant purchases)

Protects Ctrl+click purchases in a merchant's shop from "landmines": one item among many similar ones priced
in a more expensive currency or with an extra zero.

1. Press **F9**, hold **Ctrl** and hover the first item of the series. Its price becomes the **reference**
   (gold label); that first Ctrl+click is blocked so you see the reference before buying.
2. Ctrl+click as usual. Each hovered item's price is read first (clicks before that are dropped):
   - green label — at most `MaxRatio` × the reference, the click goes through;
   - red label + sound — more expensive, a currency not in the ini, or an unreadable amount: blocked.
3. F9 twice resets the reference.

Currencies are compared via `VolkovExile.ini` next to the exe (created on first F9):

```ini
[AntiLandmine]
Exalted Orb = 1
Chaos Orb = 65
Divine Orb = 650
MaxRatio = 3
```

### Log

`VolkovExile.log` next to the exe, one line per event, flushed immediately. Watch it live:

```powershell
Get-Content .\VolkovExile.log -Wait
```

It has detection timings per waystone, per-roll timings of the craft lock and a stats line every 10 s.

## Build

No SDK or packages needed — the C# compiler and every library used ship with Windows 10/11.

```bat
build.cmd
```

Produces `dist\VolkovExile.exe` and `tools\wm_test.exe` (console test runner).

### Dependencies

All of them are part of Windows; nothing to download.

| What | Used for |
|---|---|
| .NET Framework 4.8 (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`) | runtime and `csc.exe` compiler (C# 5) |
| WinForms, System.Drawing | tray icon, overlay windows |
| Windows Runtime `Windows.Media.Ocr` (`%WINDIR%\System32\WinMetadata\*.winmd`) | reading two-line mods; needs the **English** OCR language, present on English Windows (Settings → Time & language → Language → add English if missing) |
| user32/gdi32 (P/Invoke) | screen capture, hotkeys, low-level mouse hook |

Dev-only tools (not needed to build or run):

| What | Used for |
|---|---|
| `ffmpeg` | made `src/divine.wav` from `samples/divine_drop.mp3`: `ffmpeg -i samples/divine_drop.mp3 -af volume=0.1667 -c:a pcm_s16le -ar 44100 src/divine.wav` |
| PowerShell | `tools/*.ps1` helpers for test images |
| `tools/MakeIcon.cs` | regenerates `src/volkov.ico`: `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe -r:System.Drawing.dll -out:tools\makeicon.exe tools\MakeIcon.cs` then `tools\makeicon.exe src\volkov.ico` |

### Tests

Detection runs on saved screenshots in `samples/` (2560×1440, plus `craft_1080.png` at 1920×1080):

```bat
tools\wm_test.exe --test samples\rare_crit.png     :: waystone: mod count
tools\wm_test.exe --slot samples\craft_mod.png     :: craft slot: present / highlighted / roll fingerprint
tools\wm_test.exe --price samples\landmine_4.png   :: merchant price in the tooltip (16x EXALTED ORB)
```

Expected: every `rare_*.png` → 6 mods; `craft_mod.png` → highlighted, other `craft_*.png` → not;
`craft_*.png` → present, `rare_*.png` → not present.
`tools/scale.ps1 -height 1080` makes rescaled copies to try other resolutions.

### Layout

```
src/Program.cs          entry point, tray, hotkeys, test runner (--test / --slot)
src/WaystoneOverlay.cs  F8 worker thread and label
src/Analyzer.cs         waystone header search, mod line counting, OCR of two-line mods
src/CraftLock.cs        F7 worker thread, mouse hook, frame, sound, auto-off
src/SlotDetector.cs     craft slot: presence, gold highlight, roll fingerprint
src/ScreenGrab.cs       allocation-free screen capture (BitBlt + GetDIBits)
src/Log.cs              VolkovExile.log
src/ws_tpl.png          "Waystone" header template (1440p)
src/volkov.ico          icon (generated by tools/MakeIcon.cs)
src/divine.wav          match sound
samples/                test screenshots
tools/                  icon generator, test image helpers
```

## Changelog

### 0.6 (anti-landmine branch)
- F9 anti-landmine: reads the merchant price under the cursor, blocks Ctrl+click on landmines.
- `VolkovExile.ini` with currency rates and the ratio.

### 0.5
- Craft lock turns itself off when the currency tab is closed (slot off screen 5 s).
- Waystone: Alt (detailed tooltip) pauses detection.
- Waystone: fixed 5-instead-of-6 when blue stash icons show through the tooltip between two mod lines.
- Match sound: divine orb drop, embedded, 1/6 of the original volume.
- `build.cmd` (no bash needed), README.

### 0.4
- Waystone: status colours (yellow counting / green done / red unreadable); runs on its own thread.
- Waystone: first sighting shown in ~45 ms instead of ~100 ms (OCR moved to the confirming frame).
- Craft lock: works with autoclickers — clicks are held until the new roll is on screen and checked
  (roll fingerprint from the tooltip's mod text, so non-matching → non-matching rerolls are seen too).
- Logging to `VolkovExile.log`.

### 0.3
- Renamed to VolkovExile; Volkov Commander style icon.
- Craft lock: release Shift over a match to accept it and reroll.

### 0.2
- F7 craft lock: frame around the slot, sound and Shift+click blocking on the search highlight.
- Faster waystone detection: scan only after the cursor stops, track the tooltip area.

### 0.1
- F8 waystone mod counter: template match of the header, blue line counting, OCR for two-line mods.

## Roadmap

- [x] Auto-off for the craft lock when the currency tab is closed.
- [ ] Anti-landmine: per-currency ratios; tablet "uses remaining" check.
- [ ] Auto-off for the waystone overlay when no waystone was seen for a while (or the stash is closed).
- [ ] Magic (blue-header) waystones.
- [ ] Verify on 1080p and other resolutions with real screenshots; non-16:9 aspect ratios.
- [ ] Settings file: hotkeys, volume, auto-off delay.
- [ ] Craft slot position for non-default UI layouts (it is fixed at the 1440p reference now).
- [ ] Faster waystone counting: replace OCR with templates for the two two-line mods (~25 ms less).
