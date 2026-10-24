using System.ComponentModel;
using System.Windows;
using D2Translator.App.ViewModels;

namespace D2Translator.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _shutdownCompleted;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_shutdownCompleted || e.Cancel)
        {
            return;
        }

        // 取得を止めてから閉じ直す
        e.Cancel = true;
        IsEnabled = false;
        await _viewModel.ShutdownAsync();
        _shutdownCompleted = true;
        Close();
    }
}
