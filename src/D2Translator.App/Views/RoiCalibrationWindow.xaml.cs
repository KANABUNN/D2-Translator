using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using D2Translator.App.ViewModels;
using D2Translator.Domain.Geometry;

namespace D2Translator.App.Views;

public partial class RoiCalibrationWindow : Window
{
    private readonly RoiCalibrationViewModel _viewModel;
    private Point? _dragStart;

    public RoiCalibrationWindow(RoiCalibrationViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RoiCalibrationViewModel.SelectionPixels) or nameof(RoiCalibrationViewModel.FrameWidth))
        {
            DrawSelection(_viewModel.SelectionPixels);
        }
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Surface);
        Surface.CaptureMouse();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point current = e.GetPosition(Surface);
        DrawBox(Math.Min(start.X, current.X), Math.Min(start.Y, current.Y),
            Math.Abs(current.X - start.X), Math.Abs(current.Y - start.Y));
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not Point start)
        {
            return;
        }

        Surface.ReleaseMouseCapture();
        _dragStart = null;
        Point end = e.GetPosition(Surface);
        _viewModel.SetSelection(start.X, start.Y, end.X, end.Y);
        DrawSelection(_viewModel.SelectionPixels);
    }

    private void DrawSelection(PixelRect? rect)
    {
        if (rect is not PixelRect r)
        {
            SelectionBox.Visibility = Visibility.Collapsed;
            return;
        }

        DrawBox(r.X, r.Y, r.Width, r.Height);
    }

    private void DrawBox(double x, double y, double width, double height)
    {
        // Viewbox で縮小されても見える太さにする
        SelectionBox.StrokeThickness = Math.Max(2, Surface.ActualWidth / 400);
        Canvas.SetLeft(SelectionBox, x);
        Canvas.SetTop(SelectionBox, y);
        SelectionBox.Width = width;
        SelectionBox.Height = height;
        SelectionBox.Visibility = Visibility.Visible;
    }
}
