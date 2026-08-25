using System.Windows;
using System.Windows.Threading;
using SkeldForge.Services;
using SkeldForge.ViewModels;
using SkeldForge.ViewModels.Infrastructure;

namespace SkeldForge;

public partial class App : Application
{
    private AppCompositionRoot? _compositionRoot;

    static App()
    {
        // WPF's font cache still reads the legacy process-level windir variable.
        // Some stripped launch environments expose only SystemRoot; normalize it before any Window is created.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(systemRoot))
                Environment.SetEnvironmentVariable("windir", systemRoot, EnvironmentVariableTarget.Process);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _compositionRoot = new AppCompositionRoot();

        var viewModel = new MainViewModel(_compositionRoot);
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
        _ = viewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _compositionRoot?.Dispose();
        base.OnExit(e);
    }

    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_compositionRoot is null)
            return;
        try { await _compositionRoot.Logger.ErrorAsync("Unhandled UI exception.", e.Exception); } catch { }
        if (MainWindow is { IsLoaded: true })
        {
            await _compositionRoot.Dialogs.ShowErrorAsync(
                LocalizationService.Current["Dialog.ErrorTitle"],
                LocalizationService.Current["Error.Generic"],
                e.Exception.Message);
        }
        else
        {
            Shutdown(-1);
        }
    }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }
}
