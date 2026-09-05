using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PortableDroid.App.ViewModels;
using PortableDroid.App.Views;
using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Models;
using PortableDroid.Core.Services;
using PortableDroid.Infrastructure;
using PortableDroid.Runtime;
using PortableDroid.Runtime.Adb;
using PortableDroid.Runtime.Qemu;

namespace PortableDroid.App;

/// <summary>
/// Composition root. Wiring is done by hand rather than with a DI container so the
/// application starts fast and pulls in no extra dependencies (spec §9, §52).
/// </summary>
public partial class App : Application
{
    private FileLogService? _log;
    private RuntimeManager? _runtime;
    private MainViewModel? _viewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Pin an explicit, specific (non-neutral) culture before any XAML is loaded. WPF resolves
        // a specific culture for every data binding (XmlLanguage.GetSpecificCulture); if that fails
        // the main window dies before it renders. The real fix is InvariantGlobalization=false in
        // Directory.Build.props — this is a second line of defence. If the culture cannot be
        // resolved for any reason we silently keep the system default rather than block startup.
        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch
        {
            // fall back to the system default culture
        }

        try
        {
            // 1. Everything hangs off the folder the exe is in — this is what makes it portable.
            var paths = new PathService();
            paths.EnsureLayout();

            _log = new FileLogService(paths);
            _log.Info("core", "----------------------------------------------------------");
            _log.Info("core", $"PortableDroid starting from {paths.Root}");

            var settings = new SettingsService(paths, _log);
            await settings.LoadAsync();

            var hardware = new HardwareDetector(_log, paths);
            var graphics = new GraphicsManager(paths, _log);
            var profiles = new ProfileService(paths, _log);
            var storage = new StorageManager(paths, settings, _log);
            var backend = new QemuBackend(paths, _log, settings, graphics, hardware);
            var adb = new AdbService(paths, _log, settings);

            _runtime = new RuntimeManager(backend, adb, hardware, profiles, settings, storage, _log);

            var apks = new ApkManager(adb, paths, _log, settings, _runtime);
            var backups = new BackupManager(paths, _log, _runtime);
            var monitor = new ResourceMonitor(backend);

            _viewModel = new MainViewModel(_runtime, apks, hardware, monitor, backups, settings, _log, paths);

            var window = new MainWindow(_viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _log?.Crash("PortableDroid could not start.", ex);
            MessageBox.Show(
                "PortableDroid could not start.\n\n" +
                "This usually means the folder it is running from is read-only, or the files are incomplete.\n\n" +
                "Try copying the whole PortableDroid folder to a normal folder on a drive you can write to.\n\n" +
                $"Details: {ex.Message}",
                "PortableDroid",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            // Stop Android cleanly so the guest filesystem is flushed and no VM is orphaned.
            if (_runtime is not null)
            {
                _log?.Info("core", "Shutting down; stopping the Android runtime.");
                await _runtime.DisposeAsync();
            }
            _viewModel?.Dispose();
            _log?.Info("core", "PortableDroid exited.");
            _log?.Dispose();
        }
        catch
        {
            // never block exit
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Crash("Unhandled UI exception.", e.Exception);
        var error = PortableDroidError.Unexpected("continue", e.Exception);
        MessageBox.Show(error.ToUserMessage(), "PortableDroid", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // keep the app alive; the user's Android session may still be fine
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        _log?.Crash("Unhandled application exception.", e.ExceptionObject as Exception);
}
