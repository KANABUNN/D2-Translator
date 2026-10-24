using System.Windows;
using System.Windows.Input;
using D2Translator.App.ViewModels;

namespace D2Translator.App.Views;

public partial class ReplyWindow : Window
{
    private readonly ReplyViewModel _viewModel;

    public ReplyWindow(ReplyViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => DraftBox.Focus();
    }

    /// <summary>
    /// Ctrl+Enter で英訳する。IME の変換中は Enter が ImeProcessed として届くため、変換の確定と取り違えない（仕様書 10.1）。
    /// </summary>
    private void DraftBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (_viewModel.TranslateCommand.CanExecute(null))
            {
                _viewModel.TranslateCommand.Execute(null);
            }
        }
    }
}
