using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskHub.App;

/// <summary>null / empty string / false → Collapsed; anything else → Visible. ConverterParameter "invert" flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            _ => true,
        };
        if (parameter is "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// "#RRGGBB" → a frozen brush. Null or invalid → <see cref="DependencyProperty.UnsetValue"/>, so the
/// property keeps its normal (theme) value. ConverterParameter "soft" gives a 20 % tint for backgrounds.
/// </summary>
public sealed class ColorBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || hex.Length == 0) return DependencyProperty.UnsetValue;
        var soft = parameter is "soft";
        var key = soft ? hex + "|soft" : hex;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            Color color;
            try
            {
                color = (Color)ColorConverter.ConvertFromString(hex);
            }
            catch (Exception e) when (e is FormatException or NotSupportedException)
            {
                return DependencyProperty.UnsetValue;
            }
            if (soft) color.A = 0x33;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Cache[key] = brush;
            return brush;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
