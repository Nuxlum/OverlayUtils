using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using OverlayUtils.Core;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Platform;

namespace OverlayUtils.App;

/// <summary>
/// The unelevated half of switching between standard and full mode.
/// Admin-only steps run in an elevated "OverlayUtils.exe --helper ..."
/// (see <see cref="FullModeInstaller"/>); everything per-user happens here.
/// </summary>
internal static class ModeSwitcher
{
    private const int ErrorCancelled = 1223; // the user said no to the UAC prompt

    public enum Outcome { Ok, Cancelled, HelperFailed, LaunchFailed }

    public sealed record Result(Outcome Outcome, HelperResult Helper = HelperResult.Ok, string? Detail = null);

    /// <summary>
    /// Standard -> full: install to Program Files (elevated), re-point start-up,
    /// start the UIAccess exe. On <see cref="Outcome.Ok"/> the caller must exit.
    /// </summary>
    public static Result EnableFullMode()
    {
        var helper = RunElevatedHelper($"--helper enable-full-mode --source {Quote(AppPaths.AppDir)}");
        if (helper.Outcome != Outcome.Ok) return helper;

        var target = Path.Combine(AppPaths.FullModeInstallDir, AppPaths.UIAccessExeName);
        StartupRegistration.RetargetIfEnabled(target);
        try
        {
            // ShellExecute is required: Windows refuses UIAccess to CreateProcess.
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Arguments = $"--after-switch {Environment.ProcessId}",
            })?.Dispose();
            return new Result(Outcome.Ok);
        }
        catch (Win32Exception e)
        {
            Log.Error("Starting the UIAccess exe", e);
            StartupRegistration.RetargetIfEnabled(Path.Combine(AppPaths.AppDir, AppPaths.StandardExeName));
            return new Result(Outcome.LaunchFailed, Detail: e.Message);
        }
    }

    /// <summary>
    /// Full -> standard, step 1 (in the UIAccess instance): copy the app to a
    /// per-user folder and start it there. On Ok the caller must exit; step 2
    /// (<see cref="FinishDisableFullMode"/>) runs in the new instance, because
    /// the Program Files folder cannot be deleted while we run from it.
    /// </summary>
    public static Result BeginDisableFullMode()
    {
        if (!FullModeInstaller.CopyInto(AppPaths.AppDir, AppPaths.StandardInstallDir))
            return new Result(Outcome.HelperFailed, HelperResult.CopyFailed);
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(AppPaths.StandardInstallDir, AppPaths.StandardExeName))
            {
                UseShellExecute = true,
                Arguments = $"--after-switch {Environment.ProcessId} --finish-disable-full-mode",
            })?.Dispose();
            return new Result(Outcome.Ok);
        }
        catch (Win32Exception e)
        {
            Log.Error("Starting the standard exe", e);
            return new Result(Outcome.LaunchFailed, Detail: e.Message);
        }
    }

    /// <summary>Full -> standard, step 2: re-point start-up, then remove Program Files copy and certificate.</summary>
    public static Result FinishDisableFullMode()
    {
        StartupRegistration.RetargetIfEnabled(Path.Combine(AppPaths.AppDir, AppPaths.StandardExeName));
        return FullModeInstaller.IsInstalled
            ? RunElevatedHelper("--helper disable-full-mode")
            : new Result(Outcome.Ok);
    }

    public static Result RemoveLegacy() => RunElevatedHelper("--helper remove-legacy");

    private static Result RunElevatedHelper(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(AppPaths.AppDir, AppPaths.StandardExeName))
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
            });
            if (process is null) return new Result(Outcome.HelperFailed, HelperResult.Failed);
            process.WaitForExit();
            var code = (HelperResult)process.ExitCode;
            return code == HelperResult.Ok ? new Result(Outcome.Ok) : new Result(Outcome.HelperFailed, code);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return new Result(Outcome.Cancelled);
        }
        catch (Win32Exception e)
        {
            Log.Error("Starting the elevated helper", e);
            return new Result(Outcome.HelperFailed, HelperResult.Failed, e.Message);
        }
    }

    /// <summary>Quotes a path for a command line; a trailing backslash would escape the closing quote.</summary>
    internal static string Quote(string path) => "\"" + path.TrimEnd('\\') + "\"";
}
