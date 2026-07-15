using System.IO;
using System.Windows;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Initialize DB
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            await DbSeeder.SeedAsync(db);
        }

        // Load settings
        var settingsService = Services.GetRequiredService<ISettingsService>();
        StoreSettings = await settingsService.GetStoreSettingsAsync();

        // Apply language from settings
        var loc = LocalizationManager.Instance;
        loc.SetLanguage(StoreSettings.Language ?? "en");

        // Show login window
        var login = Services.GetRequiredService<LoginView>();
        login.Show();
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

        // Hardware
        services.AddSingleton<IReceiptPrinter>(sp =>
        {
            // Use WindowsPrinter by default (more compatible). Switch to EscPosPrinter
            // in Settings if a thermal printer is attached.
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
