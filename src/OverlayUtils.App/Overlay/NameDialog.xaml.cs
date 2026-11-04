using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Localization;

namespace OverlayUtils.App.Overlay;

/// <summary>
/// Modeless name prompt (add / rename a channel, save a preset). Shown with <see cref="Show"/> rather than
/// ShowDialog, so no nested message loop runs inside the navigator's action and the overlay keeps rendering.
/// <see cref="Accepted"/> is raised only for OK / Enter / pad A (<see cref="Accept"/>) with a non-empty trimmed name;
/// Cancel, Esc, pad B, the overlay hiding or <see cref="Dismiss"/> close it without it. UI thread only.
/// </summary>
internal sealed partial class NameDialog : Window
{
    bool _done;

    public NameDialog(string title, string suggested, nint owner, Color accent, bool padConnected)
    {
        InitializeComponent();
        Resources["Accent"] = Frozen(accent);
        Prompt.Text = title;
        if (padConnected)
        {
            PadHint.Text = Loc.T("Dialog.Name.PadHint");
            PadHint.Visibility = Visibility.Visible;
        }
        OkButton.Content = Loc.T("Dialog.Name.Ok");
        CancelButton.Content = Loc.T("Dialog.Name.Cancel");
        NameBox.Text = suggested;
        if (owner != 0) new WindowInteropHelper(this).Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Loaded += (_, _) =>
        {
            Activate();
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The trimmed, non-empty name the user confirmed.</summary>
    public event Action<string>? Accepted;

    /// <summary>Closes without a result (the overlay hid, or the app exits).</summary>
    public void Dismiss()
    {
        _done = true;
        try { Close(); }
        catch (Exception e) { Log.Error("Name dialog close", e); }
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Enter acts on what has focus: the text box or OK saves, Cancel cancels (Tab can reach either button).
            var focused = Keyboard.FocusedElement;
            if (ReferenceEquals(focused, CancelButton))
            {
                e.Handled = true;
                Dismiss();
            }
            else if (ReferenceEquals(focused, NameBox) || ReferenceEquals(focused, OkButton))
            {
                e.Handled = true;
                Accept();
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Dismiss();
        }
    }

    void OnOk(object sender, RoutedEventArgs e) => Accept();

    void OnCancel(object sender, RoutedEventArgs e) => Dismiss();

    /// <summary>OK: the name in the box (the suggestion unless typed over); an empty name keeps the prompt open.</summary>
    public void Accept()
    {
        if (_done) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameBox.Focus(); // nothing to save: keep the prompt open
            return;
        }
        _done = true;
        try { Close(); }
        catch (Exception e) { Log.Error("Name dialog close", e); }
        try { Accepted?.Invoke(name); }
        catch (Exception e) { Log.Error("Name dialog", e); }
    }

    static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
