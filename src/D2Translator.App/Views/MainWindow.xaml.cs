using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2Translator.App.Services;
using D2Translator.App.ViewModels;

namespace D2Translator.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppSettingsService _settings;
    private readonly HotkeyStatusStore _hotkeyStatus;
    private OverlayWindow? _overlay;
    private HotkeyService? _hotkeys;
    private ForegroundWatcher? _foreground;
    private bool _shutdownCompleted;

    public MainWindow(MainViewModel viewModel, AppSettingsService settings, HotkeyStatusStore hotkeyStatus)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _hotkeyStatus = hotkeyStatus;
        DataContext = viewModel;
        viewModel.Messages.CollectionChanged += OnMessagesChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hotkeys = new HotkeyService(this);
        _hotkeys.Pressed += (_, action) => _viewModel.OnHotkey(action);
        RegisterHotkeys();
        _settings.Changed += (_, _) => RegisterHotkeys();

        _foreground = new ForegroundWatcher();
        _foreground.Changed += (_, info) => _viewModel.OnForegroundChanged(info);
        SyncOverlay();
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null)
        {
            return;
        }

        _viewModel.SetHotkeyStatus(_hotkeys.Register(_settings.Current.Hotkeys));
        _hotkeyStatus.Text = _viewModel.HotkeyStatusText;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.OverlayVisible) or nameof(MainViewModel.IsPlacementEditing))
        {
            SyncOverlay();
        }
    }

    /// <summary>オーバーレイの表示と配置編集の状態を ViewModel に合わせる。</summary>
    private void SyncOverlay()
    {
        if (!IsInitialized || _foreground is null)
        {
            return;
        }

        if (!_viewModel.OverlayVisible)
        {
            if (_overlay is { IsVisible: true })
            {
                _overlay.SetEditing(false);
                _overlay.Hide();
            }

            return;
        }

        try
        {
            _overlay ??= new OverlayWindow(_viewModel);
            if (!_overlay.IsVisible)
            {
                _overlay.Show();
            }

            _overlay.SetEditing(_viewModel.IsPlacementEditing);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _viewModel.OnOverlayFailed(ex.GetType().Name);
        }
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
        _overlay?.Close();
        _hotkeys?.Dispose();
        _foreground?.Dispose();
        Close();
    }
}
