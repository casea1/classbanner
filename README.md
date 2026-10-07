# Better Banner

A classification banner for Windows 10 and 11. It keeps the system's marking (UNCLASSIFIED, CUI, CONFIDENTIAL,
SECRET, TOP SECRET, TOP SECRET//SCI) visible at the top and/or bottom of **every monitor**, at any resolution or
display scaling. It replaces the USAF *Banner 3.1* (2003); see [docs/banner-3.1-analysis.md](docs/banner-3.1-analysis.md).

- **One small file.** `BetterBanner.exe` is a native program (a few MB) with no .NET or other prerequisites. The same
  file installs, configures, uninstalls, runs the banner and is the screen saver.
- **Standalone or domain.** Configure with the command line on workgroup PCs, or with Group Policy (ADMX templates
  included) on domains. Group Policy always wins.
- **Stays on the right monitor.** Banners are rebuilt after unlock, resume, monitor changes, DPI changes and Explorer
  restarts, and are checked every 2 seconds. This fixes Banner 3.1's "second monitor's banners jump to the main
  screen after unlock" problem.
- **Standard markings and colors**, with optional custom text such as `SECRET//NOFORN`.
- **Sign-in notice.** Optionally shows a message users must acknowledge before they can sign in.
- **Optional extras:** clock (local or Zulu), classification-colored title bars and highlights, wallpaper, and a
  classification screen saver.

| Level | Banner | Text |
|---|---|---|
| UNCLASSIFIED | `#007A33` green | white |
| CUI | `#502B85` purple | white |
| CONFIDENTIAL | `#0033A0` blue | white |
| SECRET | `#C8102E` red | white |
| TOP SECRET | `#FF8C00` orange | black |
| TOP SECRET//SCI | `#FCE83A` yellow | black |

## Install

Download `BetterBanner.exe` from the [latest release](../../releases/latest) and check it against `SHA256SUMS.txt`:

```powershell
Get-FileHash .\BetterBanner.exe -Algorithm SHA256
```

### Standalone / workgroup PC

From an **elevated** Command Prompt or PowerShell:

```bat
start /wait BetterBanner.exe install --level SECRET
```

This copies the program to `C:\Program Files\BetterBanner`, saves settings to
`C:\ProgramData\BetterBanner\settings.json` (only administrators can change it), adds it to *Apps & features*, starts
it for every user at sign-in, and starts it now for you.

More examples:

```bat
:: Custom marking, top banner only, Zulu clock
start /wait BetterBanner.exe install --level S --caption "SECRET//NOFORN" --position top --clock on --zulu

:: Sign-in notice users must click OK on before logging on
start /wait BetterBanner.exe config --logon-title "SECRET SYSTEM" --logon-text "This system is approved for SECRET. Authorized use only."

:: Any setting from the table below
start /wait BetterBanner.exe config --set FontSize=12 --set WallpaperMode=Generated

:: What is in effect, and where it came from
BetterBanner.exe show

:: Remove (add --keep-settings to keep settings.json)
start /wait BetterBanner.exe uninstall
```

> `start /wait` makes the prompt wait for the result; `BetterBanner.exe` is a windowed program so the banner never
> flashes a console window. Exit codes: `0` OK, `1` error, `2` bad arguments, `5` not run as administrator.

### Domain

1. Deploy `BetterBanner.exe install --quiet --no-start` (plus any options) as a computer startup script, or with
   SCCM/Intune/PDQ.
2. Copy `BetterBanner.admx` and `en-US\BetterBanner.adml` (from `BetterBanner-<version>-GroupPolicy.zip`) to your
   Central Store (`\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions`).
3. Set the policies under **Computer Configuration > Administrative Templates > Better Banner**.

Banners pick up Group Policy changes after `gpupdate` (immediately) or within 30 seconds.

For the sign-in notice on domains, use the built-in policies **Computer Configuration > Windows Settings > Security
Settings > Local Policies > Security Options > Interactive logon: Message title / Message text for users attempting to
log on**.

### Unsigned program

Releases are not code-signed yet. Windows SmartScreen may warn the first time (choose *More info > Run anyway*), and
allow-listing tools (AppLocker, WDAC) need a rule for it. A hash rule using the published SHA-256, or a path rule for
`%ProgramFiles%\BetterBanner\*`, both work.

## Settings

Settings come from built-in defaults, then `settings.json`, then Group Policy
(`HKLM\SOFTWARE\Policies\BetterBanner`). Invalid values are ignored and logged, so a bad setting never breaks the banner.

| Setting | Default | Description |
|---|---|---|
| `Level` | `Unclassified` | `Unclassified`, `Cui`, `Confidential`, `Secret`, `TopSecret`, `TopSecretSci` (also accepts `U`, `C`, `S`, `TS`, `TS/SCI`) |
| `Caption` | *(standard)* | Custom banner text, e.g. `SECRET//NOFORN` |
| `BackColor`, `ForeColor` | *(standard)* | `#RRGGBB` overrides |
| `Position` | `Both` | `Top`, `Bottom`, `Both` |
| `FontSize` | `10` | Points, 8-36. Scaled per monitor for display scaling |
| `HoverTransparency` | `40` | Percent see-through while the mouse is over the banner (0-90) |
| `StayAboveFullScreenApps` | `true` | Keep the banner over full-screen presentations and video |
| `AllowUserExit` | `false` | Adds *Exit* to the banner's menu |
| `ClockEnabled` | `false` | Show a clock on the right |
| `ClockFormat` | `ddd dd-MMM-yy HH:mm%TZI%` | [.NET date format](https://learn.microsoft.com/dotnet/standard/base-types/custom-date-and-time-format-strings); `%TZI%` becomes `Z` or `L` |
| `ClockUseZulu` | `false` | Show UTC |
| `ClockUppercase` | `false` | e.g. `071830Z OCT 26` with format `ddHHmm%TZI% MMM yy` |
| `EnforceColors` | `true` | Classification-colored title bars, accent and selection highlight |
| `WallpaperMode` | `Off` | `Off`, `Generated` (solid color with the marking), `Image` |
| `WallpaperImagePath` | | Image for `WallpaperMode=Image` |
| `EnforceScreenSaver` | `false` | Use the classification screen saver |
| `ScreenSaverTimeoutSeconds` | `600` | 60-7200 |
| `ScreenSaverRequirePassword` | `true` | Lock when the screen saver ends |
| `LogonNoticeTitle`, `LogonNoticeText` | | Sign-in notice (applied by `install`/`config`) |

## Using the banner

- **Double-click** a banner to bring back windows whose title bar is hidden under it or off-screen.
- **Right-click** for *Recover hidden windows* and *About* (which shows the marking and where settings came from).
- Hover over a banner to see through it.
- Log: `%LOCALAPPDATA%\BetterBanner\BetterBanner.log`.

## Development

Requirements: .NET 8 SDK. The core logic (settings, layout, reconciliation, clock, command line) is platform
independent and unit tested; the app is plain Win32 and compiles to a native exe with Native AOT.

```sh
dotnet build                 # builds everywhere (Linux/macOS only compile-check the Windows app)
dotnet test                  # unit tests, including the Group Policy template checks
dotnet publish src/BetterBanner -c Release -o publish   # native exe; Windows only
```

```
src/BetterBanner.Core   settings, markings, monitor layout and reconciliation, clock, command line (tested)
src/BetterBanner        Win32 app: banner windows, app-bar docking, policies, screen saver, installer
tests/                  xUnit tests
policy/                 Group Policy ADMX/ADML templates
scripts/smoke-test.ps1  end-to-end install/run/uninstall test used by CI
```

### CI and releases

- **CI** (`.github/workflows/ci.yml`) runs on every push to `main` and every pull request, on Windows: build, unit
  tests, publish the native exe, then an end-to-end smoke test. The test installs the exe, checks files, registry and
  settings, runs the banner, checks only one copy can run, then uninstalls and checks cleanup. The exe is uploaded as
  a build artifact.
- **Releases**: update `CHANGELOG.md`, then tag and push:

  ```sh
  git tag v1.0.0 && git push origin v1.0.0
  ```

  `.github/workflows/release.yml` builds and tests that tag, then publishes a GitHub Release with
  `BetterBanner.exe`, the Group Policy templates zip and `SHA256SUMS.txt`. Tags with a suffix (e.g. `v1.1.0-rc.1`)
  become pre-releases.
- Dependabot keeps NuGet packages and GitHub Actions up to date.
