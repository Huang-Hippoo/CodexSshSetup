using System.ComponentModel;
using System.Windows;
using CodexSshSetup.App.Services;
using CodexSshSetup.App.ViewModels;

namespace CodexSshSetup.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private TrayIconService? _tray;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        _tray = new TrayIconService(
            ShowWindow,
            () => _viewModel.ConnectCommand.Execute(null),
            () => _viewModel.DisconnectCommand.Execute(null),
            () => Dispatcher.BeginInvoke(ExitAsync));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        var choice = System.Windows.MessageBox.Show(
            "选择“是”将隐藏到托盘并保持隧道；选择“否”将停止隧道并退出。",
            "关闭 Codex SSH Setup",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            Hide();
        }
        else if (choice == MessageBoxResult.No)
        {
            e.Cancel = true;
            _ = ExitAsync();
        }
        else e.Cancel = true;
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task ExitAsync()
    {
        if (_allowClose) return;
        _allowClose = true;
        await _viewModel.StopTunnelAsync();
        await _viewModel.DisposeAsync();
        _tray?.Dispose();
        Close();
    }
}
