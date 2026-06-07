using System.Globalization;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace GameAssetExplorer.App.Views.Converters;

/// <summary>Returns true when the value is not null. Used to enable buttons when something is selected.</summary>
[ValueConversion(typeof(object), typeof(bool))]
public class NullToBoolConverter : IValueConverter
{
    public static readonly NullToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
