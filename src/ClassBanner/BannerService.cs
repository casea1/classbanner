using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using ClassBanner.Core;
using static ClassBanner.Native;

namespace ClassBanner;

/// <summary>
/// "ClassBanner.exe service": a LocalSystem service that keeps a banner running in every signed-in session (issue #17).
/// It starts the banner as the signed-in user, with a process DACL that doesn't let that user end it, and starts it
/// again within seconds whenever it exits unexpectedly. Each unexpected exit and restart goes to the Application log.
/// </summary>
internal static unsafe class BannerService
{
    public const string Name = "ClassBanner";
    public const string DisplayName = "ClassBanner";
    public const string Description = "Keeps the classification banner running in every signed-in session.";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private static nint _statusHandle;
    private static nint _wake;
    private static volatile bool _stopping;
    private static volatile bool _shutdown;
    private static readonly HashSet<uint> LoggedOff = [];
    private static readonly HashSet<uint> LoggedOn = [];

    public static int Run()
    {
        fixed (char* name = Name)
        {
            var table = stackalloc SERVICE_TABLE_ENTRYW[2];
            table[0] = new SERVICE_TABLE_ENTRYW { lpServiceName = name, lpServiceProc = &ServiceMain };
            table[1] = default;
            if (!StartServiceCtrlDispatcherW(table))
            {
                // 1063: not started by the Service Control Manager (someone ran "ClassBanner.exe service" by hand).
                MessageBoxW(0, "This command is run by Windows as the ClassBanner service.", "ClassBanner", MB_OK | MB_ICONINFORMATION);
                return ExitCodes.BadArguments;
            }
        }
        return ExitCodes.Ok;
    }

    [UnmanagedCallersOnly]
    private static void ServiceMain(uint argc, nint argv)
    {
        try
        {
            _statusHandle = RegisterServiceCtrlHandlerExW(Name, &Handler, 0);
            _wake = CreateEventW(0, false, false, 0);
            SetState(SERVICE_RUNNING);
            Log.Info($"ClassBanner service {AppInfo.Version} started.");
            new Watcher().Run();
        }
        catch (Exception ex)
        {
            Log.Error("Banner service failed", ex);
            EventLog.Write(EVENTLOG_ERROR_TYPE, EventLog.ServiceError, $"The ClassBanner service failed: {ex.Message}");
        }
        Log.Info("ClassBanner service stopped.");
        SetState(SERVICE_STOPPED);
    }

    [UnmanagedCallersOnly]
    private static uint Handler(uint control, uint eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case SERVICE_CONTROL_STOP:
                _stopping = true;
                SetState(SERVICE_STOP_PENDING);
                SetEvent(_wake);
                return 0;
            case SERVICE_CONTROL_SHUTDOWN:
                _shutdown = true;
                _stopping = true;
                SetEvent(_wake);
                return 0;
            case SERVICE_CONTROL_SESSIONCHANGE:
                if (eventData != 0 && eventType is WTS_SESSION_LOGON or WTS_SESSION_LOGOFF)
                {
                    // WTSSESSION_NOTIFICATION { DWORD cbSize; DWORD dwSessionId; }
                    var session = ((uint*)eventData)[1];
                    lock (LoggedOff)
                    {
                        if (eventType == WTS_SESSION_LOGOFF) LoggedOff.Add(session);
                        else
                        {
                            LoggedOff.Remove(session);
                            LoggedOn.Add(session);
                        }
                    }
                }
                SetEvent(_wake);
                return 0;
            case SERVICE_CONTROL_INTERROGATE:
                return 0;
            default:
                return 120; // ERROR_CALL_NOT_IMPLEMENTED
        }
    }

    private static void SetState(uint state)
    {
        if (_statusHandle == 0) return;
        var status = new SERVICE_STATUS
        {
            dwServiceType = SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = state == SERVICE_RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN | SERVICE_ACCEPT_SESSIONCHANGE : 0,
            dwWaitHint = state == SERVICE_STOP_PENDING ? 10_000u : 0,
        };
        SetServiceStatus(_statusHandle, status);
    }

    private sealed class Slot
    {
        public nint Process;
        /// <summary>The banner's first thread, which runs its windows: watched so a frozen or ended UI is noticed.</summary>
        public nint Thread;
        public uint ProcessId;
        public string User = "";
        public bool UserExited;
        public bool LaunchFailing;
        public DateTime NextStart;
        public RestartDecision? Pending;
        public string? HealthProblem;
        public readonly RestartThrottle Throttle = new();
    }

    private sealed class Watcher
    {
        private readonly Dictionary<uint, Slot> _slots = [];

        public void Run()
        {
            while (!_stopping)
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    Log.Error("Banner service check failed", ex);
                }
                WaitForSingleObject(_wake, 1000);
            }

            // Stopped by uninstall or upgrade: end the banners we started (the new version starts fresh ones). At
            // shutdown leave them to Windows, so the marking stays up while applications close.
            foreach (var slot in _slots.Values)
            {
                if (slot.Process == 0) continue;
                if (!_shutdown) TerminateProcess(slot.Process, 0);
                CloseHandle(slot.Process);
                if (slot.Thread != 0) CloseHandle(slot.Thread);
            }
            _slots.Clear();
        }

        private void Tick()
        {
            var now = DateTime.UtcNow;
            var signedIn = SignedInSessions();

            uint[] newLogons;
            lock (LoggedOff)
            {
                newLogons = [.. LoggedOn];
                LoggedOn.Clear();
            }
            foreach (var session in newLogons)
            {
                // A new sign-in resets "the user chose Exit".
                if (_slots.TryGetValue(session, out var slot)) slot.UserExited = false;
            }

            // An administrator turning AllowUserExit off brings back the banners users had closed (issue #26).
            if (_slots.Values.Any(s => s.UserExited) && !AllowUserExit())
            {
                foreach (var (session, slot) in _slots.Where(s => s.Value.UserExited))
                {
                    slot.UserExited = false;
                    slot.NextStart = now;
                    Log.Info($"AllowUserExit is off now; starting the banner again for {slot.User} in session {session}.");
                }
            }

            foreach (var session in _slots.Keys.Where(s => !signedIn.Contains(s)).ToList())
            {
                if (_slots[session].Process != 0) CloseHandle(_slots[session].Process);
                if (_slots[session].Thread != 0) CloseHandle(_slots[session].Thread);
                _slots.Remove(session);
            }

            foreach (var session in signedIn)
            {
                if (!_slots.TryGetValue(session, out var slot)) _slots[session] = slot = new Slot { NextStart = now };

                if (slot.Process != 0 && WaitForSingleObject(slot.Process, 0) != WAIT_OBJECT_0) CheckHealth(session, slot);
                if (slot.Process != 0 && WaitForSingleObject(slot.Process, 0) == WAIT_OBJECT_0)
                    OnExited(session, slot, now);

                if (slot.Process == 0 && !slot.UserExited && now >= slot.NextStart)
                    Start(session, slot, now);
            }
        }

        /// <summary>
        /// A banner whose process is alive is not necessarily showing (issue #17): its window thread may have been
        /// suspended or ended. A suspended thread is resumed; if it has ended, the process is ended and restarted.
        /// </summary>
        private static void CheckHealth(uint session, Slot slot)
        {
            if (slot.Thread == 0) return;
            if (WaitForSingleObject(slot.Thread, 0) == WAIT_OBJECT_0)
            {
                // A banner that is ending (killed, crashed or closed) loses its window thread a moment before the
                // process itself ends: that is an ordinary exit, handled (and logged as 1001) by OnExited.
                if (WaitForSingleObject(slot.Process, 1000) == WAIT_OBJECT_0) return;
                TerminateProcess(slot.Process, BannerHealthExitCode);
                WaitForSingleObject(slot.Process, 2000);
                slot.HealthProblem = "its window thread had ended";
                EventLog.Write(EVENTLOG_WARNING_TYPE, EventLog.NotResponding,
                    $"The banner for {slot.User} in session {session} (process {slot.ProcessId}) was running but showing nothing: its window thread had ended. It was ended and restarted.");
                return;
            }

            var count = ResumeThread(slot.Thread);
            if (count is 0 or uint.MaxValue) return;
            while (count > 1 && count != uint.MaxValue) count = ResumeThread(slot.Thread);
            EventLog.Write(EVENTLOG_WARNING_TYPE, EventLog.NotResponding,
                $"The banner for {slot.User} in session {session} (process {slot.ProcessId}) had been suspended (frozen). It was resumed.");
        }

        private static void OnExited(uint session, Slot slot, DateTime now)
        {
            GetExitCodeProcess(slot.Process, out var exitCode);
            CloseHandle(slot.Process);
            slot.Process = 0;
            if (slot.Thread != 0) CloseHandle(slot.Thread);
            slot.Thread = 0;

            var decision = RestartPolicy.Decide((int)exitCode, userSignedIn: true, _stopping, AllowUserExit());
            if (slot.HealthProblem is { } problem)
            {
                decision = new RestartDecision(RestartAction.Restart, false, problem); // already logged as event 1005
                slot.HealthProblem = null;
            }
            switch (decision.Action)
            {
                case RestartAction.StayStopped:
                    slot.UserExited = true;
                    EventLog.Write(EVENTLOG_INFORMATION_TYPE, EventLog.UserExit,
                        $"The banner for {slot.User} in session {session} was closed by the user (AllowUserExit is on). It starts again at their next sign-in.");
                    break;
                case RestartAction.RetryLater:
                    slot.NextStart = now + RetryDelay;
                    break;
                case RestartAction.Restart:
                    slot.Pending = decision;
                    slot.NextStart = now + slot.Throttle.NextDelay(now);
                    if (decision.Unexpected) Log.Warn($"Banner for {slot.User} in session {session} (process {slot.ProcessId}) stopped: {decision.Reason}.");
                    break;
            }
        }

        private static void Start(uint session, Slot slot, DateTime now)
        {
            if (!TryLaunch(session, out var process, out var thread, out var processId, out var user, out var error))
            {
                if (!slot.LaunchFailing)
                {
                    EventLog.Write(EVENTLOG_ERROR_TYPE, EventLog.StartFailed, $"Could not start the banner in session {session}: {error}. Retrying every {RetryDelay.TotalSeconds:0} seconds.");
                    slot.LaunchFailing = true;
                }
                slot.NextStart = now + RetryDelay;
                return;
            }

            slot.LaunchFailing = false;
            slot.Process = process;
            slot.Thread = thread;
            slot.ProcessId = processId;
            slot.User = user;
            var pending = slot.Pending;
            slot.Pending = null;
            if (pending is { Unexpected: true })
            {
                EventLog.Write(EVENTLOG_WARNING_TYPE, EventLog.Restarted,
                    $"The banner for {user} in session {session} stopped: {pending.Reason}. It was restarted (process {processId}).");
            }
            else
            {
                Log.Info($"Started the banner for {user} in session {session} (process {processId}){(pending is null ? "" : $"; {pending.Reason}")}.");
                if (pending is null)
                    EventLog.Write(EVENTLOG_INFORMATION_TYPE, EventLog.Started, $"Started the banner for {user} in session {session} (process {processId}).");
            }
        }

        private static bool AllowUserExit()
        {
            try
            {
                return RegistryPolicySource.LoadEffective().Settings.AllowUserExit;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read settings: {ex.Message}");
                return false;
            }
        }

        private static HashSet<uint> SignedInSessions()
        {
            var result = new HashSet<uint>();
            if (!WTSEnumerateSessionsW(0, 0, 1, out var sessions, out var count)) return result;
            try
            {
                lock (LoggedOff)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var id = sessions[i].SessionId;
                        if (id == 0 || LoggedOff.Contains(id)) continue;
                        // A session has a signed-in user exactly when it has a user token.
                        if (!WTSQueryUserToken(id, out var token)) continue;
                        CloseHandle(token);
                        result.Add(id);
                    }
                }
            }
            finally
            {
                WTSFreeMemory(sessions);
            }
            return result;
        }
    }

    /// <summary>Starts "ClassBanner.exe banner --managed" as the session's user, on their desktop.</summary>
    private static bool TryLaunch(uint session, out nint process, out nint thread, out uint processId, out string user, out string error)
    {
        process = 0;
        thread = 0;
        processId = 0;
        user = $"session {session}";
        error = "";
        var exe = Environment.ProcessPath ?? Installer.InstalledExe;
        nint userToken = 0, primary = 0, environment = 0;
        try
        {
            if (!WTSQueryUserToken(session, out userToken)) return Fail(out error, "no user token");
            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, 0, SecurityImpersonation, TokenPrimary, out primary)) return Fail(out error, "DuplicateTokenEx");

            string sid;
            using (var identity = new WindowsIdentity(primary))
            {
                user = identity.Name;
                sid = identity.User?.Value ?? throw new InvalidOperationException("The session's token has no user.");
            }

            // Every object the banner creates (each thread the runtime starts, too) gets this DACL, so its user can't
            // suspend or end its threads (issue #17). Owner Rights limits what the user gets as their owner.
            SetDefaultDacl(primary, sid);
            if (!CreateEnvironmentBlock(out environment, primary, false)) environment = 0;

            var commandLine = $"\"{exe}\" banner --managed\0".ToCharArray();
            fixed (char* cmd = commandLine)
            fixed (char* desktop = "winsta0\\default")
            {
                var startup = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW), lpDesktop = desktop };
                if (!CreateProcessAsUserW(primary, exe, cmd, 0, 0, false, CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                        environment, Path.GetDirectoryName(exe), ref startup, out var info))
                    return Fail(out error, "CreateProcessAsUser");

                Harden(info.hProcess, sid);
                HardenThread(info.hThread, sid);
                ResumeThread(info.hThread);
                thread = info.hThread;
                process = info.hProcess;
                processId = info.dwProcessId;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (environment != 0) DestroyEnvironmentBlock(environment);
            if (primary != 0) CloseHandle(primary);
            if (userToken != 0) CloseHandle(userToken);
        }

        static bool Fail(out string error, string what)
        {
            error = $"{what} failed (error {Marshal.GetLastPInvokeError()})";
            return false;
        }
    }

    /// <summary>Exit code the service gives a banner it ends because it had stopped showing.</summary>
    private const uint BannerHealthExitCode = 0xBB0017;

    /// <summary>The user's rights on the banner's objects: read and wait only (no suspend, terminate or write-DACL).</summary>
    private static string UserLimitedSddl(string userSid) =>
        $"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{userSid})(A;;RC;;;OW)";

    private static void SetDefaultDacl(nint token, string userSid)
    {
        var acl = new RawSecurityDescriptor(UserLimitedSddl(userSid)).DiscretionaryAcl
            ?? throw new InvalidOperationException("No DACL.");
        var bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        fixed (byte* pAcl = bytes)
        {
            var info = (nint)pAcl; // TOKEN_DEFAULT_DACL { PACL DefaultDacl; }
            if (!SetTokenInformation(token, TokenDefaultDacl, &info, (uint)sizeof(nint)))
                Log.Warn($"Could not set the banner's default DACL (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    private static void HardenThread(nint thread, string userSid)
    {
        var descriptor = new RawSecurityDescriptor($"O:SYG:SY{UserLimitedSddl(userSid)}");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        fixed (byte* sd = bytes)
        {
            if (!SetKernelObjectSecurity(thread, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, sd))
                Log.Warn($"Could not protect the banner's thread (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    /// <summary>
    /// Owner SYSTEM; SYSTEM and (elevated) Administrators get full access; the user only gets to query and wait on it.
    /// Without terminate or write-DACL rights, Task Manager's "End task" fails for the user. Elevated admins can still
    /// end it, and the service then starts it again.
    /// </summary>
    private static void Harden(nint process, string userSid)
    {
        const string QuerySyncRead = "0x101410"; // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | QUERY_INFORMATION | VM_READ
        var descriptor = new RawSecurityDescriptor($"O:SYG:SYD:(A;;0x1FFFFF;;;SY)(A;;0x1FFFFF;;;BA)(A;;{QuerySyncRead};;;{userSid})");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        fixed (byte* sd = bytes)
        {
            if (!SetKernelObjectSecurity(process, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, sd))
                Log.Warn($"Could not protect the banner process (error {Marshal.GetLastPInvokeError()}); the user can end it, and it will be restarted.");
        }
    }
}

/// <summary>Application event log entries (source "ClassBanner"), so auditors find stops and restarts in one place.</summary>
internal static unsafe class EventLog
{
    public const string Source = "ClassBanner";
    public const uint Started = 1000;
    public const uint Restarted = 1001;
    public const uint UserExit = 1002;
    public const uint StartFailed = 1003;
    public const uint ServiceError = 1004;
    public const uint NotResponding = 1005;

    public static void Write(ushort type, uint id, string message)
    {
        if (type == EVENTLOG_ERROR_TYPE) Log.Error(message);
        else if (type == EVENTLOG_WARNING_TYPE) Log.Warn(message);
        else Log.Info(message);

        var log = RegisterEventSourceW(null, Source);
        if (log == 0) return;
        try
        {
            fixed (char* text = message)
            {
                var strings = stackalloc char*[1];
                strings[0] = text;
                ReportEventW(log, type, 0, id, 0, 1, 0, strings, 0);
            }
        }
        finally
        {
            DeregisterEventSource(log);
        }
    }
}
