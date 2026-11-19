using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using RigTune.App.Services;
using RigTune.App.Tray;
using RigTune.App.ViewModels;
using RigTune.App.Views;
using RigTune.Core.Hardware;
using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Runners;
using RigTune.Core.Runners.Handlers;
using RigTune.Core.Services;
using RigTune.Core.Services.AppOptimizer;
using RigTune.Core.Services.AppxWinget;
using RigTune.Core.Services.DiskAnalyzer;
using RigTune.Core.Services.GameMode;
using RigTune.Core.Services.SystemCare;
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
        IReadOnlyList<string> catalogErrors = TweakCatalogValidator.Validate(
            _services.GetRequiredService<TweakCatalogService>().Catalog, requireExactCount: false);
        if (catalogErrors.Count > 0)
        {
            System.Windows.MessageBox.Show(string.Join(Environment.NewLine, catalogErrors.Take(10)), "RigTune — tweak catalog problems",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

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

            // Crash recovery first (restores priorities/EcoQoS/power plan of a stale session), then watchers.
            await _services.GetRequiredService<GameModeService>().InitializeAsync();
            RefreshRateWatchdogService watchdog = _services.GetRequiredService<RefreshRateWatchdogService>();
            watchdog.Start();
            await watchdog.CheckAsync("app startup");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Non-fatal: the UI still works; failures surface where persistence is required.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services?.GetService<GameModeService>() is { ActiveSession: not null } gameMode)
        {
            // Restore every priority / EcoQoS flag / power plan before the process goes away.
            Task.Run(() => gameMode.StopAsync("RigTune exited")).Wait(TimeSpan.FromSeconds(10));
        }

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
        services.AddSingleton<PowerRunner>();
        services.AddSingleton<IPowerAccess>(sp => sp.GetRequiredService<PowerRunner>());
        services.AddSingleton<INvApiDrsAccess, NvApiRunner>();
        services.AddSingleton<IRestorePointBackend, RestorePointRunner>();
        services.AddSingleton<DismRunner>();
        services.AddSingleton<DeviceManagerRunner>();
        services.AddSingleton<PciRunner>();
        services.AddSingleton<NetworkAdapterRunner>();

        // Core: snapshot-able state runners (generic true rollback)
        services.AddSingleton<DismFeatureStateRunner>();
        services.AddSingleton<BcdStateRunner>();
        services.AddSingleton<WinReStateRunner>();
        services.AddSingleton<TaskSchedulerRunner>();
        services.AddSingleton<TcpGlobalStateRunner>();
        services.AddSingleton<IStateRunner>(sp => sp.GetRequiredService<DismFeatureStateRunner>());
        services.AddSingleton<IStateRunner>(sp => sp.GetRequiredService<BcdStateRunner>());
        services.AddSingleton<IStateRunner>(sp => sp.GetRequiredService<WinReStateRunner>());
        services.AddSingleton<IStateRunner>(sp => sp.GetRequiredService<TaskSchedulerRunner>());
        services.AddSingleton<IStateRunner>(sp => sp.GetRequiredService<DeviceManagerRunner>());
        services.AddSingleton<IStateRunner>(sp => sp.GetRequiredService<TcpGlobalStateRunner>());
        services.AddSingleton<ChromiumFlagStateRunner>();
        services.AddSingleton<IStateRunner>(sp => sp.GetRequiredService<ChromiumFlagStateRunner>());

        // Core: services
        services.AddSingleton<SnapshotService>(sp => new SnapshotService(
            sp.GetRequiredService<RigTunePaths>(),
            sp.GetRequiredService<IRegistryAccess>(),
            sp.GetRequiredService<IServiceAccess>(),
            sp.GetRequiredService<IPowerAccess>(),
            sp.GetRequiredService<INvApiDrsAccess>(),
            sp.GetRequiredService<AuditLogService>(),
            stateRunners: sp.GetServices<IStateRunner>()));
        services.AddSingleton<RestorePointService>();
        services.AddSingleton<HostProfileDetector>();
        services.AddSingleton<WasapiInspectService>();
        services.AddSingleton<TimerResolutionService>();
        services.AddSingleton<TweakCatalogService>();

        // Core: System Care (Phase 3)
        services.AddSingleton<ISystemMaintenanceNative, SystemMaintenanceNative>();
        services.AddSingleton<CleanupEngine>(sp => new CleanupEngine(
            sp.GetRequiredService<AuditLogService>(), sp.GetRequiredService<ISystemMaintenanceNative>()));
        services.AddSingleton<StartupManager>(sp => new StartupManager(
            sp.GetRequiredService<IRegistryAccess>(), sp.GetRequiredService<SnapshotService>(), sp.GetRequiredService<AuditLogService>()));
        services.AddSingleton<RepairScanner>();
        services.AddSingleton<SystemCareService>();
        services.AddSingleton<IAppxPackageSource, WinRtAppxPackageSource>();
        services.AddSingleton<IWingetCli, WingetCli>();
        services.AddSingleton<AppxWingetService>();

        // Core: tweak engine + one handler per SPEC §4.1 action type
        services.AddSingleton<RegistryActionHandler>();
        services.AddSingleton<ITweakActionHandler>(sp => sp.GetRequiredService<RegistryActionHandler>());
        services.AddSingleton<ITweakActionHandler, ServiceActionHandler>();
        services.AddSingleton<ITweakActionHandler, PowerActionHandler>();
        services.AddSingleton<ITweakActionHandler, NvApiActionHandler>();
        services.AddSingleton<ITweakActionHandler, DismActionHandler>();
        services.AddSingleton<ITweakActionHandler, ScheduledTaskActionHandler>();
        services.AddSingleton<ITweakActionHandler, DeviceManagerActionHandler>();
        services.AddSingleton<ITweakActionHandler>(sp => new PciActionHandler(sp.GetRequiredService<PciRunner>()));
        services.AddSingleton<ITweakActionHandler, NetworkActionHandler>();
        services.AddSingleton<ITweakActionHandler, NtfsActionHandler>();
        services.AddSingleton<ITweakActionHandler, InspectionActionHandler>();
        services.AddSingleton<ITweakActionHandler, CleanupActionHandler>();
        services.AddSingleton<ITweakActionHandler, AppStartupActionHandler>();
        services.AddSingleton<ITweakActionHandler>(sp => new ChromiumFlagActionHandler(sp.GetRequiredService<ChromiumFlagStateRunner>()));
        services.AddSingleton<TweakEngine>();
        // One shared store for gamemode.json (App Optimizer rules + Game Mode lists/session).
        services.AddSingleton(sp => new JsonDocumentStore<GameModeDocument>(sp.GetRequiredService<RigTunePaths>().GameModeFile));
        services.AddSingleton<AppOptimizerService>(sp => new AppOptimizerService(
            sp.GetRequiredService<TweakEngine>(), sp.GetRequiredService<IRegistryAccess>(), sp.GetRequiredService<AuditLogService>(),
            RigTunePaths.BundledAppsFile, sp.GetRequiredService<RigTunePaths>().CustomAppsFile,
            sp.GetRequiredService<JsonDocumentStore<GameModeDocument>>()));

        // Core: Game Mode & watchdogs (Phase 5)
        services.AddSingleton<IDisplayAccess, DisplayRunner>();
        services.AddSingleton<RefreshRateWatchdogService>();
        services.AddSingleton<IGameModePlatform, WindowsGameModePlatform>();
        services.AddSingleton<GameLibraryScanner>(sp => new GameLibraryScanner(sp.GetRequiredService<IRegistryAccess>()));
        services.AddSingleton<StartWithWindowsService>();
        services.AddSingleton<GameModeService>(sp => GameModeService.Create(
            sp.GetRequiredService<IGameModePlatform>(), sp.GetRequiredService<JsonDocumentStore<GameModeDocument>>(),
            sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<AuditLogService>(),
            sp.GetRequiredService<AppOptimizerService>(), sp.GetRequiredService<RefreshRateWatchdogService>()));
        services.AddSingleton<SystemTrayManager>();

        // Core: Disk Analyzer & Space Advisor (Phase 6)
        services.AddSingleton(_ => PathGuard.ForThisMachine());
        services.AddSingleton(sp => new JsonDocumentStore<DiskLocksDocument>(sp.GetRequiredService<RigTunePaths>().DiskLocksFile));
        services.AddSingleton<DiskLockService>();
        services.AddSingleton<IRecycleBin, WindowsRecycleBin>();
        services.AddSingleton<DiskAnalyzerService>(sp =>
        {
            JsonDocumentStore<GameModeDocument> gameStore = sp.GetRequiredService<JsonDocumentStore<GameModeDocument>>();
            return new DiskAnalyzerService(sp.GetRequiredService<JsonDocumentStore<DiskLocksDocument>>(), async ct =>
                DiskCategoryClassifier.ForThisMachine((await gameStore.ReadAsync(ct)).DiscoveredGames.Select(g => g.InstallDir)));
        });
        services.AddSingleton<DiskDeletionService>(sp => new DiskDeletionService(sp.GetRequiredService<PathGuard>(),
            sp.GetRequiredService<DiskLockService>(), sp.GetRequiredService<IRecycleBin>(), sp.GetRequiredService<AuditLogService>(),
            sp.GetRequiredService<DiskAnalyzerService>()));
        services.AddSingleton<SpaceAdvisorService>(sp => new SpaceAdvisorService(
            AdvisorEnvironment.ForThisMachine(sp.GetRequiredService<GameLibraryScanner>().FindSteamRoot()),
            sp.GetRequiredService<DiskLockService>(), sp.GetRequiredService<DiskDeletionService>(), sp.GetRequiredService<IRecycleBin>(),
            sp.GetRequiredService<AuditLogService>(), sp.GetRequiredService<AppOptimizerService>(), sp.GetRequiredService<CleanupEngine>(),
            sp.GetRequiredService<DiskAnalyzerService>()));

        // UI
        services.AddSingleton<MainWindow>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<TweaksViewModel>();
        services.AddSingleton<RestorePointsTabViewModel>();
        services.AddSingleton<CleanupTabViewModel>();
        services.AddSingleton<StartupTabViewModel>();
        services.AddSingleton<AppxWingetTabViewModel>();
        services.AddSingleton<RepairTabViewModel>();
        services.AddSingleton<SystemCareViewModel>();
        services.AddSingleton<AppOptimizerViewModel>();
        services.AddSingleton<GameModeViewModel>();
        services.AddSingleton<DiskAnalyzerViewModel>();
        services.AddSingleton<DialogService>();
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
