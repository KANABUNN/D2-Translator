using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace D2Translator.App.Presentation;

/// <summary>
/// 通知や状態の文が変わったら、スクリーンリーダーに読み上げを促す（仕様書 17.2、T19）。
/// WPF は LiveSetting を指定しただけでは変化を通知しないため、文の変化で LiveRegionChanged を送る。
/// </summary>
public static class LiveRegion
{
    public static readonly DependencyProperty SettingProperty = DependencyProperty.RegisterAttached(
        "Setting", typeof(AutomationLiveSetting), typeof(LiveRegion),
        new PropertyMetadata(AutomationLiveSetting.Off, OnSettingChanged));

    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    public static AutomationLiveSetting GetSetting(DependencyObject element) => (AutomationLiveSetting)element.GetValue(SettingProperty);

    public static void SetSetting(DependencyObject element, AutomationLiveSetting value) => element.SetValue(SettingProperty, value);

    private static void OnSettingChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock text)
        {
            return;
        }

        AutomationProperties.SetLiveSetting(text, (AutomationLiveSetting)e.NewValue);
        TextDescriptor.RemoveValueChanged(text, OnTextChanged);
        if ((AutomationLiveSetting)e.NewValue != AutomationLiveSetting.Off)
        {
            TextDescriptor.AddValueChanged(text, OnTextChanged);
        }
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock { Text.Length: > 0 } text &&
            (UIElementAutomationPeer.FromElement(text) ?? UIElementAutomationPeer.CreatePeerForElement(text)) is AutomationPeer peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
