using System.Globalization;
using System.Windows.Data;
using D2Translator.Domain.Settings;

namespace D2Translator.App.ViewModels;

/// <summary>文体の表示名。機能上の違いを用例で説明する（仕様書 12 send.style）。</summary>
public sealed class SendStyleLabelConverter : IValueConverter
{
    public static string Label(SendStyle style) => style switch
    {
        SendStyle.Short => "短く（例: need 2 more）",
        SendStyle.Polite => "丁寧（例: Could we get two more?）",
        _ => "一般的なゲームチャット（例: we need 2 more）",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SendStyle style ? Label(style) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
