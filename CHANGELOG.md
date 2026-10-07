# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
- Window recovery (double-click a banner) for windows hidden under a banner or off-screen.
- CI on Windows with unit and end-to-end smoke tests; tag-driven GitHub Releases with checksums.
