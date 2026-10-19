using System.Runtime.InteropServices;

namespace ClassBanner;

/// <summary>Interop for the banner service: service control, sessions, starting a process in a session, event log.</summary>
internal static unsafe partial class Native
{
    // ---- Services ----
    public const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    public const uint SERVICE_AUTO_START = 2;
    public const uint SERVICE_ERROR_NORMAL = 1;
    public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

    public const uint SERVICE_STOPPED = 1;
    public const uint SERVICE_START_PENDING = 2;
    public const uint SERVICE_STOP_PENDING = 3;
    public const uint SERVICE_RUNNING = 4;

    public const uint SERVICE_ACCEPT_STOP = 0x1;
    public const uint SERVICE_ACCEPT_SHUTDOWN = 0x4;
    public const uint SERVICE_ACCEPT_SESSIONCHANGE = 0x80;

    public const uint SERVICE_CONTROL_STOP = 0x1;
    public const uint SERVICE_CONTROL_INTERROGATE = 0x4;
    public const uint SERVICE_CONTROL_SHUTDOWN = 0x5;
    public const uint SERVICE_CONTROL_SESSIONCHANGE = 0xE;

    public const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    public const uint SERVICE_ALL_ACCESS = 0xF01FF;
    public const uint SERVICE_CONFIG_DESCRIPTION = 1;
    public const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    public const int SC_ACTION_RESTART = 1;

    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    public const int ERROR_SERVICE_NOT_ACTIVE = 1062;
    public const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    public const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_TABLE_ENTRYW
    {
        public char* lpServiceName;
        public delegate* unmanaged<uint, nint, void> lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_FAILURE_ACTIONSW
    {
        public uint dwResetPeriod;
        public char* lpRebootMsg;
        public char* lpCommand;
        public uint cActions;
        public SC_ACTION* lpsaActions;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRYW* table);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint RegisterServiceCtrlHandlerExW(string serviceName, delegate* unmanaged<uint, uint, nint, nint, uint> handler, nint context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetServiceStatus(nint handle, in SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenServiceW(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateServiceW(nint manager, string name, string displayName, uint access, uint serviceType,
        uint startType, uint errorControl, string binaryPath, string? loadOrderGroup, nint tagId, string? dependencies,
        string? account, string? password);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeServiceConfigW(nint service, uint serviceType, uint startType, uint errorControl,
        string binaryPath, string? loadOrderGroup, nint tagId, string? dependencies, string? account, string? password,
        string displayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeServiceConfig2W(nint service, uint infoLevel, void* info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StartServiceW(nint service, uint argCount, nint args);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ControlService(nint service, uint control, out SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatus(nint service, out SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteService(nint service);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint handle);

    // ---- Sessions ----
    public const int WTS_SESSION_LOGON = 5;
    public const int WTS_SESSION_LOGOFF = 6;

    [StructLayout(LayoutKind.Sequential)]
    public struct WTS_SESSION_INFOW
    {
        public uint SessionId;
        public nint pWinStationName;
        public int State;
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSEnumerateSessionsW(nint server, uint reserved, uint version, out WTS_SESSION_INFOW* sessions, out uint count);

    [LibraryImport("wtsapi32.dll")]
    public static partial void WTSFreeMemory(void* memory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQueryUserToken(uint sessionId, out nint token);

    // ---- Processes ----
    public const uint CREATE_SUSPENDED = 0x4;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    public const uint CREATE_NEW_PROCESS_GROUP = 0x200;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint TOKEN_ALL_ACCESS = 0xF01FF;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;
    public const uint OWNER_SECURITY_INFORMATION = 0x1;
    public const uint DACL_SECURITY_INFORMATION = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOW
    {
        public uint cb;
        public char* lpReserved;
        public char* lpDesktop;
        public char* lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateTokenEx(nint token, uint access, nint attributes, int impersonationLevel, int tokenType, out nint newToken);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessAsUserW(nint token, string? applicationName, char* commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
        string? currentDirectory, ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyEnvironmentBlock(nint environment);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetKernelObjectSecurity(nint handle, uint securityInformation, byte* securityDescriptor);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(nint handle);

    // ---- Event log ----
    public const ushort EVENTLOG_ERROR_TYPE = 1;
    public const ushort EVENTLOG_WARNING_TYPE = 2;
    public const ushort EVENTLOG_INFORMATION_TYPE = 4;

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint RegisterEventSourceW(string? server, string source);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReportEventW(nint eventLog, ushort type, ushort category, uint eventId, nint userSid,
        ushort numStrings, uint dataSize, char** strings, nint rawData);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeregisterEventSource(nint eventLog);
}
