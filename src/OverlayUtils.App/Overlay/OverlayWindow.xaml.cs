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
/// The full-screen overlay. It only draws <see cref="OverlayViewModel"/> (and the backdrop it is handed) and turns
/// keyboard and mouse input into <see cref="CommandIssued"/> / <see cref="RowClicked"/> and the other events; the
/// controller owns the navigator, size, placement and the backdrop capture.
/// </summary>
internal sealed partial class OverlayWindow : Window
{
    const string AccentKey = "Accent";
    static readonly Duration OpenDuration = TimeSpan.FromMilliseconds(150);
    const double OpenSlide = 24; // DIPs

    OverlayViewModel? _vm;

    public OverlayWindow()
    {
        InitializeComponent();
        Resources[AccentKey] = new SolidColorBrush(OverlayViewModel.FallbackAccent);
        SourceInitialized += (_, _) => WindowEffects.ApplyChrome(this);
        Scroller.ScrollChanged += (_, _) => UpdateArtRange();
    }

    public event Action<OverlayCommand>? CommandIssued;

    /// <summary>Row index (navigator index) and the clicked option of a segmented row, if any.</summary>
    public event Action<int, int?>? RowClicked;

    /// <summary>The mouse really moved onto a row (navigator index).</summary>
    public event Action<int>? RowHovered;

    /// <summary>A rail icon was clicked (<see cref="RailVm.VisibleIndex"/>, index into the visible tabs).</summary>
    public event Action<int>? RailClicked;

    /// <summary>The Settings gear of the rail was clicked.</summary>
    public event Action? SettingsClicked;

    /// <summary>The bare backdrop was clicked (not the rail, the column or a stage card).</summary>
    public event Action? BackdropClicked;

    /// <summary>A picker arrow (row, -1/+1) was clicked, or the wheel turned over the volume bar.</summary>
    public event Action<int, int>? StepClicked;

    /// <summary>The volume bar was clicked or dragged (row, fraction of its width).</summary>
    public event Action<int, double>? LevelClicked;

    /// <summary>A media button was clicked (row, -1 previous / 0 play-pause / +1 next).</summary>
    public event Action<int, int>? MediaClicked;

    /// <summary>The mouse really moved onto a game tile (game id).</summary>
    public event Action<string>? TileHovered;

    /// <summary>A game tile was clicked (game id: the list may have changed since it was drawn).</summary>
    public event Action<string>? TileClicked;

    // Last cursor position seen, in screen pixels: hover acts only when the mouse really moves, not when the
    // overlay opens under a still cursor or the list scrolls under it.
    Point? _lastPointer;

    // Volume bar drag. Every render rebuilds the rows, so the drag is tracked by row index and the window
    // holds the capture; the window-to-bar transform taken at the press stays valid (same layout).
    int? _dragRow;
    GeneralTransform? _toBar;
    double _barWidth;
    int _dragPercent = -1;

    /// <summary>Called on open: the cursor where it is now does not count as a move.</summary>
    public void ResetPointer()
    {
        EndDrag();
        var p = System.Windows.Forms.Cursor.Position;
        _lastPointer = new Point(p.X, p.Y);
    }

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
        else if (e.PropertyName is nameof(OverlayViewModel.Rows) or nameof(OverlayViewModel.SelectedTile))
            // After the new rows are laid out, so their containers exist.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, BringSelectedIntoView);
        else if (e.PropertyName is nameof(OverlayViewModel.Tiles) or nameof(OverlayViewModel.ShowTiles)
                 or nameof(OverlayViewModel.TileColumns))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateArtRange);
    }

    /// <summary>
    /// Call right before Show: the frame (rail, column, stage) slides in from the left and fades in, unless Windows
    /// "Animation effects" is off. Only the frame moves (the window and the backdrop stay put) and nothing waits for
    /// it, so focus is not delayed. Closing is never animated.
    /// </summary>
    public void PrepareOpening()
    {
        FrameRoot.BeginAnimation(OpacityProperty, null);
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        if (!SystemParameters.ClientAreaAnimation) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var from = -OpenSlide;
        FrameRoot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, OpenDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        Slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(from, 0, OpenDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Asks for the art of the grid rows in view, plus one row above and below.</summary>
    void UpdateArtRange()
    {
        try
        {
            if (_vm is not { ShowTiles: true } vm || vm.Tiles.Count == 0 || !IsVisible) return;
            if (TileList.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement first || first.ActualHeight <= 0) return;
            var rowHeight = first.ActualHeight;
            var top = TileList.TranslatePoint(new Point(0, 0), Scroller).Y; // negative once scrolled past the grid top
            var fromRow = Math.Max(0, (int)Math.Floor(-top / rowHeight) - 1);
            var toRow = (int)Math.Ceiling((Scroller.ViewportHeight - top) / rowHeight) + 1;
            var columns = vm.TileColumns;
            vm.ShowArt(fromRow * columns, Math.Min(vm.Tiles.Count - 1, (toRow + 1) * columns - 1));
        }
        catch (Exception ex)
        {
            Log.Error("Overlay tile art", ex);
        }
    }

    void BringSelectedIntoView()
    {
        if (_vm is { ShowTiles: true, SelectedTile: >= 0 and var tile }
            && TileList.ItemContainerGenerator.ContainerFromIndex(tile) is FrameworkElement tileContainer)
        {
            tileContainer.BringIntoView();
            return;
        }
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
        if (e.ClickCount > 1) return; // a double-click would open Settings and go straight back
        Raise(() => SettingsClicked?.Invoke(), "gear click");
    }

    void OnRailMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RailVm item) return;
        e.Handled = true;
        if (e.ClickCount > 1) return;
        Raise(() => RailClicked?.Invoke(item.VisibleIndex), "rail click");
    }

    /// <summary>Only a press on the bare backdrop closes: never one that started on the frame or a card.</summary>
    void OnBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Backdrop.IsSurface(e.OriginalSource)) return;
        e.Handled = true;
        Raise(() => BackdropClicked?.Invoke(), "backdrop click");
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
        // Over the volume bar it changes the volume instead.
        e.Handled = true;
        if (Ancestor(e.OriginalSource as DependencyObject, n => n is FrameworkElement { Tag: "VolumeBar" }) is not null
            && RowOf(e.OriginalSource as DependencyObject) is { } row)
        {
            Raise(() => StepClicked?.Invoke(row.Index, e.Delta > 0 ? 1 : -1), "wheel on the volume bar");
            return;
        }
        Issue(e.Delta > 0 ? OverlayCommand.WheelUp : OverlayCommand.WheelDown);
    }

    void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is not null)
        {
            // Dragging the volume bar keeps its row; no hover meanwhile.
            if (e.LeftButton == MouseButtonState.Pressed) DragTo(e.GetPosition(this));
            else EndDrag();
            return;
        }
        var p = PointToScreen(e.GetPosition(this));
        if (_lastPointer == p) return;
        _lastPointer = p;
        if (TileOf(e.OriginalSource as DependencyObject) is { } tile)
        {
            Raise(() => TileHovered?.Invoke(tile.Game.Id), "tile hover");
            return;
        }
        if (RowOf(e.OriginalSource as DependencyObject) is { } row)
            Raise(() => RowHovered?.Invoke(row.Index), "hover");
    }

    void OnStepMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender as DependencyObject) is not { } row || !int.TryParse((sender as FrameworkElement)?.Tag as string, out var direction)) return;
        e.Handled = true;
        if (e.ClickCount > 1) return;
        Raise(() => StepClicked?.Invoke(row.Index, direction), "picker arrow");
    }

    void OnMediaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender as DependencyObject) is not { } row || !int.TryParse((sender as FrameworkElement)?.Tag as string, out var button)) return;
        e.Handled = true;
        if (e.ClickCount > 1) return;
        Raise(() => MediaClicked?.Invoke(row.Index, button), "media button");
    }

    void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement bar || RowOf(bar) is not { } row) return;
        e.Handled = true;
        if (e.ClickCount > 1) return;
        _dragRow = row.Index;
        _toBar = TransformToDescendant(bar);
        _barWidth = bar.ActualWidth;
        _dragPercent = -1;
        CaptureMouse();
        DragTo(e.GetPosition(this));
    }

    void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragRow is null) return;
        e.Handled = true;
        EndDrag();
    }

    void OnLostMouseCapture(object sender, MouseEventArgs e) => ClearDrag();

    void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) EndDrag();
    }

    void DragTo(Point inWindow)
    {
        if (_dragRow is not { } index || _toBar is null) return;
        // The fill runs between the knob's half-widths (the 7 DIP margin on each side of the track).
        const double inset = 7;
        var x = _toBar.Transform(inWindow).X;
        var fraction = Math.Clamp((x - inset) / Math.Max(_barWidth - 2 * inset, 1), 0, 1);
        // One update per whole percent: each one posts to the audio worker and redraws the rows.
        var percent = (int)Math.Round(fraction * 100);
        if (percent == _dragPercent) return;
        _dragPercent = percent;
        Raise(() => LevelClicked?.Invoke(index, fraction), "volume bar");
    }

    void EndDrag()
    {
        var dragging = _dragRow is not null;
        ClearDrag();
        if (dragging && IsMouseCaptured) ReleaseMouseCapture();
    }

    void ClearDrag()
    {
        _dragRow = null;
        _toBar = null;
        _dragPercent = -1;
    }

    void OnFooterMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FooterVm { Command: { } command }) return;
        e.Handled = true;
        if (e.ClickCount > 1) return;
        Issue(command);
    }

    void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TileVm tile) return;
        e.Handled = true;
        if (e.ClickCount > 1) return; // a double-click must not start the game twice
        Raise(() => TileClicked?.Invoke(tile.Game.Id), "tile click");
    }

    /// <summary>The filter pill of the Games grid: same as Y / Delete.</summary>
    void OnFilterMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount > 1) return;
        Issue(OverlayCommand.Remove);
    }

    static TileVm? TileOf(DependencyObject? node) =>
        (Ancestor(node, n => (n as FrameworkElement)?.DataContext is TileVm) as FrameworkElement)?.DataContext as TileVm;

    static RowVm? RowOf(DependencyObject? node) =>
        (Ancestor(node, n => (n as FrameworkElement)?.DataContext is RowVm) as FrameworkElement)?.DataContext as RowVm;

    static DependencyObject? Ancestor(DependencyObject? node, Func<DependencyObject, bool> match)
    {
        while (node is not null && !match(node))
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node;
    }

    static void Raise(Action raise, string what)
    {
        try { raise(); }
        catch (Exception ex) { Log.Error($"Overlay {what}", ex); }
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
    public DataTemplate? Grid { get; set; }

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
            ItemKind.Grid => Grid,
            _ => null,
        };
}
