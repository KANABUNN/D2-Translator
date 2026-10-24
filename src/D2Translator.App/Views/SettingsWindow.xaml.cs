using System.Windows;
using D2Translator.App.ViewModels;

namespace D2Translator.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
