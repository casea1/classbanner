# Banner 3.1 — Analysis of the Original

Reverse-engineered from the install package (`Banner3.1.msi`, `Autorun.exe/.inf/.dat`,
`Unclassified.reg`). No source code was available; everything below comes from MSI tables,
the extracted files, embedded strings, and the bundled readme/tips.

## Identity

| | |
|---|---|
| Product | Banner 3.1 (file version 3.01.0075, built March–May 2003) |
| Author | 690th Information Operations Group / 690 CSS/ADL, HQ Air Intelligence Agency (USAF) |
| Purpose | "Places a banner at the top and/or bottom of the screen" showing the system's classification |
| Language | Visual Basic 6 (needs `msvbvm60.dll`, MSCOMCTL/COMDLG32 OCXs) |
| Installer | InstallShield Express 3.5 MSI, admin rights required, reboot required |
| Supported OS | Windows NT 4.0 SP3+, 2000, XP |
| Predecessors | Banner 1.0 / 2.0 ("ClassificationBar", `C:\Program Files\USAF\ClassBar`, launched as `Kernel.exe` from Startup), 3.0 |

## What's in the package

| File | Role |
|---|---|
| `Banner.exe` (618 KB) | The banner app. Also an out-of-proc COM server (`Banner.GlobalCode`, `Banner.Settings`, `Banner.*Policy`) |
| `Banner.chm` | HTML Help: a single page describing the right-click menu (content folded into this doc) |
| `BnrTips.dll` | Actually a plain-text file: Tip-of-the-Day lines |
| `Readme.rtf` | Install notes / known issues |
| VB6 runtime + OCXs | `msvbvm60.dll`, `mscomctl.ocx`, `comdlg32.ocx`, `mscomm32.ocx`, `oleaut32.dll`, etc. |
| `BnrRmove.exe` (embedded custom action) | Removes Banner 1.0/2.0 and gracefully stops 3.x before upgrade (`/s` silent, `/p` package mode) |
| `Autorun.exe` | CD menu: "Install Banner 3.1" (`\Setup.exe`) and "Configure Banner" (`\Admin\BnrAdmin.exe`) |
| `*.reg` (e.g. `Unclassified.reg`) | Pre-built settings per classification |

**Not yet provided:** `\Admin\BnrAdmin.exe` (the admin/config tool), `\Setup.exe`, the `RemveOld` folder,
and any other `.reg` presets.

## Installer behavior

* Feature tree "Classification Levels" with three mutually-exclusive-by-convention children:
  **Unclassified**, **Secret**, **TS/SCI**. The readme warns you must pick only ONE (nothing enforces it).
* Each feature just writes a block of registry values (below).
* Adds `HKLM\...\CurrentVersion\Run\Banner` so it starts for every user.
* Writes `WinLogon\Welcome = "- <LEVEL>"` so the logon/lock/security dialogs carry the marking.
* Writes into `HKEY_USERS\.Default` so the pre-logon desktop matches.
* Known installer issue: Error 1911 (type library registration) — "click Ignore".

## Configuration model (`HKLM\Software\USAF\Banner`)

VB colors are stored as decimal **BGR** (`&H00BBGGRR`).

### Main
| Value | Meaning | Unclassified | Secret | TS/SCI |
|---|---|---|---|---|
| `Caption` | Banner text | `UNCLASSIFIED` | `SECRET` | `TOP SECRET SCI` |
| `BackColor` | Banner background | 32768 → `#008000` green | 128 → `#800000` maroon | 65535 → `#FFFF00` yellow |
| `ForeColor` | Banner text | `#FFFFFF` | `#FFFFFF` | `#000000` |
| `Position` | Top / Bottom / Both (3 = Both, inferred) | 3 | 3 | 3 |
| `Transparency` | % opacity drop when mouse hovers | 15 | 15 | 15 |
| `CascadeRecoveredWindows` | Cascade windows rescued from under banner | 1 | | |
| `IgnoredWindowClasses` | `;`-separated window classes to never move | (default list below) | | |
| `ExpansionDelimiter` | Character used to space out caption text | `" "` | | |
| `Logging` / `LogFilename` | Debug log | 0 / `C:\Banner.log` | | |
| `ShowSplashAtStartup`, `ShowFeedbackOptions` | UI toggles | 1 | | |
| `SecurityDialogSuffix` | Suffix added to logon/lock dialog titles | | | |

Default ignored window classes:
`BASEBAR;TOOLTIPS_CLASS32;SHELL_TRAYWND;PROGMAN;NAI_VS_STAT;ITOUCHSTATUS;ZAFRAMEWND;MOM CLASS;HOM HIT CLASS;STATIC`

### `\Colors` — Windows theme enforcement (`Enforce=1`)
Forces system colors (`SetSysColors`) to match the level: `ActiveTitle`, `GradientActiveTitle`,
`TitleText`, `Hilight`, `HilightText`, `MenuHilight`, `Background` (desktop).
Example Unclassified: green title bars/selection highlight, white text.

### `\ScreenSaver` (`Enforce=1`)
Forces the built-in **Marquee** screensaver (`SSMarque.scr`) scrolling the classification text,
level-colored background, Times New Roman 72pt, speed 9, password-protected, 10-minute timeout.

### `\Wallpaper` (`Enforce=0` by default)
Can force a wallpaper (tiled/centered/stretched) and enable Active Desktop if needed.

### `\Font` (`Enforce=0`), `Size=10`
Banner font size; also adapts to the user's caption font size (non-client metrics).

### `\Clock` (`Enabled=0` by default)
Optional clock on the banner: format `ddd, dd-mmm-yy h:mm ampm %TZI%`, local→Zulu conversion,
`L`/`Z` indicators. Also supports military DTG style `ddhhmm%tzi% mmm yy`.

Per-user state (`CurrentUser`): `ShowTipsAtStartup`, `CurrentTip`.

## Runtime features (from code symbols, menus and tips)

* **AppBar docking** (`SHAppBarMessage`): reserves screen space at top/bottom so maximized windows don't
  cover it. Handles auto-hide conflicts ("Only one auto hidden window is allowed on each edge").
* **Multi-monitor**: one banner per monitor (`Monitors`/`Monitor` classes, `MonitorFromWindow`).
* **Full-screen awareness**: changes behavior for full-screen apps like PowerPoint.
* **Mouse-over transparency** (`SetLayeredWindowAttributes`, `tmrMouseTransparency`).
* **Off-screen / covered window recovery**: double-click banner or menu "Recover Off-Screen Windows"
  (enumerates windows, optional cascade); "Drag Covered Window" mode makes the banner click-through for ~8s.
* **Policy enforcement objects** (`ColorPolicy`, `ScreenSaverPolicy`, `WallpaperPolicy`, `FontPolicy`,
  `ClockPolicy`) each with `Enforce`, `EnforceNow`, `DoStartupChecks`, and a `WillChangeSetting` event
  (lets the app veto/react when settings change). Re-applies on `WM_SETTINGCHANGE`.
* **Logon/lock marking**: "Begin Logon", "Computer Locked", "Windows Security", "Unlock Computer" titles
  get the classification via `WinLogon\Welcome`.
* **Office Shortcut Bar** compatibility.
* **COM automation** (`Banner.GlobalCode`, `GetSharedBannerSettingsInstance`) so the admin tool can
  read/change live settings.
* **Export settings** to `C:\Banner.reg` (Debug menu).

### Right-click menu
Recover Off-Screen Windows · Drag Covered Window · Debug ▸ (Enforce Color Policy Now, Enforce Screen Saver
Policy Now, Enforce Wallpaper Policy Now, Export Settings to File, Close) · Show Tips at Startup ·
Tip of the Day · What's This? · Help · Send Feedback… · About Banner

The help file adds: "Drag Covered Window" exists because "Recover Off-Screen Windows" doesn't always
work, and "Send Feedback" only works on NIPRNet machines (it opens a `mailto:`).

## Weaknesses / opportunities for "better-banner"

1. **Obsolete platform.** VB6 + 2003-era OCXs, NT4–XP target; COM registration errors (Error 1911);
   ships system DLLs into `System32`. Needs a modern, self-contained build for Windows 10/11.
2. **Classification palette is incomplete/outdated.** Only U / S / TS-SCI; no **CUI**, **CONFIDENTIAL**,
   plain **TOP SECRET**, or caveats (NOFORN, REL TO, etc.). Secret is maroon `#800000` instead of the
   standard red; there's no orange TOP SECRET. A modern version should ship the current standard
   palette (U green, CUI purple, C blue, S red, TS orange, TS/SCI yellow) and allow custom markings.
3. **Fragile install model.** Choosing a level = ticking one of three installer features, with only a
   warning that picking two breaks it. Better: one installer + a config file/GPO (ADMX) policy.
4. **Hard-coded paths.** `C:\Banner.log`, `C:\Banner.reg` at drive root (often not writable today).
5. **Enforces legacy UI.** `SetSysColors`, Marquee screensaver (`SSMarque.scr` was removed in modern
   Windows), Active Desktop. Modern equivalents: lock-screen/logon message via policy, accent color,
   a custom classification screensaver/lock overlay.
6. **Known bug:** screensaver policy sometimes doesn't activate.
7. **No high-DPI / per-monitor DPI awareness**, and mixed-DPI multi-monitor setups didn't exist then.
8. **Startup via `Run` key only** — easy for users to kill. Could run as per-session agent launched by a
   service/scheduled task with restart-on-exit, and be locked down by policy.
9. **Admin tooling** lives in a separate `BnrAdmin.exe`; settings live in a VB-specific COM API.

## Open questions for the rebuild

* Target stack (e.g. C#/.NET 8 WinForms/WPF, or native C++/Rust for a tiny footprint)?
* Which classification markings / caveats need to be supported?
* Managed by GPO/Intune (ADMX) or a local config file?
* Which legacy enforcement features (colors, screensaver, wallpaper, logon text) still matter?
