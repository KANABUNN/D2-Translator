using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using D2Translator.App.Native;
using D2Translator.App.ViewModels;

namespace D2Translator.App.Views;

/// <summary>
/// UI03 オーバーレイ。ゲームとは別プロセスのトップレベルウィンドウで、ゲームの描画には参加しない（仕様書 4.1）。
/// 表示専用の間はフォーカスを取らず、クリックをゲームへ通す。配置編集の間だけ入力を受ける。
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly MainViewModel _viewModel;
    private nint _hwnd;

    public OverlayWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        // 幅は設定値に合わせ、高さは内容に合わせる（仕様書 11.3）
        Width = viewModel.OverlayWidth;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.OverlayWidth))
            {
                Width = _viewModel.OverlayWidth;
            }
        };
        PlaceInitially();
        _viewModel.ReportOverlayPosition(Left, Top);
        LocationChanged += (_, _) => _viewModel.ReportOverlayPosition(Left, Top);
        MouseLeftButtonDown += (_, e) =>
        {
            if (_viewModel.IsPlacementEditing && e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _viewModel.IsPlacementEditing)
            {
                _viewModel.EndPlacementEdit(Left, Top);
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetExtendedStyle(_hwnd, NativeMethods.WS_EX_TOOLWINDOW, 0);
        ApplyClickThrough(editing: _viewModel.IsPlacementEditing);
    }

    /// <summary>
    /// 自アプリの表示を画面取得から除く。補助的な対策で、効果は OS と取得方式で異なり、秘匿の保証として扱わない（仕様書 4.3）。
    /// 透過ウィンドウは最初の描画が済んでから指定する。
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        bool excluded = NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        _viewModel.SetCaptureExclusion(excluded, excluded ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
    }

    /// <summary>配置編集の開始と終了。編集中だけクリックを受け、Esc を受け取れるよう前面にする。</summary>
    public void SetEditing(bool editing)
    {
        ApplyClickThrough(editing);
        if (editing)
        {
            Activate();
            Focus();
        }
    }

    private void ApplyClickThrough(bool editing)
    {
        if (_hwnd == 0)
        {
            return;
        }

        nint passThrough = NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetExtendedStyle(_hwnd, editing ? 0 : passThrough, editing ? passThrough : 0);
        Focusable = editing;
    }

    /// <summary>保存した位置が今の画面の外なら、可視領域へ戻す（仕様書 4.2 モニター切断、DPI 変更）。</summary>
    private void PlaceInitially()
    {
        double left = _viewModel.OverlayLeft ?? SystemParameters.WorkArea.Left + 40;
        double top = _viewModel.OverlayTop ?? SystemParameters.WorkArea.Top + 120;
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!virtualScreen.IntersectsWith(new Rect(left, top, Math.Max(Width, 100), 60)))
        {
            left = SystemParameters.WorkArea.Left + 40;
            top = SystemParameters.WorkArea.Top + 120;
        }

        Left = left;
        Top = top;
    }
}
