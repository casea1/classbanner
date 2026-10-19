using System.ComponentModel;
using System.Runtime.InteropServices;
using static ClassBanner.Native;

namespace ClassBanner;

/// <summary>Creates, starts, stops and deletes the banner service (install / uninstall run as administrator).</summary>
internal static unsafe class ServiceControl
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Creates the service, or updates its configuration: automatic start, LocalSystem, restart on failure.</summary>
    public static void Install(string name, string displayName, string description, string binaryPath)
    {
        WithManager(manager =>
        {
            var service = OpenServiceW(manager, name, SERVICE_ALL_ACCESS);
            if (service == 0)
            {
                service = CreateServiceW(manager, name, displayName, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START,
                    SERVICE_ERROR_NORMAL, binaryPath, null, 0, null, null, null);
                if (service == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the ClassBanner service");
            }
            else if (!ChangeServiceConfigW(service, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, binaryPath,
                         null, 0, null, null, null, displayName))
            {
                var error = Marshal.GetLastPInvokeError();
                CloseServiceHandle(service);
                throw new Win32Exception(error, "Could not update the ClassBanner service");
            }

            try
            {
                fixed (char* text = description)
                {
                    var info = text; // SERVICE_DESCRIPTIONW { LPWSTR lpDescription; }
                    ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, &info);
                }

                // If the service itself ever dies, Windows restarts it after 5 seconds (each failure, reset after a day).
                var actions = stackalloc SC_ACTION[3];
                for (var i = 0; i < 3; i++) actions[i] = new SC_ACTION { Type = SC_ACTION_RESTART, Delay = 5000 };
                var failure = new SERVICE_FAILURE_ACTIONSW { dwResetPeriod = 86400, cActions = 3, lpsaActions = actions };
                if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_FAILURE_ACTIONS, &failure))
                    Log.Warn($"Could not set the service's recovery actions (error {Marshal.GetLastPInvokeError()}).");
            }
            finally
            {
                CloseServiceHandle(service);
            }
        });
    }

    public static void Start(string name)
    {
        WithService(name, service =>
        {
            if (!StartServiceW(service, 0, 0) && Marshal.GetLastPInvokeError() is var error && error != ERROR_SERVICE_ALREADY_RUNNING)
                throw new Win32Exception(error, "Could not start the ClassBanner service");
        });
    }

    /// <summary>Stops the service and waits until it has (it ends the banners it started). No-op when not installed.</summary>
    public static void Stop(string name)
    {
        WithService(name, service =>
        {
            if (!ControlService(service, SERVICE_CONTROL_STOP, out var status))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ERROR_SERVICE_NOT_ACTIVE) return;
                throw new Win32Exception(error, "Could not stop the ClassBanner service");
            }

            var deadline = DateTime.UtcNow + StopTimeout;
            while (status.dwCurrentState != SERVICE_STOPPED)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The ClassBanner service did not stop within 30 seconds.");
                Thread.Sleep(250);
                if (!QueryServiceStatus(service, out status)) break;
            }
        });
    }

    public static void Delete(string name)
    {
        WithService(name, service =>
        {
            if (!DeleteService(service) && Marshal.GetLastPInvokeError() is var error && error != ERROR_SERVICE_MARKED_FOR_DELETE)
                throw new Win32Exception(error, "Could not remove the ClassBanner service");
        });
    }

    public static bool Exists(string name)
    {
        var exists = false;
        WithService(name, _ => exists = true);
        return exists;
    }

    private static void WithService(string name, Action<nint> action)
    {
        WithManager(manager =>
        {
            var service = OpenServiceW(manager, name, SERVICE_ALL_ACCESS);
            if (service == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ERROR_SERVICE_DOES_NOT_EXIST) return;
                throw new Win32Exception(error, "Could not open the ClassBanner service");
            }
            try
            {
                action(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        });
    }

    private static void WithManager(Action<nint> action)
    {
        var manager = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the Service Control Manager");
        try
        {
            action(manager);
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }
}
