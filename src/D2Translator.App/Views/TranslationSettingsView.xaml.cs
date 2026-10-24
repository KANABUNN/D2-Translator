using System.Windows;
using System.Windows.Controls;
using D2Translator.App.ViewModels;

namespace D2Translator.App.Views;

public partial class TranslationSettingsView : UserControl
{
    public TranslationSettingsView()
    {
        InitializeComponent();
    }

    // PasswordBox は値をバインドできないため、ここで受け取ってすぐ消す
    private async void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        string key = KeyBox.Password;
        KeyBox.Clear();
        if (DataContext is TranslationSettingsViewModel viewModel)
        {
            await viewModel.SaveKeyAsync(key);
        }
    }
}
