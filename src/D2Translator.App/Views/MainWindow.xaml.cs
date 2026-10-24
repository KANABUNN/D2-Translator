using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        viewModel.Messages.CollectionChanged += OnMessagesChanged;
    }

    /// <summary>
    /// 利用者が最下部を見ている時だけ新着へ追従する。上へスクロールしている間は位置を固定する（仕様書 11.2）。
    /// </summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || FindScrollViewer(MessageList) is not ScrollViewer viewer)
        {
            return;
        }

        bool atBottom = viewer.VerticalOffset >= viewer.ScrollableHeight - 2;
        if (atBottom)
        {
            Dispatcher.InvokeAsync(viewer.ScrollToEnd);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is ScrollViewer nested)
            {
                return nested;
            }
        }

        return null;
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
