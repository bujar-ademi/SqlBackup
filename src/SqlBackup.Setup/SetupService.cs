using Microsoft.Win32;
using SqlBackup.Core;
using System.Diagnostics;
using System.ServiceProcess;

namespace SqlBackup.Setup;

public sealed class SetupService(SetupPaths paths)
{
    public const string Name = "AbaSqlBackup";

    public bool Exists()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name);
        if (key is null)
        {
            return false;
        }

        string expected = Path.Combine(paths.InstallDirectory, "Service", "SqlBackup.Service.exe");
        string imagePath = Environment.ExpandEnvironmentVariables(key.GetValue("ImagePath") as string ?? string.Empty).Trim('"');
        string account = key.GetValue("ObjectName") as string ?? string.Empty;
        if (!imagePath.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || (account != "LocalSystem" && !account.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("AbaSqlBackup belongs to another path or service account. Setup will not replace or change its identity.");
        }

        return true;
    }

    public bool IsRunning()
    {
        if (!Exists())
        {
            return false;
        }

        using var service = new ServiceController(Name);
        return service.Status != ServiceControllerStatus.Stopped;
    }

    public async Task BeginMaintenanceAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await new ServiceClient().SendAsync(new ServiceRequest("BeginMaintenance"), timeout.Token);
        if (!response.Success)
        {
            throw new ArgumentException(response.Message);
        }
    }

    public void Stop()
    {
        if (!Exists())
        {
            return;
        }

        using var service = new ServiceController(Name);
        if (service.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        if (service.Status != ServiceControllerStatus.StopPending)
        {
            service.Stop();
        }

        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
    }

    public void Configure()
    {
        string executable = Path.Combine(paths.InstallDirectory, "Service", "SqlBackup.Service.exe");
        if (!File.Exists(executable))
        {
            throw new IOException("The service executable is missing.");
        }

        RunSc(Exists() ? "config" : "create", Name, "binPath=", "\"" + executable + "\"",
            "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "AbaSqlBackup");
        RunSc("description", Name, "SQL Server backup scheduling, verification and storage by Bujar Ademi.");
        RunSc("failure", Name, "reset=", "86400", "actions=", "restart/60000/restart/120000");
    }

    public async Task StartAndCheckAsync()
    {
        using (var service = new ServiceController(Name))
        {
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                service.Start();
            }

            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(3));
                var response = await new ServiceClient().SendAsync(new ServiceRequest("SetupReadiness"), attempt.Token);
                if (!response.Success)
                {
                    throw new ArgumentException("The service cannot initialize its data or decrypt active credentials. Setup remains in maintenance; inspect the recovery guide.");
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException && !timeout.IsCancellationRequested)
            {
                await Task.Delay(500, timeout.Token);
            }
        }
    }

    public void Remove()
    {
        if (Exists())
        {
            Stop();
            RunSc("delete", Name);
        }
    }

    private static void RunSc(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException("Service control could not start.");
        // sc output is small; no application configuration or secret is passed to this process.
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException("Windows service control failed. Inspect the service and setup log.");
        }
    }
}
