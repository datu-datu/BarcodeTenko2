using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Tenko.Lite.Services;
using Tenko.Lite.ViewModels;

namespace Tenko.Lite
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private ServiceProvider? _serviceProvider;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            ConfigureServices(services);

            _serviceProvider = services.BuildServiceProvider();

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // Core Storage & Infrastructure Services (StudentService は不要)
            services.AddSingleton<StorageService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<HistoryService>();
            services.AddSingleton<ScanFileService>();
            services.AddSingleton<NotificationService>();

            // Server Synchronization
            services.AddSingleton<ServerSyncService>(sp =>
            {
                var storage = sp.GetRequiredService<StorageService>();
                return new ServerSyncService(persistFilePath: storage.GetDataPath(ServerSyncService.PersistFileName));
            });

            // UI & Utility Services
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IClockService, ClockService>();
            services.AddSingleton<IExportService, ExportService>();

            // Core UseCase & Processor (StudentService なし)
            services.AddSingleton<IScanProcessor, ScanProcessor>();

            // ViewModels
            services.AddTransient<MainViewModel>();

            // Views
            services.AddTransient<MainWindow>();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _serviceProvider?.Dispose();
            base.OnExit(e);
        }
    }
}
