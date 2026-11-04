namespace OverlayUtils.Core.Overlay;

/// <summary>What the overlay content needs from the app around it (implemented in the App project).</summary>
public interface IOverlayHost
{
    bool InFullMode { get; }

    /// <summary>
    /// True only when the window that was in front when the overlay opened is still alive and has the foreground
    /// again. Read at the moment an action runs; when false (opened over the taskbar or desktop, or another window
    /// took the foreground) Alt+F4 would hit the wrong window.
    /// </summary>
    bool PreviousWindowIsForeground { get; }

    /// <summary>Switch + tray icon + notification, like the hotkeys.</summary>
    void SwitchMonitor(string profile);

    void ToggleStartup();

    /// <summary>File picker, as the tray menu.</summary>
    void ChooseMonitorSwitcher();

    /// <summary>Overlay flow: no dialogs, failures are reported as notifications.</summary>
    void SwitchFullMode();

    void Exit();

    /// <summary>
    /// Small name dialog owned by the overlay, titled <paramref name="title"/> (what is being named) and prefilled with
    /// <paramref name="suggested"/>; <paramref name="done"/> runs on the UI thread with the accepted text, and not at
    /// all on cancel. A new request replaces a prompt still open (that one is cancelled).
    /// </summary>
    void AskName(string title, string suggested, Action<string> done);

    /// <summary>File picker for a program (.exe); <paramref name="done"/> runs on the UI thread with the full path, not on cancel.</summary>
    void ChooseProgram(Action<string> done);
}
