using System.Text;
using BetterBanner.Core;
using static BetterBanner.Native;

namespace BetterBanner;

internal static class Program
{
    private const string SingleInstanceMutex = @"Local\BetterBanner.Banner";

    private static int Main(string[] args)
    {
        var cmd = CommandLine.Parse(args);
        try
        {
            return cmd.Kind switch
            {
                CommandKind.Run => RunBanner(),
                CommandKind.ScreenSaverShow => ScreenSaver.Run(),
                CommandKind.ScreenSaverPreview => ExitCodes.Ok, // no preview in the Control Panel thumbnail
                CommandKind.ScreenSaverConfigure => ShowScreenSaverInfo(),
                _ => RunCommand(cmd),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"'{cmd.Kind}' failed", ex);
            if (cmd.Kind == CommandKind.Run) return ExitCodes.Error;
            using var output = new Output(cmd.Quiet);
            output.Error(ex.Message);
            return ExitCodes.Error;
        }
    }

    private static int RunBanner()
    {
        // One banner process per user session. A second launch (Run key + manual start) just exits.
        CreateMutexW(0, true, SingleInstanceMutex);
        if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == ERROR_ALREADY_EXISTS) return ExitCodes.Ok;

        Log.Info($"Better Banner {AppInfo.Version} starting.");
        _ = new BannerHost();
        var code = Win32Window.RunMessageLoop();
        Log.Info("Better Banner exiting.");
        return code;
    }

    private static int RunCommand(ParsedCommand cmd)
    {
        using var output = new Output(cmd.Quiet);
        switch (cmd.Kind)
        {
            case CommandKind.Install: return Installer.Install(cmd, output);
            case CommandKind.Uninstall: return Installer.Uninstall(cmd, output);
            case CommandKind.Config: return Installer.Config(cmd, output);
            case CommandKind.Show: return Installer.Show(output);
            case CommandKind.Version:
                output.Line(AppInfo.Version);
                return ExitCodes.Ok;
            default:
                if (cmd.Error is not null) output.Error(cmd.Error + Environment.NewLine);
                output.Line(CommandLine.Usage);
                return cmd.Error is null ? ExitCodes.Ok : ExitCodes.BadArguments;
        }
    }

    private static int ShowScreenSaverInfo()
    {
        MessageBoxW(0, "The Better Banner screen saver is configured by your administrator.", "Better Banner", MB_OK | MB_ICONINFORMATION);
        return ExitCodes.Ok;
    }
}

/// <summary>
/// Command output. The exe is a GUI program (so the banner never flashes a console), so it attaches to the console
/// it was started from; when there is none (e.g. double-clicked) the output is shown in a message box instead.
/// Tip: in a script use "start /wait BetterBanner.exe install ..." to wait for the exit code.
/// </summary>
internal sealed class Output : IDisposable
{
    private readonly bool _quiet;
    private readonly bool _console;
    private readonly StringBuilder _buffer = new();
    private bool _hadError;

    public Output(bool quiet)
    {
        _quiet = quiet;
        _console = AttachConsole(ATTACH_PARENT_PROCESS);
        if (_console) Console.WriteLine();
    }

    public void Line(string text)
    {
        if (_quiet) return;
        if (_console) Console.WriteLine(text);
        else _buffer.AppendLine(text);
    }

    public void Error(string text)
    {
        _hadError = true;
        if (_console) Console.Error.WriteLine("Error: " + text);
        else _buffer.AppendLine("Error: " + text);
    }

    public void Dispose()
    {
        if (_console || _buffer.Length == 0) return;
        if (_quiet && !_hadError) return;
        MessageBoxW(0, _buffer.ToString().TrimEnd(), "Better Banner", MB_OK | (_hadError ? MB_ICONERROR : MB_ICONINFORMATION) | MB_SETFOREGROUND);
    }
}
