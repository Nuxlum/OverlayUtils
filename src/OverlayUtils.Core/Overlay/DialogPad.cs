namespace OverlayUtils.Core.Overlay;

public enum DialogPadAction { Ignore, Accept, Cancel }

/// <summary>
/// Pad input while the name dialog is open: A accepts the shown name (the suggestion unless typed over), B cancels,
/// every other command is swallowed so the overlay behind the dialog does not move.
/// </summary>
public static class DialogPad
{
    public static DialogPadAction Route(OverlayCommand command) => command switch
    {
        OverlayCommand.A => DialogPadAction.Accept,
        OverlayCommand.B => DialogPadAction.Cancel,
        _ => DialogPadAction.Ignore,
    };
}
