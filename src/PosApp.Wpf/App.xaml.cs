using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;
using PosApp.Hardware;
using PosApp.Hardware.Devices;
using PosApp.Hardware.Printers;
using PosApp.Localization;
using PosApp.Services;
using PosApp.Wpf.Helpers;
using PosApp.Wpf.Views;

namespace PosApp.Wpf;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static User? CurrentUser { get; set; }
    public static StoreSettings StoreSettings { get; private set; } = new();
    public static event EventHandler? SettingsChanged;

    public static void PublishSettings(StoreSettings settings)
    {
        StoreSettings = settings;
        SettingsChanged?.Invoke(null, EventArgs.Empty);
    }
    private bool _startupCompleted;
    private bool _dispatcherErrorDialogOpen;
    private static IServiceScope? _activeSessionScope;

    public App()
    {
        // Register global unhandled-exception handlers FIRST, before anything else runs.
        // These make sure the user always sees an error dialog instead of a silent crash.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) RuntimeUiText.LocalizeWindow(window);
            }));

        EnterKeyNavigation.Register();
    }

    /// <summary>Path to a log file in %LOCALAPPDATA%\PosApp\posapp.log</summary>
    public static string LogFilePath
    {
        get
        {
            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PosApp");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "posapp.log");
            }
            catch
            {
                return "posapp.log";
            }
        }
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(LogFilePath, $"[{DateTime.Now:u}] {message}\n"); }
        catch { /* logging must never throw */ }
    }

    public static void LogError(string context, Exception exception)
        => Log($"{context}: {exception}");

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Mark the exception handled before displaying a modal dialog. MessageBox
        // runs a nested dispatcher loop, so another UI exception can otherwise
        // recursively create a stack of identical dialogs.
        e.Handled = true;
        Log($"DISPATCHER EXCEPTION: {e.Exception}");
        if (_dispatcherErrorDialogOpen) return;

        try
        {
            _dispatcherErrorDialogOpen = true;
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
                $"An error occurred:\n\n{e.Exception.Message}\n\n" +
                $"Technical details were written to:\n{LogFilePath}",
                "PosApp - Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // The original exception is already logged. Error reporting must not
            // terminate the register if the dialog itself cannot be displayed.
        }
        finally
        {
            _dispatcherErrorDialogOpen = false;
        }
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            Log($"APPDOMAIN EXCEPTION (isTerminating={e.IsTerminating}): {ex}");
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
                $"A fatal error occurred:\n\n{ex?.Message}\n\n" +
                $"Technical details were written to:\n{LogFilePath}",
                "PosApp - Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            Log($"UNOBSERVED TASK EXCEPTION: {e.Exception}");
            e.SetObserved();
        }
        catch { }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log("=== PosApp starting ===");

        try
        {
            var preRestoreBackup = DbPathResolver.ApplyPendingRestore();
            if (preRestoreBackup != null)
                Log($"Pending restore applied. Previous database preserved at: {preRestoreBackup}");

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();
            Log("DI container built.");

            // Protect upgrades started either inside PosApp or by directly
            // running a newer installer/portable executable. This snapshot is
            // completed before EF is allowed to change the database schema.
            var preMigrationBackup = await Services.GetRequiredService<IUpdateService>()
                .EnsurePreMigrationBackupAsync();
            if (preMigrationBackup != null)
                Log($"Pre-migration safety backup ready: {preMigrationBackup.BackupPath}");

            // Initialize DB with explicit error handling
            using (var scope = Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Log($"Creating database at: {DbPathResolver.DefaultPath()}");
                await db.Database.EnsureCreatedAsync();
                Log("Database ready (created or already exists).");
                await DbSchemaUpgrader.ApplyAsync(db);
                Log("Database schema upgrades applied.");
                await DbSeeder.SeedAsync(db);
                Log("Database seeded.");
            }

            // Load settings
            Log("Loading store settings...");
            var settingsService = Services.GetRequiredService<ISettingsService>();
            PublishSettings(await settingsService.GetStoreSettingsAsync());
            Log($"Settings loaded. Store: {StoreSettings.StoreName}");
            ApplyTheme(StoreSettings.Theme);

            // Apply language from settings
            ApplyLanguage(StoreSettings.Language);

            // A seeded local database is not the same as a configured store.
            // Until the one-time flag is written, show setup before login.
            var setupService = Services.GetRequiredService<ISetupService>();
            if (!await setupService.IsSetupCompleteAsync())
            {
                Log("First-run setup is required.");
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var setup = Services.GetRequiredService<SetupView>();
                MainWindow = setup;
                var setupCompleted = setup.ShowDialog() == true;
                if (!setupCompleted)
                {
                    Log("Setup was closed before completion.");
                    Shutdown();
                    return;
                }

                PublishSettings(await settingsService.GetStoreSettingsAsync());
                ApplyTheme(StoreSettings.Theme);
                ApplyLanguage(StoreSettings.Language);
                Log($"First-run setup completed. Store: {StoreSettings.StoreName}");
            }

            if (StoreSettings.AutomaticBackupEnabled && StoreSettings.BackupOnStartup)
            {
                try
                {
                    var backup = Services.GetRequiredService<IBackupService>();
                    var path = await backup.CreateBackupAsync(
                        retentionCount: StoreSettings.BackupRetentionCount);
                    Log($"Startup backup created: {path}");
                }
                catch (Exception backupError)
                {
                    Log($"Startup backup failed (continuing): {backupError}");
                }
            }

            // Show login window
            Log("Showing login window...");
            var login = CreateLoginSession(out var previousSession);
            previousSession?.Dispose();
            MainWindow = login;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            login.Show();
            _startupCompleted = true;
            try
            {
                // Do not clear update recovery state until the complete normal
                // startup path, including setup, settings, language and login UI,
                // has succeeded.
                var completedUpdate = await Services.GetRequiredService<IUpdateService>()
                    .MarkStartupSuccessfulAsync();
                if (completedUpdate != null)
                    Log($"Safe update result: {completedUpdate.State}; " +
                        $"{completedUpdate.FromVersion} -> {completedUpdate.RunningVersion}; " +
                        $"recovery backup: {completedUpdate.BackupPath}");
            }
            catch (Exception updateStatusError)
            {
                // The app and database are healthy. Retain the pending record
                // and continue rather than blocking sales over status metadata.
                Log($"Could not finalize safe update status: {updateStatusError}");
            }
            Log("Login window shown. Startup complete.");
        }
        catch (Exception ex)
        {
            Log($"FATAL DURING STARTUP: {ex}");
            var updateRecovery = string.Empty;
            try
            {
                var updateService = Services?.GetService<IUpdateService>();
                var pendingUpdate = updateService == null
                    ? null
                    : await updateService.GetPendingUpdateAsync();
                if (pendingUpdate != null && File.Exists(pendingUpdate.BackupPath))
                {
                    updateRecovery =
                        $"\n\nYour data was backed up before the update and has not been deleted.\n" +
                        $"Recovery backup:\n{pendingUpdate.BackupPath}\n\n" +
                        "You can reinstall the previous PosApp version and restore this file from Settings > Database.";
                    Log($"Pending update recovery backup retained: {pendingUpdate.BackupPath}");
                }
            }
            catch (Exception recoveryError)
            {
                Log($"Could not read update recovery information: {recoveryError}");
            }
            PosApp.Wpf.Helpers.LocalizedMessageBox.Show(
                $"PosApp failed to start.\n\nError: {ex.Message}\n\n" +
                $"Full details were written to:\n{LogFilePath}{updateRecovery}",
                "PosApp - Startup Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // DbContext
        services.AddDbContext<AppDbContext>(opt =>
            opt.UseSqlite(DbPathResolver.ConnectionString()),
            ServiceLifetime.Transient);

        // Services
        services.AddTransient<IAuthService, AuthService>();
        services.AddTransient<IInventoryService, InventoryService>();
        services.AddTransient<ISaleService, SaleService>();
        services.AddTransient<ICustomerService, CustomerService>();
        services.AddTransient<IReportService, ReportService>();
        services.AddTransient<ISettingsService, SettingsService>();
        services.AddTransient<IDiscountService, DiscountService>();
        services.AddTransient<ISetupService, SetupService>();
        services.AddTransient<IPurchaseService, PurchaseService>();
        services.AddTransient<IRegisterService, RegisterService>();
        services.AddTransient<ICatalogTransferService, CatalogTransferService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IUpdateService, SafeUpdateService>();

        // Hardware drivers fail safely when a configured device is unavailable.
        services.AddSingleton<IReceiptPrinter>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            return new WindowsPrinter(settings);
        });
        services.AddSingleton<IBarcodeScanner, NullBarcodeScanner>();
        services.AddSingleton<IHardwareService, HardwareService>();

        // Views
        services.AddTransient<LoginView>();
        services.AddTransient<SetupView>();
        services.AddTransient<MainWindow>();
        services.AddTransient<PosView>();
        services.AddTransient<DashboardView>();
        services.AddTransient<PromotionsView>();
        services.AddTransient<ProductsView>();
        services.AddTransient<InventoryView>();
        services.AddTransient<CustomersView>();
        services.AddTransient<SalesView>();
        services.AddTransient<ReportsView>();
        services.AddTransient<UsersView>();
        services.AddTransient<SettingsView>();
        services.AddTransient<PurchasesView>();
        services.AddTransient<RegisterView>();
    }

    /// <summary>
    /// Creates one disposable dependency scope for a login and its main window.
    /// Closing and signing in repeatedly must not retain every view and DbContext
    /// in the root service provider for the remainder of the process.
    /// </summary>
    internal static LoginView CreateLoginSession(out IServiceScope? previousSession)
    {
        previousSession = _activeSessionScope;
        var nextSession = Services.CreateScope();
        try
        {
            var login = nextSession.ServiceProvider.GetRequiredService<LoginView>();
            _activeSessionScope = nextSession;
            return login;
        }
        catch
        {
            nextSession.Dispose();
            throw;
        }
    }

    internal static void DisposePreviousSession(IServiceScope? session)
    {
        if (session == null || ReferenceEquals(session, _activeSessionScope)) return;
        session.Dispose();
    }

    internal static void RestorePreviousSession(IServiceScope? previousSession)
    {
        var failedSession = _activeSessionScope;
        _activeSessionScope = previousSession;
        if (failedSession != null && !ReferenceEquals(failedSession, previousSession))
            failedSession.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_startupCompleted && StoreSettings.AutomaticBackupEnabled && StoreSettings.BackupOnExit)
        {
            try
            {
                var backup = Services.GetRequiredService<IBackupService>();
                var path = backup.CreateBackupAsync(
                        retentionCount: StoreSettings.BackupRetentionCount)
                    .GetAwaiter().GetResult();
                Log($"Exit backup created: {path}");
            }
            catch (Exception backupError)
            {
                Log($"Exit backup failed: {backupError}");
            }
        }

        try
        {
            _activeSessionScope?.Dispose();
            _activeSessionScope = null;
            (Services as IDisposable)?.Dispose();
        }
        catch (Exception disposeError)
        {
            Log($"Service cleanup failed: {disposeError}");
        }
        finally
        {
            base.OnExit(e);
        }
    }

    public static void ApplyTheme(string? theme)
    {
        var dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);

        SetThemeBrush("BackgroundBrush", dark ? "#0F172A" : "#F1F5F9");
        SetThemeBrush("CardBrush", dark ? "#1E293B" : "#FFFFFF");
        SetThemeBrush("InputBrush", dark ? "#111827" : "#FFFFFF");
        SetThemeBrush("TextDarkBrush", dark ? "#F8FAFC" : "#0F172A");
        SetThemeBrush("TextMutedBrush", dark ? "#94A3B8" : "#64748B");
        SetThemeBrush("BorderBrush", dark ? "#334155" : "#E2E8F0");
        SetThemeBrush("SurfaceMutedBrush", dark ? "#293548" : "#F1F5F9");
        SetThemeBrush("SurfaceHoverBrush", dark ? "#334155" : "#F8FAFC");
        SetThemeBrush("AlternateRowBrush", dark ? "#243247" : "#F8FAFC");
        SetThemeBrush("SelectionBrush", dark ? "#1D4ED8" : "#DBEAFE");
        SetThemeBrush("SuccessSurfaceBrush", dark ? "#153A2B" : "#F0FDF4");
        SetThemeBrush("SuccessTextBrush", dark ? "#86EFAC" : "#15803D");
        SetThemeBrush("DangerSurfaceBrush", dark ? "#451A24" : "#FEF2F2");
        SetThemeBrush("DangerTextBrush", dark ? "#FCA5A5" : "#DC2626");
        SetThemeBrush("InfoSurfaceBrush", dark ? "#172554" : "#EFF6FF");
        SetThemeBrush("InfoTextBrush", dark ? "#93C5FD" : "#2563EB");
        SetThemeBrush("SidebarBrush", dark ? "#020617" : "#FFFFFF");
        SetThemeBrush("SidebarActiveBrush", dark ? "#000000" : "#DBEAFE");
        SetThemeBrush("SidebarTextBrush", dark ? "#E2E8F0" : "#0F172A");
        SetThemeBrush("SidebarMutedBrush", dark ? "#94A3B8" : "#64748B");
        SetThemeBrush("SidebarHoverBrush", dark ? "#334155" : "#F1F5F9");
        SetThemeBrush("SidebarCardBrush", dark ? "#0F172A" : "#F8FAFC");
        SetThemeBrush("SidebarBorderBrush", dark ? "#334155" : "#E2E8F0");
        SetThemeBrush("CommandPanelBrush", dark ? "#182235" : "#F8FAFC");
        SetThemeBrush("CommandTileBrush", dark ? "#243247" : "#FFFFFF");
        SetThemeBrush("CommandTileHoverBrush", dark ? "#334155" : "#EFF6FF");
        SetThemeBrush("CommandTextBrush", dark ? "#F8FAFC" : "#0F172A");
        SetThemeBrush("CommandMutedBrush", dark ? "#94A3B8" : "#64748B");
        SetThemeBrush("CommandBorderBrush", dark ? "#475569" : "#CBD5E1");
        SetThemeBrush("CommandStatusBrush", dark ? "#111827" : "#FFFFFF");
        SetThemeBrush("OverlayScrimBrush", dark ? "#B3000000" : "#140F172A");
        SetThemeBrush("DrawerPanelBrush", dark ? "#111827" : "#FFFFFF");
        SetThemeBrush("DrawerTextBrush", dark ? "#F8FAFC" : "#0F172A");
        SetThemeBrush("DrawerMutedBrush", dark ? "#94A3B8" : "#64748B");
        SetThemeBrush("DrawerBorderBrush", dark ? "#334155" : "#E2E8F0");
        SetThemeBrush("DrawerHoverBrush", dark ? "#334155" : "#F1F5F9");
    }

    public static void ApplyLanguage(string? language)
    {
        var code = string.Equals(language, "bn", StringComparison.OrdinalIgnoreCase) ? "bn" : "en";
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(code == "bn"
                ? "pack://application:,,,/PosApp.Localization;component/Strings.bn.xaml"
                : "pack://application:,,,/PosApp.Localization;component/Strings.en.xaml")
        };

        var mergedDictionaries = Current.Resources.MergedDictionaries;
        for (var i = mergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (mergedDictionaries[i].Source?.OriginalString.Contains("Strings.") == true)
                mergedDictionaries.RemoveAt(i);
        }
        mergedDictionaries.Insert(0, dictionary);
        LocalizationManager.Instance.SetLanguage(code);
        foreach (Window window in Current.Windows) RuntimeUiText.LocalizeWindow(window);
    }

    private static void SetThemeBrush(string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex)!;
        // Publish a new application-level resource so every DynamicResource
        // reference is invalidated immediately, including already-open drawers.
        Current.Resources[key] = new SolidColorBrush(color);
    }
}
