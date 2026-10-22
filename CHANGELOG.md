# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.6.0] - 2026-10-22

### Added
- **Show computer name** (`ShowComputerName`, off by default): this computer's name (hostname) on the banner.
- **Choose where each item goes.** The clock, user name and computer name each have a position (`ClockPosition`,
  `UserNamePosition`, `ComputerNamePosition`): the left or right of every banner, or one corner (top left, top right,
  bottom left, bottom right), for example the clock top left, the user name top right and the computer name bottom
  left. Items that share a place are shown together. A corner on a banner that isn't shown (bottom left with
  top-only banners) uses the same side of the one that is. Set it in Group Policy (new 1.6 templates), the settings
  window (a drop-down for each item) or `config --set`. The defaults are unchanged: clock on the right, user name on
  the left, on every banner.

## [1.5.2] - 2026-10-20

### Fixed
- The settings and setup window could open partly under a banner, depending on the monitor and display scaling. At
  high scaling on a shorter screen it was taller than the space between the banners, so its top or its buttons ended
  up underneath them. It now always opens inside the space the banners leave free, on the monitor it opens on (also
  after moving to a monitor with different scaling), and is drawn smaller when it would not otherwise fit.
- The banner's right-click menu and About box could disappear behind the banner a moment after opening. The banner's
  regular "stay on top" check raised it over them; it now leaves them in front while they are open.

## [1.5.1] - 2026-10-19

### Fixed
- A full-screen app (such as full-screen Remote Desktop) now keeps covering its monitor's banner when you click a
  window on another monitor. The banner used to come back over it, and sometimes took up to 2 seconds to step behind
  it again after you clicked back in. Which monitor has a full-screen app on top is now judged on each monitor by
  itself, not by which window is active, and it is re-checked the moment the active window changes. A window of
  another app on top of the full-screen one, such as a local window dragged over it, still brings the banner back.

## [1.5.0] - 2026-10-19

### Changed
- **Full-screen apps now cover the banner by default** (`StayAboveFullScreenApps` is off). With full-screen Remote
  Desktop, this computer's banner used to sit over the top of the remote screen (covering the connection bar and the
  remote system's own banner) and float above where the taskbar would be on a Linux desktop. Now, while a full-screen
  app is in front on a monitor, that monitor's banner steps behind it, and it comes back as soon as you switch away.
  The remote system should show its own banner. Turn it back on with Group Policy, the new checkbox in the settings
  window, or `config --set StayAboveFullScreenApps=1`. PCs upgraded from 1.4.0 or earlier keep the value they have
  saved (on), so set it there to change them.

## [1.4.0] - 2026-10-19

### Added
- **Show user name** (`ShowUserName`, off by default): the signed-in user's account name on the left of every
  banner. Set it in Group Policy (*Appearance > Show user name*, new in the 1.4 templates), the settings window or
  `config --set ShowUserName=1`. On a narrow screen the clock is hidden first, then the user name, before the marking
  is ever shortened.

### Fixed
- Banners no longer disappear and reappear once or twice after unlocking or waking the PC. They were rebuilt after
  every unlock, and again for each display change while the monitors came back. Now the banner waits until the displays
  are quiet, then checks once: a banner that moved is put back, and all of them are rebuilt only if the monitor layout
  really changed.

### Security
- `install`/`uninstall` start `cmd.exe` and `powershell.exe` by full path, and the program loads the system DLLs it
  calls from System32 only. Setup runs elevated from the download folder, where a planted `cmd.exe` or DLL could
  otherwise have run as administrator.
- A banner started by the service no longer steps aside when its single-instance mutex already exists, so a user
  can't keep the marking off the screen by creating that mutex first.
- `uninstall` writes back only the color, wallpaper and screen-saver values the banner saves, even if a user has
  edited the saved copy in their own profile.

## [1.3.1] - 2026-10-19

### Fixed
- An ended banner is logged as event 1001 (stopped unexpectedly, restarted) again; it could be logged as 1005
  (showing nothing) when the service checked it in the moment between its window thread and its process ending.

### Changed
- Banners are thinner by default: `BannerThickness` now defaults to 15 pixels at 100% scaling (was automatic, about
  24). Set `BannerThickness=0` for the previous automatic size.

## [1.3.0] - 2026-10-19

### Fixed
- The banner's user can no longer freeze or remove it without administrator rights (#17): its threads (and every
  object it creates) get the same locked-down permissions as its process, so suspending or ending them is refused; a
  hidden or minimized banner window is shown again within 2 seconds; and the service resumes a suspended banner and
  restarts one whose window thread has ended, logging event 1005.
- `uninstall` restores profiles that aren't signed in: enabling the privileges needed to load their `NTUSER.DAT`
  always failed because of a wrong structure layout (#24). The summary now says plainly when a profile could not be
  restored.
- After an upgrade from 1.1.0, `uninstall` no longer puts the banner's own colors back: colors, wallpaper or screen
  saver that are already the banner's are saved as the Windows defaults, and a saved copy that holds them (from 1.2.0)
  is not restored (#25).
- Settings window preview shows the standard marking when the banner text doesn't fit the level, as the banner does;
  saving banner text that fits only Group Policy's level now warns that it depends on the policy (#26).
- `uninstall` started by a deployment agent or over SSH removes its files even when the caller ends right away, and
  anything still in use is removed at the next restart (#26).
- Turning `AllowUserExit` off brings back banners that users had closed (#26).
- Help listed `--keep-user-settings` twice (#26).

### Changed
- Renamed to **ClassBanner**: `ClassBanner.exe`, `%ProgramFiles%\ClassBanner`, the *ClassBanner* service and event log
  source, Group Policy key `HKLM\SOFTWARE\Policies\ClassBanner` and templates `ClassBanner.admx`/`.adml`. Installing
  over Better Banner (any version) imports its settings, keeps the sign-in notice and users' saved colors, stops and
  removes its service and banners, and removes Better Banner. Better Banner Group Policy keeps applying until the
  ClassBanner templates are deployed.

### Added
- `BannerThickness`: set the banner height in pixels at 100% scaling (12-120), in the settings window, on the
  command line (`--set BannerThickness=40`) or by Group Policy. It scales with each monitor's display scaling; 0 (the
  default) keeps the automatic height. Text is made smaller to fit a thin banner.

## [1.2.0] - 2026-10-18

### Added
- `uninstall` gives every profile its own title-bar colors, wallpaper and screen saver back (saved before the banner
  first changed them), and switches off what 1.1.0 changed in profiles where nothing was saved;
  `--keep-user-settings` skips this (#19).

### Changed
- The banner can no longer be ended and stay gone (#17). A new *Better Banner* Windows service (LocalSystem,
  automatic) replaces the `Run` key: it starts the banner in every signed-in session, starts it again about 2 seconds
  after any unexpected exit, and protects the process so its user can't end it with *End task*. Each unexpected stop
  and restart is written to the Application event log (source "Better Banner") with the user and session. *Exit*
  (when `AllowUserExit` is on), sign-out, uninstall and upgrades don't restart it. `install` stops and replaces every
  banner, including 1.1.0's, and the service starts the new ones; `--no-start` now means "don't start the service yet".

### Fixed
- An open settings window follows Group Policy changes: fields it starts managing show the policy value and lock,
  and Save re-checks policy first (#19).
- An invalid `Level` in Group Policy shows CLASSIFICATION NOT SET instead of quietly using the local level (#19).
- Command-line mistakes show the error first, then one line pointing at `BetterBanner.exe help`, instead of the whole
  usage text with the error at the end (#19).
- Setup/settings window: messages like "Choose a classification first." clear when you change something; the status
  text has room for long messages; the intro no longer says "click Install" after installing (#19).
- The banner text can no longer contradict the classification (#18). It must be the level's marking or start with it
  followed by `//` or a space (`SECRET//NOFORN` for SECRET; `TOP SECRET//SCI...` only for TS/SCI). `install`, `config`
  (exit 2) and the settings window refuse anything else and name the expected prefix; a bad caption in `settings.json`
  or Group Policy is ignored, the standard marking is shown, and `show` and the log warn. Custom colors that differ
  from the level's standard colors are reported the same way.

## [1.1.0] - 2026-10-08

### Added
- Setup window: double-clicking the downloaded `BetterBanner.exe` opens a graphical installer (classification and
  options, then Install or Upgrade). No command prompt needed.
- Start menu entry "Better Banner" that opens the settings window.
- `BetterBanner.exe banner` shows the banner without installing.

## [1.0.0] - 2026-10-08

First stable release. Same code as 1.0.0-beta.3; see the beta entries below for everything included.

## [1.0.0-beta.3] - 2026-10-08

### Fixed
- `config`, the settings window and a running banner no longer turn "classification not set" into UNCLASSIFIED: the
  level is now simply absent until someone chooses one, `config` refuses to save without one, and the settings window
  shows NOT SET and requires a choice (#6, #7, #8).
- Upgrading from beta.1 imports its settings (if the old folder is owned by Administrators), keeps the sign-in notice,
  stops the beta.1 banners once and removes the old folder (#9).
- The sign-in notice is only changed when set or cleared explicitly, never because the settings file has no notice.
- The classification marking is never cut off: the clock moves aside, then hides, and a marking wider than the screen
  is shrunk instead of shortened with "..." (#10).
- Values starting with `--` (e.g. `--logon-text "-- Authorized use only --"`) are accepted; `--option=value` also
  works (#11).
- Out-of-range numbers (e.g. `FontSize=999`) are rejected instead of silently clamped; invalid clock formats are
  rejected, and the clock falls back to the default format instead of showing the format text (#12).
- The settings window stays responsive while saving (#12).
- `settings.json` and `show` no longer escape `&`, `<`, `>` and accented letters (#12).
- Help shown in a message box is laid out for it (#12).
- Release workflow: tag names are passed through the environment instead of being inserted into the script (#12).

### Changed
- Test dependencies updated (Microsoft.NET.Test.Sdk 18.10.1, xunit 2.9.3, xunit.runner.visualstudio 4.0.0).

## [1.0.0-beta.2] - 2026-10-08

### Fixed
- One invalid value in `settings.json` no longer discards the whole file and falls back to UNCLASSIFIED; each value is
  read on its own, with the same spellings as the command line (e.g. `"Level": "S"`).
- `config` no longer replaces an unreadable `settings.json` with defaults; it refuses and changes nothing. Values it
  drops are reported.
- Settings moved from `%ProgramData%\BetterBanner` (where a standard user could create the folder first and keep
  control of it) to `%ProgramFiles%\BetterBanner`. Beta testers: run `install` again with your options.
- `install` no longer stops other signed-in users' banners. The exe is replaced in place and every running banner
  restarts itself into the new version.
- `install`, `config` and `uninstall` ask for administrator rights (UAC) instead of failing, so *Uninstall* from
  *Apps & features* works.
- A long banner text no longer runs into the clock; it is shortened with "..." instead.
- `/settings` (or any argument starting with `/s`, `/p`, `/c`) is no longer mistaken for a screen-saver switch, and an
  option is never taken as the previous option's value (`--caption --quiet`).

### Changed
- When no valid classification level is configured, the banner shows a grey "CLASSIFICATION NOT SET - CONTACT YOUR
  ADMINISTRATOR" instead of UNCLASSIFIED. The first `install` requires `--level`.

## [1.0.0-beta.1] - 2026-10-07

### Added
- Classification banner on the top, bottom or both edges of every monitor, docked as a shell app bar so maximized
  windows stop at it.
- Standard markings and colors for UNCLASSIFIED, CUI, CONFIDENTIAL, SECRET, TOP SECRET and TOP SECRET//SCI, with
  optional custom text and colors.
- Per-monitor DPI support: correct size and crisp text at any resolution and display scaling.
- Banner reconciliation after unlock, resume, display and DPI changes and Explorer restarts, plus a periodic check.
  Fixes Banner 3.1's banners moving from the second monitor to the main one after unlock.
- Single self-installing native exe: `install`, `config`, `show`, `uninstall`; also acts as the screen saver.
- Settings file with Group Policy overrides (ADMX/ADML templates).
- Optional sign-in notice, clock (local/Zulu), title bar and highlight colors, generated or custom wallpaper, and
  classification screen saver.
- Control Panel item and settings window with live preview; Group Policy-managed settings shown locked; saving
  elevates with a UAC prompt. "Change" in Apps & features opens the same window.
- Logo and app icon.
- Window recovery (double-click a banner) for windows hidden under a banner or off-screen.
- CI on Windows with unit and end-to-end smoke tests; tag-driven GitHub Releases with checksums.
