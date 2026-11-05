using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Games;
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

    OverlayViewModel? _vm;

    public OverlayWindow()
    {
        InitializeComponent();
        Resources[AccentKey] = new SolidColorBrush(OverlayViewModel.FallbackAccent);
        SourceInitialized += (_, _) => WindowEffects.ApplyChrome(this);
        Scroller.ScrollChanged += (_, _) => UpdateArtRange();
        ListArea.SizeChanged += (_, _) => QueueHighlight();
        // The Games hero keeps clear of the status corner, whose width follows the device name and battery.
        StatusCorner.SizeChanged += (_, _) => { if (_vm is not null) _vm.StatusWidth = StatusCorner.ActualWidth; };
        _contextFront = ContextA;
        _rowTintMask = TryFindResource("TintFade") as Brush;
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

    /// <summary>A source pill of the Games page was clicked (null: All).</summary>
    public event Action<GameSource?>? FilterClicked;

    /// <summary>The old page has slid out (<see cref="AnimatePage"/>): draw the new one now.</summary>
    public event Action? PageLeft;

    bool _backdropPressed; // the left button went down on the bare backdrop and the mouse is captured

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
        vm.StatusWidth = StatusCorner.ActualWidth;
        ShowContext(vm.Context);
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(OverlayViewModel.AccentBrush))
            Resources[AccentKey] = _vm.AccentBrush;
        else if (e.PropertyName is nameof(OverlayViewModel.Rows) or nameof(OverlayViewModel.SelectedTile))
        {
            // After the new rows are laid out, so their containers exist.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, BringSelectedIntoView);
            if (e.PropertyName == nameof(OverlayViewModel.Rows)) QueueHighlight();
        }
        else if (e.PropertyName == nameof(OverlayViewModel.Context))
            ShowContext(_vm.Context);
        else if (e.PropertyName == nameof(OverlayViewModel.Ambient))
            Backdrop.ShowTint(_vm.Ambient, MotionEnabled && IsVisible);
        else if (e.PropertyName is nameof(OverlayViewModel.Tiles) or nameof(OverlayViewModel.ShowTiles)
                 or nameof(OverlayViewModel.TileColumns))
        {
            if (e.PropertyName == nameof(OverlayViewModel.ShowTiles)) Backdrop.SetVertical(_vm.ShowTiles);
            // A new column count reflows the shelf: the same game stays selected (by id), so bring it back into view.
            if (e.PropertyName == nameof(OverlayViewModel.TileColumns))
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, BringSelectedIntoView);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateArtRange);
        }
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
            // With the room the lifted cover and its glow take above and below the cell.
            var lift = tileContainer.ActualHeight * 0.12;
            tileContainer.BringIntoView(new Rect(0, -lift, tileContainer.ActualWidth, tileContainer.ActualHeight + 2 * lift));
            return;
        }
        var row = _vm?.Rows.FirstOrDefault(r => r.IsSelected);
        var list = _vm is { ShowHomeTiles: true } ? HomeTileList : RowList;
        if (row is not null && list.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            container.BringIntoView();
    }

    // --- Motion ---------------------------------------------------------------
    //
    // Every animation is short (<= 220 ms), runs on Opacity / RenderTransform (the highlight also on its own size)
    // and never holds input back: each one first sets the property to its final value and animates from the value
    // shown towards it (FillBehavior.Stop), so the state is always current, a new change simply starts from where the
    // old one is, and clearing an animation can never leave anything half-faded. Without MotionEnabled every value
    // is set directly.

    static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    static readonly IEasingFunction EaseIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    const double OpenShift = 40;                                            // DIPs, rail and column on open / close
    static readonly TimeSpan OpenStagger = TimeSpan.FromMilliseconds(30);   // column after rail
    static readonly TimeSpan OpenLength = TimeSpan.FromMilliseconds(190);   // each; with the stagger 220 in all
    static readonly Duration BackdropIn = TimeSpan.FromMilliseconds(180);
    static readonly Duration CloseLength = TimeSpan.FromMilliseconds(120);
    const double PageShift = 24;                                            // DIPs, page change
    static readonly Duration PageOut = TimeSpan.FromMilliseconds(90);
    static readonly Duration PageIn = TimeSpan.FromMilliseconds(140);
    static readonly Duration HighlightGlide = TimeSpan.FromMilliseconds(140);
    internal static readonly Duration LiftLength = TimeSpan.FromMilliseconds(120);
    static readonly Duration ContextFade = TimeSpan.FromMilliseconds(150);

    // Open: built once and frozen (no allocation per open). The keyframes hold the start value through the stagger.
    static readonly AnimationTimeline BackdropFadeIn =
        Frozen(new DoubleAnimation(0, 1, BackdropIn) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    static readonly AnimationTimeline RailFadeIn = OpenFrames(0, 1, TimeSpan.Zero);
    static readonly AnimationTimeline RailSlideIn = OpenFrames(-OpenShift, 0, TimeSpan.Zero);
    static readonly AnimationTimeline ColumnFadeIn = OpenFrames(0, 1, OpenStagger);
    static readonly AnimationTimeline ColumnSlideIn = OpenFrames(-OpenShift, 0, OpenStagger);

    /// <summary>False when Windows "Animation effects" is off; read at each open (<see cref="BeginOpen"/>).</summary>
    public bool MotionEnabled { get; private set; }

    bool _closing;          // the close fade runs; the window hides when it ends
    int _closeVersion;      // a close fade that ends after a re-open does nothing
    int _leaveDirection;    // direction of the page change while the old page slides out, 0 otherwise
    int _pageVersion;
    bool _jumpHighlight = true;  // the next placement snaps (open, page change) instead of gliding
    bool _highlightQueued;
    bool _highlightShown;
    Rect _highlightTarget;
    readonly Brush? _rowTintMask;
    ContentControl _contextFront;  // the context host shown (the other one is fading out or empty)
    int _contextVersion;

    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    static AnimationTimeline OpenFrames(double from, double to, TimeSpan delay)
    {
        var frames = new DoubleAnimationUsingKeyFrames { Duration = delay + OpenLength, FillBehavior = FillBehavior.Stop };
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > TimeSpan.Zero) frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + OpenLength), EaseOut));
        return Frozen(frames);
    }

    /// <summary>
    /// Sets <paramref name="property"/> to <paramref name="to"/> (its base value, so the state is final at once) and,
    /// with a <paramref name="duration"/>, animates there from the value shown now. <paramref name="done"/> runs when
    /// that animation ends, or at once when nothing animates; callers check a version, since a replaced animation
    /// still completes.
    /// </summary>
    internal static void MoveTo<T>(T target, DependencyProperty property, double to, Duration? duration,
        IEasingFunction? ease = null, Action? done = null) where T : DependencyObject, IAnimatable
    {
        var from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, to);
        if (duration is not { } length || double.IsNaN(from) || Math.Abs(from - to) < 0.01)
        {
            done?.Invoke();
            return;
        }
        var clock = new DoubleAnimation(from, to, length) { EasingFunction = ease ?? EaseOut, FillBehavior = FillBehavior.Stop }
            .CreateClock();
        if (done is not null) clock.Completed += (_, _) => done();
        target.ApplyAnimationClock(property, clock);
    }

    /// <summary>
    /// Start of an open, before anything is drawn: reads <see cref="MotionEnabled"/>. When the close fade of the last
    /// open still runs, it is dropped and the frame is back at once; returns true then (the window never hid and
    /// still shows that open's backdrop).
    /// </summary>
    public bool BeginOpen()
    {
        MotionEnabled = SystemParameters.ClientAreaAnimation;
        var resumed = _closing && IsVisible;
        ResetMotion();
        return resumed;
    }

    /// <summary>
    /// Call right before Show: the backdrop fades in (180 ms), the rail and then the column slide in from the left
    /// and fade in (the stage fades with the column), 220 ms in all; not when <paramref name="resumed"/> (the frame
    /// is on screen already) or without motion. Nothing waits for it, so focus and input are not delayed.
    /// </summary>
    public void PrepareOpening(bool resumed)
    {
        _jumpHighlight = true;
        QueueHighlight();
        if (!MotionEnabled || resumed) return;
        Backdrop.BeginAnimation(OpacityProperty, BackdropFadeIn);
        Rail.BeginAnimation(OpacityProperty, RailFadeIn);
        RailSlide.BeginAnimation(TranslateTransform.XProperty, RailSlideIn);
        Column.BeginAnimation(OpacityProperty, ColumnFadeIn);
        ColumnSlide.BeginAnimation(TranslateTransform.XProperty, ColumnSlideIn);
        Stage.BeginAnimation(OpacityProperty, ColumnFadeIn);
    }

    /// <summary>
    /// Closes: the window stops taking the mouse at once (the controller already ignores commands and gave the
    /// focus back); the rail, column and stage go back out to the left and fade (120 ms), then the window hides.
    /// Without motion it hides at once. The backdrop stays as it is until the window hides: the window is opaque,
    /// so fading it would show the dark window background, not the game.
    /// </summary>
    public void BeginClose()
    {
        EndDrag();
        ReleaseBackdropPress();
        if (!MotionEnabled || !IsVisible)
        {
            HideNow();
            return;
        }
        FrameRoot.IsHitTestVisible = false;
        Backdrop.IsHitTestVisible = false;
        _leaveDirection = 0;
        _pageVersion++; // a page still sliding out is not drawn
        _closing = true;
        var version = ++_closeVersion;
        MoveTo(Rail, OpacityProperty, 0, CloseLength, EaseIn);
        MoveTo(RailSlide, TranslateTransform.XProperty, -OpenShift, CloseLength, EaseIn);
        MoveTo(ColumnSlide, TranslateTransform.XProperty, -OpenShift, CloseLength, EaseIn);
        MoveTo(Stage, OpacityProperty, 0, CloseLength, EaseIn);
        MoveTo(Column, OpacityProperty, 0, CloseLength, EaseIn, () =>
        {
            if (version != _closeVersion || !_closing) return; // re-opened (or closed for good) meanwhile
            try { HideNow(); }
            catch (Exception ex) { Log.Error("Overlay hide", ex); }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        // Destroyed at exit: a close fade or page slide that ends later must not touch the window.
        _closing = false;
        _closeVersion++;
        _pageVersion++;
        base.OnClosed(e);
    }

    /// <summary>Hides now and puts every animated value back to rest (closed: nothing animates).</summary>
    public void HideNow()
    {
        _closing = false;
        _closeVersion++;
        try { Hide(); }
        finally
        {
            ResetMotion();
            Backdrop.Clear();
        }
    }

    /// <summary>Every animation dropped, every animated value at rest, input back on.</summary>
    void ResetMotion()
    {
        _closing = false;
        _closeVersion++;
        _leaveDirection = 0;
        _pageVersion++;
        MoveTo(Backdrop, OpacityProperty, 1, null);
        MoveTo(Rail, OpacityProperty, 1, null);
        MoveTo(RailSlide, TranslateTransform.XProperty, 0, null);
        MoveTo(Column, OpacityProperty, 1, null);
        MoveTo(ColumnSlide, TranslateTransform.XProperty, 0, null);
        MoveTo(Stage, OpacityProperty, 1, null);
        MoveTo(PageHost, OpacityProperty, 1, null);
        MoveTo(PageSlide, TranslateTransform.XProperty, 0, null);
        FrameRoot.IsHitTestVisible = true;
        Backdrop.IsHitTestVisible = true;
        PageHost.IsHitTestVisible = true;
        _contextVersion++;
        var back = ReferenceEquals(_contextFront, ContextA) ? ContextB : ContextA;
        MoveTo(_contextFront, OpacityProperty, 1, null);
        MoveTo(back, OpacityProperty, 0, null);
        back.Content = null;
        _jumpHighlight = true;
        PlaceHighlight(_highlightTarget, snap: true);
    }

    /// <summary>
    /// A page change, before the new page is drawn. With motion and a direction (-1: LT or a rail icon above, +1: RT
    /// or below) the old page slides 24 DIP away from the side pressed and fades out (90 ms) and true is returned:
    /// draw the new page on <see cref="PageLeft"/> or at the next render, whichever comes first (input never waits for
    /// the slide). Otherwise false: draw it now. A change while the old page is still leaving returns false too, so
    /// pressing LT / RT quickly shows each page at once. The highlight jumps to the new page (no glide across pages).
    /// </summary>
    public bool AnimatePage(int direction)
    {
        _jumpHighlight = true;
        if (_leaveDirection != 0)
        {
            if (direction != 0) _leaveDirection = Math.Sign(direction);
            return false;
        }
        if (!MotionEnabled || direction == 0 || _closing || !IsVisible) return false;
        _leaveDirection = Math.Sign(direction);
        var version = ++_pageVersion;
        PageHost.IsHitTestVisible = false; // the old rows must not take a click meant for the new page
        MoveTo(PageSlide, TranslateTransform.XProperty, -PageShift * _leaveDirection, PageOut);
        MoveTo(PageHost, OpacityProperty, 0, PageOut, done: () =>
            // Posted: the end may come at once (nothing to fade), inside the render that started it.
            Dispatcher.BeginInvoke(() =>
            {
                if (version != _pageVersion || _leaveDirection == 0) return;
                try { PageLeft?.Invoke(); }
                catch (Exception ex) { Log.Error("Overlay page change", ex); }
            }));
        return true;
    }

    /// <summary>
    /// Call after every refresh of the page: when the old page was sliding out, the new one (drawn now) comes in from
    /// 24 DIP on the side of the command and fades in (140 ms).
    /// </summary>
    public void PageDrawn()
    {
        if (_leaveDirection == 0) return;
        var direction = _leaveDirection;
        _leaveDirection = 0;
        _pageVersion++;
        PageHost.IsHitTestVisible = true;
        _jumpHighlight = true;
        MoveTo(PageSlide, TranslateTransform.XProperty, PageShift * direction, null);
        MoveTo(PageHost, OpacityProperty, 0, null);
        MoveTo(PageSlide, TranslateTransform.XProperty, 0, PageIn);
        MoveTo(PageHost, OpacityProperty, 1, PageIn);
    }

    /// <summary>Places the highlight after the next layout (coalesced: one placement per burst of changes).</summary>
    void QueueHighlight()
    {
        if (_highlightQueued) return;
        _highlightQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _highlightQueued = false;
            UpdateHighlight();
        });
    }

    /// <summary>
    /// Moves the one highlight onto the selected row or Home tile (its <see cref="HighlightAnchor"/> border), gliding
    /// 140 ms from where it is; it snaps after an open or a page change, and hides when the selection has no anchor
    /// (the Games shelf, which lifts its covers instead).
    /// </summary>
    void UpdateHighlight()
    {
        try
        {
            if (SelectedAnchor() is not { } anchor)
            {
                Highlight.Visibility = Visibility.Collapsed;
                _highlightShown = false;
                return;
            }
            var bounds = anchor.TransformToVisual(HighlightLayer).TransformBounds(new Rect(anchor.RenderSize));
            StyleHighlight(HighlightAnchor.GetKind(anchor), (anchor as Border)?.CornerRadius ?? default);
            var snap = _jumpHighlight || !_highlightShown || !MotionEnabled;
            _jumpHighlight = false;
            Highlight.Visibility = Visibility.Visible;
            _highlightShown = true;
            if (!snap && bounds == _highlightTarget) return; // already there or on its way
            PlaceHighlight(bounds, snap);
        }
        catch (Exception ex)
        {
            Log.Error("Overlay highlight", ex);
        }
    }

    void PlaceHighlight(Rect bounds, bool snap)
    {
        _highlightTarget = bounds;
        if (bounds.IsEmpty) return;
        Duration? glide = snap ? null : HighlightGlide;
        MoveTo(HighlightMove, TranslateTransform.XProperty, bounds.X, glide);
        MoveTo(HighlightMove, TranslateTransform.YProperty, bounds.Y, glide);
        MoveTo(Highlight, WidthProperty, bounds.Width, glide);
        MoveTo(Highlight, HeightProperty, bounds.Height, glide);
    }

    /// <summary>The anchor border in the selected row's or tile's template, if it is laid out and shown.</summary>
    FrameworkElement? SelectedAnchor()
    {
        // No IsVisible test: while the window is hidden new containers are not laid out (no size, so no anchor), and
        // showing it places the highlight again.
        if (_vm is null) return null;
        RowVm? selected = null;
        foreach (var row in _vm.Rows)
            if (row.IsSelected) { selected = row; break; }
        if (selected is null) return null;
        var list = _vm.ShowHomeTiles ? HomeTileList : RowList;
        return list.ItemContainerGenerator.ContainerFromItem(selected) is DependencyObject container ? FindAnchor(container) : null;
    }

    static FrameworkElement? FindAnchor(DependencyObject node)
    {
        if (node is FrameworkElement element && HighlightAnchor.GetKind(element) != HighlightKind.None)
            return element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0 ? element : null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindAnchor(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    /// <summary>The highlight's look for the anchor: Row = fading tint and accent bar, Outline, Tile = tint, outline, glow.</summary>
    void StyleHighlight(HighlightKind kind, CornerRadius corner)
    {
        var row = kind == HighlightKind.Row;
        var tile = kind == HighlightKind.Tile;
        HighlightTint.Visibility = row || tile ? Visibility.Visible : Visibility.Collapsed;
        HighlightTint.OpacityMask = row ? _rowTintMask : null;
        HighlightTint.CornerRadius = corner;
        HighlightOutline.Visibility = kind is HighlightKind.Outline or HighlightKind.Tile ? Visibility.Visible : Visibility.Collapsed;
        HighlightOutline.CornerRadius = corner;
        HighlightGlow.Visibility = tile ? Visibility.Visible : Visibility.Collapsed;
        HighlightGlow.CornerRadius = new CornerRadius(corner.TopLeft + 3, corner.TopRight + 3, corner.BottomRight + 3, corner.BottomLeft + 3);
        HighlightBar.Visibility = row ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The stage card for <paramref name="context"/>. The same card with new values (same kind and title, e.g. the
    /// volume moving) updates in place; another card cross-fades with the old one (150 ms) on the second host, which
    /// takes the mouse from then on. A change during a fade starts from the opacities shown.
    /// </summary>
    void ShowContext(ContextVm? context)
    {
        try
        {
            var front = _contextFront;
            var back = ReferenceEquals(front, ContextA) ? ContextB : ContextA;
            var shown = front.Content as ContextVm;
            if (context is not null && shown is not null && shown.Kind == context.Kind && shown.Title == context.Title)
            {
                front.Content = context;
                return;
            }
            var version = ++_contextVersion;
            if (context is null || shown is null || !MotionEnabled || !IsVisible)
            {
                // No card before or after (the host collapses or appears), or no motion: at once, no second host.
                front.Content = context;
                back.Content = null;
                MoveTo(front, OpacityProperty, 1, null);
                MoveTo(back, OpacityProperty, 0, null);
                return;
            }
            back.Content = context;
            back.IsHitTestVisible = true;
            front.IsHitTestVisible = false;
            Panel.SetZIndex(back, 1);
            Panel.SetZIndex(front, 0);
            _contextFront = back;
            MoveTo(back, OpacityProperty, 1, ContextFade);
            MoveTo(front, OpacityProperty, 0, ContextFade, done: () =>
            {
                if (version == _contextVersion) front.Content = null; // gone: let its card (and art) go
            });
        }
        catch (Exception ex)
        {
            Log.Error("Overlay stage context", ex);
        }
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

    /// <summary>
    /// A press on the bare backdrop (never one that started on the frame or a card) captures the mouse; the release
    /// closes, if it is over the bare backdrop too. Closing on the press would hand the release to the game.
    /// </summary>
    void OnBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Backdrop.IsSurface(e.OriginalSource)) return;
        e.Handled = true;
        _backdropPressed = Backdrop.CaptureMouse();
    }

    void OnBackdropMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_backdropPressed) return;
        e.Handled = true;
        _backdropPressed = false;
        Backdrop.ReleaseMouseCapture();
        if (Backdrop.IsSurface(InputHitTest(e.GetPosition(this))))
            Raise(() => BackdropClicked?.Invoke(), "backdrop click");
    }

    void OnBackdropLostCapture(object sender, MouseEventArgs e) => _backdropPressed = false;

    void ReleaseBackdropPress()
    {
        if (!_backdropPressed) return;
        _backdropPressed = false;
        Backdrop.ReleaseMouseCapture();
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
        else QueueHighlight(); // laid out for real only now: place the highlight on the selected row
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

    /// <summary>A source pill of the Games page: shows that source.</summary>
    void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PillVm pill) return;
        e.Handled = true;
        if (e.ClickCount > 1) return;
        Raise(() => FilterClicked?.Invoke(pill.Source), "filter pill");
    }

    /// <summary>The hero's Play button: A, which launches the selected game.</summary>
    void OnPlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount > 1) return; // a double-click must not start the game twice
        Issue(OverlayCommand.A);
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

/// <summary>The stage context card for a <see cref="ContextVm"/>, by its kind.</summary>
internal sealed class ContextTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }
    public DataTemplate? Monitors { get; set; }
    public DataTemplate? Mixer { get; set; }
    public DataTemplate? Media { get; set; }
    public DataTemplate? Volume { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (item as ContextVm)?.Kind switch
        {
            ContextKind.Monitors => Monitors,
            ContextKind.Mixer => Mixer,
            ContextKind.Media => Media,
            ContextKind.Volume => Volume,
            ContextKind.Text => Text,
            _ => null,
        };
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

/// <summary>
/// The Home tiles: a 2-column grid in reading order. A wide row (<see cref="RowVm.IsWide"/>) takes a line of its
/// own; two other rows in a row share a line; one whose partner is not there (a wide row follows, or the list ends)
/// keeps its half on the left. Hidden items are not in the list, so the grid simply closes up. Both tiles of a line
/// get the taller one's height.
/// </summary>
internal sealed class TilePanel : Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double),
        typeof(TilePanel), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Space between the two columns and between lines (DIPs).</summary>
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    static bool IsWide(UIElement child) => (child as FrameworkElement)?.DataContext is RowVm { IsWide: true };

    /// <summary>First child index and child count of each line.</summary>
    List<(int First, int Count, bool Wide)> Lines()
    {
        var lines = new List<(int, int, bool)>();
        var children = InternalChildren;
        for (var i = 0; i < children.Count;)
        {
            if (IsWide(children[i])) { lines.Add((i, 1, true)); i++; continue; }
            var pair = i + 1 < children.Count && !IsWide(children[i + 1]);
            lines.Add((i, pair ? 2 : 1, false));
            i += pair ? 2 : 1;
        }
        return lines;
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width : 0;
        var half = Math.Max(0, (width - Gap) / 2);
        double height = 0, widest = 0;
        var lines = Lines();
        foreach (var (first, count, wide) in lines)
        {
            double line = 0, lineWidth = 0;
            for (var i = first; i < first + count; i++)
            {
                var child = InternalChildren[i];
                child.Measure(new Size(width == 0 ? double.PositiveInfinity : wide ? width : half, double.PositiveInfinity));
                line = Math.Max(line, child.DesiredSize.Height);
                lineWidth += child.DesiredSize.Width;
            }
            height += line;
            widest = Math.Max(widest, lineWidth + (count - 1) * Gap);
        }
        if (lines.Count > 1) height += (lines.Count - 1) * Gap;
        return new Size(width > 0 ? width : widest, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var half = Math.Max(0, (final.Width - Gap) / 2);
        double y = 0;
        foreach (var (first, count, wide) in Lines())
        {
            double line = 0;
            for (var i = first; i < first + count; i++) line = Math.Max(line, InternalChildren[i].DesiredSize.Height);
            for (var i = first; i < first + count; i++)
            {
                var x = i == first ? 0 : half + Gap;
                InternalChildren[i].Arrange(new Rect(x, y, wide ? final.Width : half, line));
            }
            y += line + Gap;
        }
        return final;
    }
}

/// <summary>How the selection highlight is drawn over an anchor (see <see cref="HighlightAnchor"/>).</summary>
internal enum HighlightKind
{
    None,
    /// <summary>Accent tint fading to the right, accent bar on the left (rows).</summary>
    Row,
    /// <summary>Accent outline only (the media card).</summary>
    Outline,
    /// <summary>Accent tint, outline and glow (Home tiles).</summary>
    Tile,
}

/// <summary>
/// Marks the element of a row or tile template whose bounds (and CornerRadius, for a Border) the one selection
/// highlight takes when that row or tile is selected.
/// </summary>
internal static class HighlightAnchor
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind",
        typeof(HighlightKind), typeof(HighlightAnchor), new PropertyMetadata(HighlightKind.None));

    public static HighlightKind GetKind(DependencyObject element) => (HighlightKind)element.GetValue(KindProperty);
    public static void SetKind(DependencyObject element, HighlightKind value) => element.SetValue(KindProperty, value);
}

/// <summary>
/// The lift of the selected Games cover: scale 1 to CoverLift (theme) in 120 ms and back, on the element's own
/// ScaleTransform (a frozen or shared one is replaced first, so no two covers ever share it). Set at once while the
/// element is not shown yet or without motion.
/// </summary>
internal static class CoverLift
{
    public static readonly DependencyProperty IsLiftedProperty = DependencyProperty.RegisterAttached("IsLifted",
        typeof(bool), typeof(CoverLift), new PropertyMetadata(false, OnIsLiftedChanged));

    public static bool GetIsLifted(DependencyObject element) => (bool)element.GetValue(IsLiftedProperty);
    public static void SetIsLifted(DependencyObject element, bool value) => element.SetValue(IsLiftedProperty, value);

    static void OnIsLiftedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        try
        {
            if (element.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
                element.RenderTransform = scale = new ScaleTransform();
            var to = (bool)e.NewValue ? element.TryFindResource("CoverLift") as double? ?? 1.1 : 1.0;
            Duration? length = element.IsVisible && Window.GetWindow(element) is OverlayWindow { MotionEnabled: true }
                ? OverlayWindow.LiftLength
                : null;
            OverlayWindow.MoveTo(scale, ScaleTransform.ScaleXProperty, to, length);
            OverlayWindow.MoveTo(scale, ScaleTransform.ScaleYProperty, to, length);
        }
        catch (Exception ex)
        {
            Log.Error("Overlay cover lift", ex);
        }
    }
}
