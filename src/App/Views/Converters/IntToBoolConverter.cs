using System.Globalization;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace GameAssetExplorer.App.Views.Converters;

/// <summary>Returns true when the integer value is greater than zero. Used to enable buttons when a queue has items.</summary>
[ValueConversion(typeof(int), typeof(bool))]
public class IntToBoolConverter : IValueConverter
{
    public static readonly IntToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
