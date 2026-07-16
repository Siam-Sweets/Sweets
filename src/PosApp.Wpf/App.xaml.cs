using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;
using PosApp.Data.Repositories;
using PosApp.Hardware;
using PosApp.Hardware.Devices;
using PosApp.Hardware.Printers;
using PosApp.Localization;
using PosApp.Services;
using PosApp.Wpf.Views;

namespace PosApp.Wpf;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static User? CurrentUser { get; set; }
    public static StoreSettings StoreSettings { get; set; } = new();
    public static string? StartupError { get; private set; }

    public App()
    {
        // Register global unhandled-exception handlers FIRST, before anything else runs.
        // These make sure the user always sees an error dialog instead of a silent crash.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Log($"DISPATCHER EXCEPTION: {e.Exception}");
            MessageBox.Show(
                $"An error occurred:\n\n{e.Exception.Message}\n\n" +
                $"Details logged to:\n{LogFilePath}\n\n" +
                $"Stack trace:\n{e.Exception.StackTrace}",
                "PosApp - Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }
        catch
        {
            e.Handled = false;
        }
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            Log($"APPDOMAIN EXCEPTION (isTerminating={e.IsTerminating}): {ex}");
            MessageBox.Show(
                $"A fatal error occurred:\n\n{ex?.Message}\n\n" +
                $"Details logged to:\n{LogFilePath}\n\n" +
                $"Stack trace:\n{ex?.StackTrace}",
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
            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();
            Log("DI container built.");

            // Initialize DB with explicit error handling
            using (var scope = Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Log($"Creating database at: {DbPathResolver.DefaultPath()}");
                await db.Database.EnsureCreatedAsync();
                Log("Database ready (created or already exists).");
                await DbSeeder.SeedAsync(db);
                Log("Database seeded.");
            }

            // Load settings
            Log("Loading store settings...");
            var settingsService = Services.GetRequiredService<ISettingsService>();
            StoreSettings = await settingsService.GetStoreSettingsAsync();
            Log($"Settings loaded. Store: {StoreSettings.StoreName}");

            // Apply language from settings
            var loc = LocalizationManager.Instance;
            loc.SetLanguage(StoreSettings.Language ?? "en");

            // Show login window
            Log("Showing login window...");
            var login = Services.GetRequiredService<LoginView>();
            login.Show();
            Log("Login window shown. Startup complete.");
        }
        catch (Exception ex)
        {
            StartupError = ex.ToString();
            Log($"FATAL DURING STARTUP: {ex}");
            MessageBox.Show(
                $"PosApp failed to start.\n\nError: {ex.Message}\n\n" +
                $"Full details written to:\n{LogFilePath}\n\n" +
                $"Stack trace:\n{ex.StackTrace}",
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

        // Hardware - safe defaults (no-op) so app never crashes if devices are missing
        services.AddSingleton<IReceiptPrinter>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            return new WindowsPrinter(settings);
        });
        services.AddSingleton<ICashDrawer, NullCashDrawer>();
        services.AddSingleton<IBarcodeScanner, NullBarcodeScanner>();
        services.AddSingleton<IWeighingScale, NullWeighingScale>();
        services.AddSingleton<IHardwareService, HardwareService>();

        // Views
        services.AddTransient<LoginView>();
        services.AddTransient<MainWindow>();
        services.AddTransient<PosView>();
        services.AddTransient<ProductsView>();
        services.AddTransient<InventoryView>();
        services.AddTransient<CustomersView>();
        services.AddTransient<SalesView>();
        services.AddTransient<ReportsView>();
        services.AddTransient<UsersView>();
        services.AddTransient<SettingsView>();
        services.AddTransient<PaymentDialog>();
    }
}
