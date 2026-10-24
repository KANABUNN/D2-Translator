using System.Windows;
using D2Translator.App.ViewModels;

namespace D2Translator.App.Views;

public partial class TranslationSettingsWindow : Window
{
    private readonly TranslationSettingsViewModel _viewModel;

    public TranslationSettingsWindow(TranslationSettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    // PasswordBox は値をバインドできないため、ここで受け取ってすぐ消す
    private async void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        string key = KeyBox.Password;
        KeyBox.Clear();
        await _viewModel.SaveKeyAsync(key);
    }
}
