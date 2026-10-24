using System.Globalization;
using System.Windows.Data;

namespace D2Translator.App.ViewModels;

public sealed class InverseBooleanConverter : IValueConverter
{
    public static InverseBooleanConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;
}
