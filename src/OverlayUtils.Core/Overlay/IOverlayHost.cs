namespace OverlayUtils.Core.Overlay;

/// <summary>What the overlay content needs from the app around it (implemented in the App project).</summary>
public interface IOverlayHost
{
    bool InFullMode { get; }

    /// <summary>False when the overlay was opened over the taskbar or desktop: Alt+F4 would hit the shell.</summary>
    bool HasPreviousWindow { get; }

    /// <summary>Switch + tray icon + notification, like the hotkeys.</summary>
    void SwitchMonitor(string profile);

    void ToggleStartup();

    /// <summary>File picker, as the tray menu.</summary>
    void ChooseMonitorSwitcher();

    /// <summary>Overlay flow: no dialogs, failures are reported as notifications.</summary>
    void SwitchFullMode();

    void Exit();
}
