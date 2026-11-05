using System.Globalization;
using System.Windows.Data;
using OverlayUtils.Core.Localization;

namespace OverlayUtils.App.Overlay;

/// <summary>Text in upper case in the UI language (the eyebrow line).</summary>
internal sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string)?.ToUpper(Loc.Culture) ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>A length times the factor in the parameter (invariant number), e.g. a cover's height from its width.</summary>
internal sealed class ScaleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var factor = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 1;
        return value is double d && double.IsFinite(d) ? d * factor : 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
