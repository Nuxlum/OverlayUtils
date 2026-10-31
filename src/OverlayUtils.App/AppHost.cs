using System.Diagnostics;
using System.Windows;
using OverlayUtils.App.Tray;
using OverlayUtils.Core;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Legacy;
using OverlayUtils.Core.Localization;
using OverlayUtils.Core.Platform;

namespace OverlayUtils.App;

/// <summary>Which exe started us. Decided by the launcher, not guessed.</summary>
public enum LauncherKind { Standard, UIAccess }

/// <summary>
/// Entry point shared by both launchers.
///
/// Command line (all optional, used internally when switching modes):
///   --helper enable-full-mode --source &lt;dir&gt;   elevated, then exits
///   --helper disable-full-mode                  elevated, then exits
///   --helper remove-legacy                      elevated, then exits
///   --after-switch &lt;pid&gt;     wait for the previous instance to exit first
///   --finish-disable-full-mode  second half of leaving full mode
/// </summary>
public static class AppHost
{
    public static int Run(string[] args, LauncherKind kind)
    {
        var options = CommandLine.Parse(args);
        Loc.SetLanguage(ConfigStore.Load(AppPaths.ConfigFile).Language);

        if (options.Helper is { } helper)
            return (int)RunHelper(helper, options);

        if (options.AfterSwitchPid is { } pid)
            WaitForExit(pid);

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            MessageBox.Show(Loc.T("Dialog.AlreadyRunning"), AppPaths.AppName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }

        var migrated = ConfigMigration.MigrateIfNeeded(AppPaths.LegacyConfigDir, AppPaths.ConfigDir);
        var config = new ConfigStore(AppPaths.ConfigFile);
        Loc.SetLanguage(config.Current.Language);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true; // keep the tray alive; the error is in errors.log
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception: " + e.ExceptionObject);

        using var tray = new TrayController(app, config, kind);
        app.Startup += (_, _) => tray.Start(migrated, options.FinishDisableFullMode);
        Log.Info($"Started {kind} v{typeof(AppHost).Assembly.GetName().Version} uiAccess={ProcessInfo.HasUIAccess}");
        return app.Run();
    }

    private static HelperResult RunHelper(string helper, CommandLine options)
    {
        if (!ProcessInfo.IsElevated) return HelperResult.Failed;
        return helper switch
        {
            "enable-full-mode" when options.Source is { } source => FullModeInstaller.Enable(source),
            "disable-full-mode" => FullModeInstaller.Disable(),
            "remove-legacy" => RemoveLegacy(),
            _ => HelperResult.Failed,
        };
    }

    private static HelperResult RemoveLegacy()
    {
        LegacyInstall.Remove();
        return HelperResult.Ok;
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(15_000);
        }
        catch (ArgumentException) { /* already gone */ }
        catch (InvalidOperationException) { }
    }
}

internal sealed record CommandLine(string? Helper, string? Source, int? AfterSwitchPid, bool FinishDisableFullMode)
{
    public static CommandLine Parse(string[] args)
    {
        string? helper = null, source = null;
        int? pid = null;
        var finish = false;
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--helper": helper = Next(); break;
                case "--source": source = Next(); break;
                case "--after-switch": pid = int.TryParse(Next(), out var p) ? p : null; break;
                case "--finish-disable-full-mode": finish = true; break;
            }
        }
        return new CommandLine(helper, source, pid, finish);
    }
}
