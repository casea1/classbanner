# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
