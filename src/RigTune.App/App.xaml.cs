using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using RigTune.App.ViewModels;
using RigTune.App.Views;
using RigTune.Core.Hardware;
using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Runners;
using RigTune.Core.Services;
using RigTune.Core.Storage;

namespace RigTune.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public static IServiceProvider Services =>
        ((App)Current)._services ?? throw new InvalidOperationException("Services are not initialized yet.");

    /// <summary>True when launched by the Start-with-Windows task (<c>--minimized</c>).</summary>
    public static bool StartMinimized { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        StartMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        _services = ConfigureServices();
        _services.GetRequiredService<RigTunePaths>().EnsureCreated();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        if (StartMinimized)
        {
            window.WindowState = WindowState.Minimized;
        }

        window.Show();

        try
        {
            await _services.GetRequiredService<AuditLogService>()
                .LogAsync(AuditAction.Info, "RigTune started");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Non-fatal: the UI still works; failures surface where persistence is required.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core: persistence
        services.AddSingleton(_ => new RigTunePaths());
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AuditLogService>(sp => new AuditLogService(sp.GetRequiredService<RigTunePaths>()));

        // Core: platform runners
        services.AddSingleton<IRegistryAccess, RegistryRunner>();
        services.AddSingleton<IServiceAccess, ServiceRunner>();
        services.AddSingleton<IPowerAccess, PowerRunner>();
        services.AddSingleton<INvApiDrsAccess, NvApiRunner>();
        services.AddSingleton<IRestorePointBackend, RestorePointRunner>();

        // Core: services
        services.AddSingleton<SnapshotService>(sp => new SnapshotService(
            sp.GetRequiredService<RigTunePaths>(),
            sp.GetRequiredService<IRegistryAccess>(),
            sp.GetRequiredService<IServiceAccess>(),
            sp.GetRequiredService<IPowerAccess>(),
            sp.GetRequiredService<INvApiDrsAccess>(),
            sp.GetRequiredService<AuditLogService>()));
        services.AddSingleton<RestorePointService>();
        services.AddSingleton<HostProfileDetector>();

        // UI
        services.AddSingleton<MainWindow>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<DashboardView>();
        services.AddSingleton<TweaksView>();
        services.AddSingleton<SystemCareView>();
        services.AddSingleton<AppOptimizerView>();
        services.AddSingleton<GameModeView>();
        services.AddSingleton<DiskAnalyzerView>();
        services.AddSingleton<HistoryView>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        System.Windows.MessageBox.Show(e.Exception.Message, "RigTune — unexpected error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
