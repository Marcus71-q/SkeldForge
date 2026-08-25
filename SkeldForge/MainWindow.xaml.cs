using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using SkeldForge.ViewModels;

namespace SkeldForge;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private bool _placementApplied;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsInitializing)
            ApplyWindowPlacement(vm);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Settings) && sender is MainViewModel vm)
            Dispatcher.BeginInvoke(() => ApplyWindowPlacement(vm));
    }

    private void ApplyWindowPlacement(MainViewModel vm)
    {
        if (_placementApplied) return;
        _placementApplied = true;
        Width = Math.Max(MinWidth, vm.Settings.WindowWidth);
        Height = Math.Max(MinHeight, vm.Settings.WindowHeight);
        if (vm.Settings.WindowLeft is double left && vm.Settings.WindowTop is double top &&
            double.IsFinite(left) && double.IsFinite(top) && IsPositionVisible(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = SystemParameters.WorkArea.Left + Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
            Top = SystemParameters.WorkArea.Top + Math.Max(0, (SystemParameters.WorkArea.Height - Height) / 2);
        }
        if (vm.Settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        vm.Settings.WindowLeft = bounds.Left;
        vm.Settings.WindowTop = bounds.Top;
        vm.Settings.WindowWidth = bounds.Width;
        vm.Settings.WindowHeight = bounds.Height;
        vm.Settings.WindowMaximized = WindowState == WindowState.Maximized;
        try { await vm.Root.SaveSettingsAsync(vm.Settings); } catch { }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void UpdateMaximizeGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }
    private static bool IsPositionVisible(double left, double top) =>
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80 &&
        left + 80 > SystemParameters.VirtualScreenLeft && top + 40 > SystemParameters.VirtualScreenTop;
}
