using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    OverlayViewModel? _vm;

    public OverlayWindow()
    {
        InitializeComponent();
        Resources[AccentKey] = new SolidColorBrush(OverlayViewModel.FallbackAccent);
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

    void BringSelectedIntoView()
    {
        var row = _vm?.Rows.FirstOrDefault(r => r.IsSelected);
        if (row is not null && RowList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            container.BringIntoView();
    }

    // --- Keyboard -------------------------------------------------------------

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (item as RowVm)?.Kind switch
        {
            ItemKind.Action => Action,
            ItemKind.Segmented => Segmented,
            ItemKind.Choice => Choice,
            ItemKind.Toggle => Toggle,
            ItemKind.Expander => Expander,
            ItemKind.Info => Info,
            _ => null,
        };
}
