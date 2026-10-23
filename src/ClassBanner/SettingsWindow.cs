using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using ClassBanner.Core;
using static ClassBanner.Native;

namespace ClassBanner;

/// <summary>
/// The settings window (Control Panel > ClassBanner, banner menu > Settings, or "ClassBanner.exe settings").
/// Shows the effective settings; anything set by Group Policy is shown but locked. Saving runs
/// "config" elevated, so the window itself never needs administrator rights.
/// </summary>
internal sealed class SettingsWindow : Win32Window
{
    private const string ClassName = "ClassBanner.Settings";
    private const uint Style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    private const uint ExStyle = WS_EX_CONTROLPARENT;
    private const int IdOk = 1;     // IDOK: Enter
    private const int IdCancel = 2; // IDCANCEL: Esc
    private const int IdCopyCommand = 100;

    private static readonly (ClassificationLevel Level, string Name)[] Levels =
        ClassificationPresets.All.Select(p => (p.Level, p.Caption)).ToArray();

    private static readonly (BannerPosition Position, string Name)[] Positions =
        [(BannerPosition.Both, "Top and bottom"), (BannerPosition.Top, "Top only"), (BannerPosition.Bottom, "Bottom only")];

    /// <summary>Choices for where the clock, user name and computer name go; null is off.</summary>
    private static readonly (InfoPosition? Position, string Name)[] Corners =
    [
        (null, "Off"),
        (InfoPosition.Left, "Left, every banner"),
        (InfoPosition.Right, "Right, every banner"),
        (InfoPosition.TopLeft, "Top left"),
        (InfoPosition.TopRight, "Top right"),
        (InfoPosition.BottomLeft, "Bottom left"),
        (InfoPosition.BottomRight, "Bottom right"),
    ];

    private const uint WM_SAVE_DONE = WM_APP + 20;
    private const int SaveCancelled = -1223;
    private const int SaveFailedToStart = int.MinValue;

    private BannerSettings _initial;
    private volatile string? _saveError;
    private readonly HashSet<string> _policyKeys;
    private readonly List<(nint Control, string Key)> _locks = [];
    private string _policySnapshot;
    private bool _statusIsProblem;
    private const nuint TimerPolicy = 1;
    private const uint PolicyCheckMs = 5000;
    private readonly List<Row> _rows = [];
    private nint _font;
    private nint _previewBrush;
    private int _dpi = BannerLayout.BaseDpi;
    private RECT _previewRect;
    private double _scale = 1;

    private nint _thickness;
    private nint _level, _caption, _position, _fontSize, _clock, _zulu, _userName, _computerName, _aboveFullScreen, _colors, _wallpaper, _screenSaver, _noticeTitle, _noticeText;
    private nint _status, _save, _cancel, _copyCommand, _policyNote;

    /// <summary>A label + control (or a full-width control) laid out top to bottom.</summary>
    private sealed record Row(nint Label, nint Control, int Lines = 1, nint Extra = 0);

    /// <summary>Setup mode: shown when the downloaded exe is double-clicked; "Install" runs the elevated install.</summary>
    private bool _setup;

    private SettingsWindow(bool setup)
    {
        _setup = setup;
        var loaded = RegistryPolicySource.LoadEffective();
        _initial = loaded.Settings;
        var policy = new RegistryPolicySource().ReadValues();
        _policyKeys = policy.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _policySnapshot = Snapshot(policy);

        CreateWindow(ClassName, 0, ExStyle, Style, 0, 0, 100, 100, GetSysColorBrush(COLOR_BTNFACE));
        SetWindowTextW(Handle, setup ? "ClassBanner Setup" : "ClassBanner Settings");
        _dpi = (int)GetDpiForWindow(Handle);

        CreateControls();
        LoadValues(_initial);
        Layout();
        // Group Policy can change while the window is open (issue #19); also checked on a policy broadcast and on Save.
        SetTimer(Handle, TimerPolicy, PolicyCheckMs, 0);

        // Center on the monitor under the mouse, then make sure all of it is inside that monitor's work area.
        GetCursorPos(out var cursor);
        var monitor = Monitors.GetAll().FirstOrDefault(m => m.Bounds.Contains(cursor.X, cursor.Y)) ?? Monitors.GetAll().FirstOrDefault();
        GetWindowRect(Handle, out var r);
        if (monitor is not null)
        {
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            SetWindowPos(Handle, 0, monitor.WorkArea.Left + (monitor.WorkArea.Width - w) / 2,
                monitor.WorkArea.Top + (monitor.WorkArea.Height - h) / 2, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
        }
        FitToMonitor();
        ShowWindow(Handle, SW_SHOWNORMAL);
        SetForegroundWindow(Handle);
    }

    public static int Run(bool setup = false)
    {
        var window = new SettingsWindow(setup);
        return RunMessageLoop(window.Handle);
    }

    private void CreateControls()
    {
        _policyNote = Static(_setup
            ? (Installer.IsInstalled ? "ClassBanner is installed. Click Upgrade to update it to this version."
                                     : "Choose the settings, then click Install. Applies to every user of this computer.")
            : SettingsNote());

        _level = AddRow("Classification:", Combo(Levels.Select(l => l.Name)), nameof(BannerSettings.Level));
        _caption = AddRow("Banner text:", Edit(), nameof(BannerSettings.Caption));
        SendMessageString(_caption, EM_SETCUEBANNER, 1, "Standard marking (e.g. add //NOFORN)");
        _position = AddRow("Position:", Combo(Positions.Select(p => p.Name)), nameof(BannerSettings.Position));
        _fontSize = AddRow("Text size (8-36):", Edit(ES_NUMBER), nameof(BannerSettings.FontSize));
        _thickness = AddRow("Thickness (0 = auto):", Edit(ES_NUMBER), nameof(BannerSettings.BannerThickness));

        _clock = Combo(Corners.Select(c => c.Name));
        _zulu = Check("Zulu (UTC) time");
        _rows.Add(new Row(Static("Clock:"), _clock, Extra: _zulu));
        Lock(_clock, nameof(BannerSettings.ClockEnabled));
        Lock(_clock, nameof(BannerSettings.ClockPosition));
        Lock(_zulu, nameof(BannerSettings.ClockUseZulu));
        _userName = AddRow("User name:", Combo(Corners.Select(c => c.Name)), nameof(BannerSettings.ShowUserName));
        Lock(_userName, nameof(BannerSettings.UserNamePosition));
        _computerName = AddRow("Computer name:", Combo(Corners.Select(c => c.Name)), nameof(BannerSettings.ShowComputerName));
        Lock(_computerName, nameof(BannerSettings.ComputerNamePosition));
        _aboveFullScreen = AddRow(null, Check("Stay above full-screen apps (incl. Remote Desktop)"), nameof(BannerSettings.StayAboveFullScreenApps));

        _colors = AddRow(null, Check("Color title bars and highlights to match"), nameof(BannerSettings.EnforceColors));
        _wallpaper = AddRow(null, Check("Classification wallpaper"), nameof(BannerSettings.WallpaperMode));
        _screenSaver = AddRow(null, Check("Classification screen saver"), nameof(BannerSettings.EnforceScreenSaver));

        _noticeTitle = AddRow("Sign-in notice title:", Edit(), nameof(BannerSettings.LogonNoticeTitle));
        _noticeText = Edit(ES_MULTILINE | ES_WANTRETURN | WS_VSCROLL);
        _rows.Add(new Row(Static("Sign-in notice text:"), _noticeText, Lines: 4));
        Lock(_noticeText, nameof(BannerSettings.LogonNoticeText));

        _status = Static("");
        _save = Button(_setup ? (Installer.IsInstalled ? "Upgrade" : "Install") : "Save", IdOk, BS_DEFPUSHBUTTON);
        _cancel = Button("Close", IdCancel, BS_PUSHBUTTON);
        _copyCommand = Button("Copy install command", IdCopyCommand, BS_PUSHBUTTON);
    }

    private string SettingsNote() => _policyKeys.Count > 0
        ? "Some settings are managed by Group Policy and can't be changed here."
        : "Changes apply to every user of this computer.";

    private static string Snapshot(IReadOnlyDictionary<string, string> values) =>
        string.Join('\n', values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v => $"{v.Key}={v.Value}"));

    /// <summary>
    /// Re-reads Group Policy. When it changed, fields it now manages show its values and are locked; fields it no
    /// longer manages are unlocked; what the user typed in fields that stay editable is kept. Returns true if it changed.
    /// </summary>
    private bool RefreshPolicy()
    {
        var policy = new RegistryPolicySource().ReadValues();
        var snapshot = Snapshot(policy);
        if (snapshot == _policySnapshot) return false;

        var edits = ChangedValues(all: true);
        _policySnapshot = snapshot;
        _policyKeys.Clear();
        _policyKeys.UnionWith(policy.Keys);
        _initial = RegistryPolicySource.LoadEffective().Settings;

        var shown = _initial.Clone();
        SettingKeys.ApplyAll(shown, edits.Where(e => !_policyKeys.Contains(e.Key)));
        LoadValues(shown);
        foreach (var group in _locks.GroupBy(l => l.Control))
            EnableWindow(group.Key, !group.Any(l => _policyKeys.Contains(l.Key)));
        if (!_setup) SetWindowTextW(_policyNote, SettingsNote());
        InvalidatePreview();
        Log.Info("Group Policy changed while the settings window was open; refreshed it.");
        return true;
    }

    private void ShowProblem(string message)
    {
        SetWindowTextW(_status, message);
        _statusIsProblem = true;
    }

    private void LoadValues(BannerSettings s)
    {
        // Not configured: no selection (and a grey NOT SET preview) rather than a preselected UNCLASSIFIED.
        SendMessage(_level, CB_SETCURSEL, s.Level is { } level ? Array.FindIndex(Levels, l => l.Level == level) : -1, 0);
        SetWindowTextW(_caption, s.Caption ?? "");
        SendMessage(_position, CB_SETCURSEL, Array.FindIndex(Positions, p => p.Position == s.Position), 0);
        SetWindowTextW(_fontSize, s.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetWindowTextW(_thickness, s.BannerThickness.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetCorner(_clock, s.ClockEnabled, s.ClockPosition);
        SetCheck(_zulu, s.ClockUseZulu);
        SetCorner(_userName, s.ShowUserName, s.UserNamePosition);
        SetCorner(_computerName, s.ShowComputerName, s.ComputerNamePosition);
        SetCheck(_aboveFullScreen, s.StayAboveFullScreenApps);
        SetCheck(_colors, s.EnforceColors);
        SetCheck(_wallpaper, s.WallpaperMode != WallpaperMode.Off);
        SetCheck(_screenSaver, s.EnforceScreenSaver);
        SetWindowTextW(_noticeTitle, s.LogonNoticeTitle ?? "");
        SetWindowTextW(_noticeText, (s.LogonNoticeText ?? "").ReplaceLineEndings("\r\n"));
    }

    /// <summary>
    /// Lays the window out for its monitor and moves it fully inside that monitor's work area. The banners are app
    /// bars outside the work area, so a window inside it is never under a banner. At high display scaling on a short
    /// screen the window is laid out smaller rather than running under the top banner and the bottom one.
    /// </summary>
    private void FitToMonitor()
    {
        _scale = 1;
        Layout();
        if (CurrentMonitor() is not { } monitor) return;

        // The title bar doesn't shrink with the contents, so a second pass may be needed to fit exactly.
        GetWindowRect(Handle, out var r);
        for (var pass = 0; pass < 3; pass++)
        {
            var scale = WindowFit.ScaleToFit(r.Bottom - r.Top, monitor.WorkArea.Height);
            if (scale >= 1 || _scale <= WindowFit.MinScale) break;
            _scale = Math.Max(WindowFit.MinScale, _scale * scale);
            Layout();
            GetWindowRect(Handle, out r);
        }

        var placed = WindowFit.MoveInside(r.ToScreenRect(), monitor.WorkArea);
        if (placed.Left != r.Left || placed.Top != r.Top)
            SetWindowPos(Handle, 0, placed.Left, placed.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private MonitorInfo? CurrentMonitor()
    {
        GetWindowRect(Handle, out var r);
        int x = (r.Left + r.Right) / 2, y = (r.Top + r.Bottom) / 2;
        var monitors = Monitors.GetAll();
        return monitors.FirstOrDefault(m => m.Bounds.Contains(x, y)) ?? monitors.FirstOrDefault(m => m.IsPrimary);
    }

    /// <summary>Positions every control for the current DPI and sizes the window to fit.</summary>
    private void Layout()
    {
        int S(int px) => (int)Math.Round(px * _dpi * _scale / BannerLayout.BaseDpi);

        if (_font != 0) DeleteObject(_font);
        _font = CreateFontW(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");

        int margin = S(16), labelWidth = S(140), controlWidth = S(300), rowHeight = S(24), gap = S(8);
        var y = margin;

        _previewRect = new RECT(margin, y, margin + labelWidth + gap + controlWidth, y + S(30));
        y = _previewRect.Bottom + gap;

        Place(_policyNote, margin, y, labelWidth + gap + controlWidth, rowHeight);
        y += rowHeight + gap;

        foreach (var row in _rows)
        {
            var height = row.Lines == 1 ? rowHeight : S(18) * row.Lines + S(6);
            if (row.Label != 0) Place(row.Label, margin, y + S(3), labelWidth, rowHeight);
            var x = margin + labelWidth + gap;
            var width = row.Extra != 0 ? controlWidth / 2 : controlWidth;
            // Combo boxes need a tall rect for their drop-down list.
            var isCombo = _combos.Contains(row.Control);
            Place(row.Control, x, y, width, isCombo ? S(200) : height);
            if (row.Extra != 0) Place(row.Extra, x + width, y, controlWidth - width, height);
            y += height + gap;
        }

        // The status gets its own full-width row, three lines tall, so a long message is never cut off (issue #19).
        var right = margin + labelWidth + gap + controlWidth;
        Place(_status, margin, y, right - margin, S(54));
        y += S(54) + gap;

        int buttonWidth = S(88), buttonHeight = S(28);
        Place(_cancel, right - buttonWidth, y, buttonWidth, buttonHeight);
        Place(_save, right - 2 * buttonWidth - gap, y, buttonWidth, buttonHeight);
        Place(_copyCommand, margin, y, S(150), buttonHeight);
        y += buttonHeight + margin;

        var client = new RECT(0, 0, right + margin, y);
        AdjustWindowRectExForDpi(ref client, Style, false, ExStyle, (uint)_dpi);
        SetWindowPos(Handle, 0, 0, 0, client.Right - client.Left, client.Bottom - client.Top, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);

        foreach (var hwnd in AllControls()) SendMessage(hwnd, WM_SETFONT, _font, 1);
        InvalidateRect(Handle, 0, true);
    }

    protected override nint WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_COMMAND:
                var id = LowWord(wParam);
                var code = HighWord(wParam);
                if (id == IdOk && code == BN_CLICKED) Save();
                else if (id == IdCopyCommand && code == BN_CLICKED) CopyInstallCommand();
                else if (id == IdCancel && code == BN_CLICKED) Destroy();
                else if (code is CBN_SELCHANGE or EN_CHANGE)
                {
                    InvalidatePreview();
                    // A "choose a classification" or "banner text must start with..." message is stale once the user
                    // changes something (issue #19).
                    if (_statusIsProblem)
                    {
                        SetWindowTextW(_status, "");
                        _statusIsProblem = false;
                    }
                }
                return 0;

            case WM_TIMER:
                if ((nuint)wParam == TimerPolicy && RefreshPolicy())
                    SetWindowTextW(_status, "Group Policy changed; the settings it manages were updated.");
                return 0;

            case WM_SETTINGCHANGE:
                if (lParam != 0 && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) is "Policy" or "ClassBanner" && RefreshPolicy())
                    SetWindowTextW(_status, "Group Policy changed; the settings it manages were updated.");
                return 0;

            case WM_PAINT:
                PaintPreview();
                return 0;

            case WM_DPICHANGED:
                _dpi = HighWord(wParam);
                unsafe
                {
                    var suggested = (RECT*)lParam;
                    SetWindowPos(Handle, 0, suggested->Left, suggested->Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
                FitToMonitor();
                return 0;

            case WM_SAVE_DONE:
                OnSaveDone((int)wParam);
                return 0;

            case WM_CLOSE:
                Destroy();
                return 0;

            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    protected override void OnDestroyed()
    {
        if (_font != 0) DeleteObject(_font);
        if (_previewBrush != 0) DeleteObject(_previewBrush);
    }

    /// <summary>A live banner preview at the top of the window.</summary>
    private void PaintPreview()
    {
        var hdc = BeginPaint(Handle, out var ps);
        try
        {
            var preview = ReadSettings();
            if (_previewBrush != 0) DeleteObject(_previewBrush);
            _previewBrush = CreateSolidBrush(ToColorRef(preview.EffectiveBackColor));
            FillRect(hdc, _previewRect, _previewBrush);

            var font = CreateFontW(-(int)Math.Round(preview.FontSize * _dpi / 72.0), 0, 0, 0, FW_BOLD, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
            var old = SelectObject(hdc, font);
            SetBkMode(hdc, TRANSPARENT);
            SetTextColor(hdc, ToColorRef(preview.EffectiveForeColor));
            var rect = _previewRect;
            // Show what the banner will show: banner text that doesn't fit the level is ignored by it (issue #26).
            var caption = preview.Level is { } level && ClassificationPresets.CaptionProblem(level, preview.Caption) is not null
                ? preview.Preset.Caption : preview.EffectiveCaption;
            DrawTextW(hdc, caption, -1, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
            SelectObject(hdc, old);
            DeleteObject(font);
        }
        finally
        {
            EndPaint(Handle, ps);
        }
    }

    /// <summary>
    /// Copies an install command with the settings as shown in the window (saved or not), for setting up other
    /// computers the same way.
    /// </summary>
    private void CopyInstallCommand()
    {
        if (ExportCommand.Build(ReadSettings()) is not { } exported)
        {
            ShowProblem("Choose a classification first.");
            return;
        }
        if (!Clipboard.SetText(exported.Command, Handle))
        {
            ShowProblem("Could not copy to the clipboard. Run 'ClassBanner.exe export' instead.");
            return;
        }
        _statusIsProblem = false;
        SetWindowTextW(_status, string.Join(' ', ["Install command copied to the clipboard. Run it as administrator on other computers.", .. exported.Notes]));
        InvalidateRect(_status, 0, true);
    }

    private void InvalidatePreview()
    {
        unsafe
        {
            fixed (RECT* r = &_previewRect) InvalidateRectPtr(Handle, r);
        }
    }

    private static unsafe void InvalidateRectPtr(nint hwnd, RECT* rect) => InvalidateRect(hwnd, (nint)rect, false);

    /// <summary>The settings as currently shown in the window.</summary>
    private BannerSettings ReadSettings()
    {
        var s = _initial.Clone();
        SettingKeys.ApplyAll(s, ChangedValues(all: true));
        return s;
    }

    /// <summary>Values from the controls, skipping anything locked by Group Policy.</summary>
    private List<KeyValuePair<string, string>> ChangedValues(bool all = false)
    {
        var values = new List<KeyValuePair<string, string>>();
        void Add(string key, string value)
        {
            if (all || !_policyKeys.Contains(key)) values.Add(new(key, value));
        }

        var level = (int)SendMessage(_level, CB_GETCURSEL, 0, 0);
        if (level >= 0) Add(nameof(BannerSettings.Level), Levels[level].Level.ToString());
        Add(nameof(BannerSettings.Caption), GetWindowText(_caption).Trim());
        var position = (int)SendMessage(_position, CB_GETCURSEL, 0, 0);
        if (position >= 0) Add(nameof(BannerSettings.Position), Positions[position].Position.ToString());
        var fontSize = GetWindowText(_fontSize).Trim();
        if (int.TryParse(fontSize, out var size) && size is >= 8 and <= 36) Add(nameof(BannerSettings.FontSize), fontSize);
        var thickness = GetWindowText(_thickness).Trim();
        if (IsValidThickness(thickness)) Add(nameof(BannerSettings.BannerThickness), thickness);
        void AddCorner(nint combo, string enabledKey, string positionKey)
        {
            var index = (int)SendMessage(combo, CB_GETCURSEL, 0, 0);
            if (index < 0) return;
            Add(enabledKey, Corners[index].Position is null ? "0" : "1");
            if (Corners[index].Position is { } corner) Add(positionKey, corner.ToString());
        }

        AddCorner(_clock, nameof(BannerSettings.ClockEnabled), nameof(BannerSettings.ClockPosition));
        Add(nameof(BannerSettings.ClockUseZulu), IsChecked(_zulu) ? "1" : "0");
        AddCorner(_userName, nameof(BannerSettings.ShowUserName), nameof(BannerSettings.UserNamePosition));
        AddCorner(_computerName, nameof(BannerSettings.ShowComputerName), nameof(BannerSettings.ComputerNamePosition));
        Add(nameof(BannerSettings.StayAboveFullScreenApps), IsChecked(_aboveFullScreen) ? "1" : "0");
        Add(nameof(BannerSettings.EnforceColors), IsChecked(_colors) ? "1" : "0");

        // Keep an administrator-chosen image; the checkbox only switches the generated wallpaper on and off.
        var wallpaperOn = IsChecked(_wallpaper);
        if (wallpaperOn != (_initial.WallpaperMode != WallpaperMode.Off))
            Add(nameof(BannerSettings.WallpaperMode), wallpaperOn ? nameof(WallpaperMode.Generated) : nameof(WallpaperMode.Off));

        Add(nameof(BannerSettings.EnforceScreenSaver), IsChecked(_screenSaver) ? "1" : "0");
        Add(nameof(BannerSettings.LogonNoticeTitle), GetWindowText(_noticeTitle).Trim());
        Add(nameof(BannerSettings.LogonNoticeText), GetWindowText(_noticeText).Trim());
        return values;
    }

    private void Save()
    {
        if (RefreshPolicy())
        {
            ShowProblem("Group Policy changed while this window was open. Check the settings, then click Save again.");
            return;
        }

        var fontSize = GetWindowText(_fontSize).Trim();
        if (!int.TryParse(fontSize, out var size) || size is < 8 or > 36)
        {
            ShowProblem("Text size must be between 8 and 36.");
            return;
        }
        if (!IsValidThickness(GetWindowText(_thickness).Trim()))
        {
            ShowProblem($"Thickness must be 0 (automatic) or between {BannerLayout.MinThickness} and {BannerLayout.MaxThickness} pixels.");
            return;
        }
        if ((int)SendMessage(_level, CB_GETCURSEL, 0, 0) < 0 && !_policyKeys.Contains(nameof(BannerSettings.Level)))
        {
            ShowProblem("Choose a classification first.");
            return;
        }

        // Banner text must keep the chosen level's marking first (issue #18); say so here instead of after the UAC prompt.
        var levelIndex = (int)SendMessage(_level, CB_GETCURSEL, 0, 0);
        if (levelIndex >= 0 && !_policyKeys.Contains(nameof(BannerSettings.Caption)) &&
            ClassificationPresets.CaptionProblem(Levels[levelIndex].Level, GetWindowText(_caption)) is { } problem)
        {
            ShowProblem(problem);
            return;
        }

        var args = new List<string> { _setup ? "install" : "config", "--quiet" };
        foreach (var (key, value) in ChangedValues())
        {
            args.Add("--set");
            args.Add($"{key}={value}");
        }

        // The UAC prompt and the elevated "config" can take a while; wait on a worker thread so this window keeps
        // painting, then report back with a posted message.
        EnableWindow(_save, false);
        SetWindowTextW(_status, "Saving...");
        _statusIsProblem = false;
        _saveError = null;
        var hwnd = Handle;
        var commandLine = CommandLine.Join(args);
        _ = Task.Run(() =>
        {
            int code;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, commandLine)
                {
                    UseShellExecute = true,
                    Verb = isAdmin ? "open" : "runas", // UAC prompt for standard users / non-elevated admins
                })!;
                process.WaitForExit();
                code = process.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                code = SaveCancelled;
            }
            catch (Exception ex)
            {
                Log.Error("Saving settings failed", ex);
                _saveError = ex.Message;
                code = SaveFailedToStart;
            }
            PostMessageW(hwnd, WM_SAVE_DONE, code, 0);
        });
    }

    private void OnSaveDone(int code)
    {
        EnableWindow(_save, true);
        var message = code switch
        {
            0 when _setup => "Installed. It's in the Start menu and Control Panel and starts at every sign-in.",
            0 => "Saved. Banners update within a few seconds.",
            SaveCancelled when _setup => "Not installed: administrator approval was cancelled.",
            SaveCancelled => "Not saved: administrator approval was cancelled.",
            SaveFailedToStart => "Not saved: " + _saveError,
            ExitCodes.BadArguments => "Not saved: a value was rejected. Check the classification and text size.",
            _ => $"Not saved (error {code}). See {Log.FilePath}.",
        };
        if (code == 0 && Installer.CaptionDependsOnPolicy() is { } note) message += " " + note;
        SetWindowTextW(_status, message);
        if (code != 0) return;
        _initial = RegistryPolicySource.LoadEffective().Settings;
        if (_setup)
        {
            // From now on this window edits the installed settings.
            _setup = false;
            SetWindowTextW(Handle, "ClassBanner Settings");
            SetWindowTextW(_save, "Save");
            SetWindowTextW(_policyNote, SettingsNote());
        }
    }

    // ---- control helpers ----

    private nint AddRow(string? label, nint control, string key)
    {
        _rows.Add(new Row(label is null ? 0 : Static(label), control));
        Lock(control, key);
        return control;
    }

    private static bool IsValidThickness(string text) =>
        int.TryParse(text, out var value) && (value == 0 || value is >= BannerLayout.MinThickness and <= BannerLayout.MaxThickness);

    private void Lock(nint control, string key)
    {
        _locks.Add((control, key));
        if (_policyKeys.Contains(key)) EnableWindow(control, false);
    }

    private IEnumerable<nint> AllControls()
    {
        yield return _policyNote;
        foreach (var row in _rows)
        {
            if (row.Label != 0) yield return row.Label;
            yield return row.Control;
            if (row.Extra != 0) yield return row.Extra;
        }
        yield return _status;
        yield return _save;
        yield return _cancel;
        yield return _copyCommand;
    }

    private nint Child(string cls, string? text, uint style, uint exStyle = 0, int id = 0) =>
        CreateChildWindow(exStyle, cls, text, WS_CHILD | WS_VISIBLE | style, 0, 0, 10, 10, Handle, id, GetModuleHandleW(null), 0);

    private nint Static(string text) => Child("STATIC", text, SS_LEFT);

    private nint Edit(uint extra = 0) => Child("EDIT", "", WS_TABSTOP | ES_AUTOHSCROLL | extra, WS_EX_CLIENTEDGE);

    private nint Check(string text) => Child("BUTTON", text, WS_TABSTOP | BS_AUTOCHECKBOX);

    private nint Button(string text, int id, uint style) => Child("BUTTON", text, WS_TABSTOP | style, id: id);

    private nint Combo(IEnumerable<string> items)
    {
        var combo = Child("COMBOBOX", null, WS_TABSTOP | CBS_DROPDOWNLIST | WS_VSCROLL);
        _combos.Add(combo);
        foreach (var item in items) SendMessageString(combo, CB_ADDSTRING, 0, item);
        return combo;
    }

    private static void Place(nint hwnd, int x, int y, int width, int height) =>
        SetWindowPos(hwnd, 0, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    private readonly HashSet<nint> _combos = [];

    /// <summary>Selects the position of an item in its combo box, or "Off".</summary>
    private static void SetCorner(nint combo, bool on, InfoPosition position) =>
        SendMessage(combo, CB_SETCURSEL, on ? Array.FindIndex(Corners, c => c.Position == position) : 0, 0);

    private static void SetCheck(nint hwnd, bool value) => SendMessage(hwnd, BM_SETCHECK, value ? 1 : 0, 0);

    private static bool IsChecked(nint hwnd) => SendMessage(hwnd, BM_GETCHECK, 0, 0) == 1;
}
