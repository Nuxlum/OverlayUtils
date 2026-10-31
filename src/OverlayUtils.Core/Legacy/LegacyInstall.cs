using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.Core.Legacy;

/// <summary>
/// The Python "AppUtili" install: Program Files\AppUtili(.old), an elevated
/// scheduled task "AppUtili" and the "CN=AppUtili Local Code Signing"
/// certificate trusted machine-wide. Detection works as a normal user;
/// removal needs admin and runs inside the elevated setup helper.
/// </summary>
public static class LegacyInstall
{
    public const string CertificateSubject = "CN=AppUtili Local Code Signing";
    private const string TaskName = AppPaths.LegacyAppName;

    public static bool IsPresent()
    {
        try
        {
            return Directory.Exists(AppPaths.LegacyInstallDir)
                || Directory.Exists(AppPaths.LegacyInstallDir + ".old")
                || TaskExists()
                || FindCertificates().Count > 0;
        }
        catch (Exception e)
        {
            Log.Error("Detecting AppUtili", e);
            return false;
        }
    }

    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName(AppPaths.LegacyAppName);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Elevated: stops AppUtili and removes its task, folders and trusted certificate.</summary>
    public static void Remove()
    {
        foreach (var p in Process.GetProcessesByName(AppPaths.LegacyAppName))
        {
            using (p)
            {
                try { p.Kill(); p.WaitForExit(5000); }
                catch (Exception e) { Log.Error("Stopping AppUtili", e); }
            }
        }

        if (TaskExists()) Schtasks("/Delete", "/TN", TaskName, "/F");

        foreach (var dir in new[] { AppPaths.LegacyInstallDir, AppPaths.LegacyInstallDir + ".old" })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception e) { Log.Error($"Deleting {dir}", e); }
        }

        foreach (var (storeName, cert) in FindCertificates())
        {
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(cert);
            cert.Dispose();
        }
        // The signing key in the developer's CurrentUser\My store is left alone:
        // it is not trusted by anything once the machine-wide copies are gone.
    }

    private static List<(StoreName, X509Certificate2)> FindCertificates()
    {
        var found = new List<(StoreName, X509Certificate2)>();
        foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            foreach (var cert in store.Certificates)
            {
                if (cert.Subject == CertificateSubject) found.Add((name, cert));
                else cert.Dispose();
            }
        }
        return found;
    }

    private static bool TaskExists() => Schtasks("/Query", "/TN", TaskName) == 0;

    private static int Schtasks(params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception e)
        {
            Log.Error("Running schtasks", e);
            return -1;
        }
    }
}
