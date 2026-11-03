using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Overlay;

namespace OverlayUtils.App.Overlay;

/// <summary>
/// The overlay panel. It only draws <see cref="OverlayViewModel"/> and turns keyboard and mouse input into
/// <see cref="CommandIssued"/> / <see cref="RowClicked"/>; the controller owns the navigator, size and placement.
/// </summary>
internal sealed partial class OverlayWindow : Window
{
    const string AccentKey = "Accent";
    const string PanelBgKey = "PanelBg";
    static readonly Duration OpenDuration = TimeSpan.FromMilliseconds(150);
    const double OpenSlide = 24; // DIPs

    OverlayViewModel? _vm;

    public OverlayWindow()
    {
        InitializeComponent();
        Resources[AccentKey] = new SolidColorBrush(OverlayViewModel.FallbackAccent);
        Resources[PanelBgKey] = FindResource("Bg");
        SourceInitialized += (_, _) =>
        {
            WindowEffects.ApplyChrome(this);
            RefreshBackdrop();
        };
    }

    public event Action<OverlayCommand>? CommandIssued;

    /// <summary>Row index (navigator index) and the clicked option of a segmented row, if any.</summary>
    public event Action<int, int?>? RowClicked;

    public void Bind(OverlayViewModel vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        DataContext = vm;
        Resources[AccentKey] = vm.AccentBrush;
        vm.PropertyChanged += OnViewModelChanged;
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(OverlayViewModel.AccentBrush))
            Resources[AccentKey] = _vm.AccentBrush;
        else if (e.PropertyName == nameof(OverlayViewModel.Rows))
            // After the new rows are laid out, so their containers exist.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, BringSelectedIntoView);
    }

    /// <summary>
    /// Acrylic with a translucent panel where Windows allows it, else the solid palette. The controller calls it
    /// after every show: DWM keeps the acrylic only if it is set while the window is visible.
    /// </summary>
    public void RefreshBackdrop()
    {
        try
        {
            if (WindowEffects.TryApplyAcrylic(this))
            {
                Resources[PanelBgKey] = FindResource("AcrylicBg");
                return;
            }
            SetResourceReference(BackgroundProperty, "Bg");
            Resources[PanelBgKey] = FindResource("Bg");
        }
        catch (Exception e)
        {
            Log.Error("Overlay backdrop", e);
        }
    }

    /// <summary>
    /// Call right before Show: the panel slides in from its side of the screen and fades in, unless Windows
    /// "Animation effects" is off. Only the content moves (the window stays where the controller put it) and
    /// nothing waits for it, so focus is not delayed. Closing is never animated.
    /// </summary>
    public void PrepareOpening(string side)
    {
        Panel.BeginAnimation(OpacityProperty, null);
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        if (!SystemParameters.ClientAreaAnimation) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var from = side == OverlaySides.Right ? OpenSlide : -OpenSlide;
        Panel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, OpenDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        Slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(from, 0, OpenDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    void BringSelectedIntoView()
    {
        var row = _vm?.Rows.FirstOrDefault(r => r.IsSelected);
        if (row is not null && RowList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            container.BringIntoView();
    }

    // --- Keyboard -------------------------------------------------------------

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt+F4 would close (destroy) the window, F10 / Alt+Space open the system menu.
        if (e.Key == Key.System && e.SystemKey is Key.F4 or Key.F10 or Key.Space)
        {
            e.Handled = true;
            return;
        }
        // A held Enter must not confirm a pending Power option by itself; Esc and F2 would flicker pages; a held
        // Delete would remove one list entry after another.
        if (e.IsRepeat && e.Key is Key.Enter or Key.Escape or Key.F2 or Key.Delete)
        {
            e.Handled = true;
            return;
        }
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        OverlayCommand? command = e.Key switch
        {
            Key.Up => OverlayCommand.Up,
            Key.Down => OverlayCommand.Down,
            Key.Left => e.IsRepeat ? OverlayCommand.LeftRepeat : OverlayCommand.Left,
            Key.Right => e.IsRepeat ? OverlayCommand.RightRepeat : OverlayCommand.Right,
            Key.Enter => OverlayCommand.A,
            Key.Escape => OverlayCommand.B,
            Key.Tab => shift ? OverlayCommand.PrevTab : OverlayCommand.NextTab,
            Key.PageDown => OverlayCommand.NextTab,
            Key.PageUp => OverlayCommand.PrevTab,
            Key.F2 => OverlayCommand.Settings,
            Key.Delete => OverlayCommand.Remove, // Y on the pad: removes a disconnected device from its list
            _ => null,
        };
        if (command is not { } c) return;
        e.Handled = true;
        Issue(c);
    }

    // --- Mouse ----------------------------------------------------------------

    void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RowVm row) return;
        e.Handled = true;
        if (e.ClickCount > 1) return; // the second press of a double-click must not fire an armed confirm
        Click(row.Index, null);
    }

    void OnOptionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OptionVm option } element) return;
        // The option sits inside its row's template: walk up to the row's data.
        DependencyObject? node = element;
        while (node is not null && (node as FrameworkElement)?.DataContext is not RowVm)
            node = VisualTreeHelper.GetParent(node);
        if ((node as FrameworkElement)?.DataContext is not RowVm row) return;
        e.Handled = true;
        if (e.ClickCount > 1) return; // the second press of a double-click must not fire an armed confirm
        Click(row.Index, option.Index);
    }

    void OnGearMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Issue(OverlayCommand.Settings);
    }

    void OnBackMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Issue(OverlayCommand.B);
    }

    void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Issue(OverlayCommand.B);
    }

    void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // The wheel moves the selection (and the list follows it); it never scrolls the list by itself.
        e.Handled = true;
        Issue(e.Delta > 0 ? OverlayCommand.WheelUp : OverlayCommand.WheelDown);
    }

    void Issue(OverlayCommand command)
    {
        try { CommandIssued?.Invoke(command); }
        catch (Exception ex) { Log.Error($"Overlay command {command}", ex); }
    }

    void Click(int index, int? option)
    {
        try { RowClicked?.Invoke(index, option); }
        catch (Exception ex) { Log.Error("Overlay click", ex); }
    }
}

/// <summary>Picks the row template from <see cref="RowVm.Kind"/>.</summary>
internal sealed class RowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Action { get; set; }
    public DataTemplate? Segmented { get; set; }
    public DataTemplate? Choice { get; set; }
    public DataTemplate? Toggle { get; set; }
    public DataTemplate? Expander { get; set; }
    public DataTemplate? Info { get; set; }
    public DataTemplate? Slider { get; set; }
    public DataTemplate? Media { get; set; }
    public DataTemplate? Reorder { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (item as RowVm)?.Kind switch
        {
            ItemKind.Action => Action,
            ItemKind.Segmented => Segmented,
            ItemKind.Choice => Choice,
            ItemKind.Toggle => Toggle,
            ItemKind.Expander => Expander,
            ItemKind.Info => Info,
            ItemKind.Slider => Slider,
            ItemKind.Media => Media,
            ItemKind.Reorder => Reorder,
            _ => null,
        };
}
