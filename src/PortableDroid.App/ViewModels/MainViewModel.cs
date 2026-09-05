using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PortableDroid.Core.Abstractions;
using PortableDroid.Core.Models;

namespace PortableDroid.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IRuntimeManager _runtime;
    private readonly IApkManager _apks;
    private readonly IHardwareDetector _hardware;
    private readonly IResourceMonitor _monitor;
    private readonly IBackupManager _backups;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly IPathService _paths;
    private readonly DispatcherTimer _timer;

    private string _statusText = "Android is stopped";
    private string _busyMessage = "";
    private bool _isBusy;
    private string _hardwareSummary = "Detecting hardware...";
    private string _resourceSummary = "";
    private string _profileSummary = "";
    private string _androidVersion = "";
    private RuntimeState _state = RuntimeState.Stopped;
    private string _logText = "";
    private string _selectedLogChannel = "portable-droid";

    public MainViewModel(
        IRuntimeManager runtime,
        IApkManager apks,
        IHardwareDetector hardware,
        IResourceMonitor monitor,
        IBackupManager backups,
        ISettingsService settings,
        ILogService log,
        IPathService paths)
    {
        _runtime = runtime;
        _apks = apks;
        _hardware = hardware;
        _monitor = monitor;
        _backups = backups;
        _settings = settings;
        _log = log;
        _paths = paths;

        StartCommand = new RelayCommand(async _ => await StartAsync(), _ => !IsBusy && State is RuntimeState.Stopped or RuntimeState.Crashed or RuntimeState.Error);
        StopCommand = new RelayCommand(async _ => await StopAsync(), _ => !IsBusy && State is RuntimeState.Running or RuntimeState.Crashed);
        RestartCommand = new RelayCommand(async _ => await RestartAsync(), _ => !IsBusy && State == RuntimeState.Running);
        InstallApkCommand = new RelayCommand(async _ => await InstallApkViaPickerAsync(), _ => !IsBusy && State == RuntimeState.Running);
        RefreshLibraryCommand = new RelayCommand(async _ => await RefreshLibraryAsync(), _ => !IsBusy);
        LaunchCommand = new RelayCommand(async p => await LaunchAsync(p as InstalledApp), p => !IsBusy && State == RuntimeState.Running && p is InstalledApp);
        StopAppCommand = new RelayCommand(async p => await StopAppAsync(p as InstalledApp), p => !IsBusy && State == RuntimeState.Running && p is InstalledApp);
        UninstallCommand = new RelayCommand(async p => await UninstallAsync(p as InstalledApp), p => !IsBusy && State == RuntimeState.Running && p is InstalledApp);
        BackupCommand = new RelayCommand(async _ => await BackupAsync(), _ => !IsBusy && State == RuntimeState.Stopped);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(_paths.Root));
        OpenLogsCommand = new RelayCommand(_ => OpenFolder(_paths.Logs));
        RefreshLogsCommand = new RelayCommand(_ => RefreshLogs());
        SaveSettingsCommand = new RelayCommand(async _ => await SaveSettingsAsync(), _ => !IsBusy);

        _runtime.StateChanged += OnStateChanged;
        _runtime.ErrorRaised += OnErrorRaised;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await SampleResourcesAsync();
        _timer.Start();
    }

    public ObservableCollection<InstalledApp> Apps { get; } = new();
    public ObservableCollection<BackupInfo> Backups { get; } = new();
    public IReadOnlyList<string> LogChannels => _log.Channels;

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand InstallApkCommand { get; }
    public ICommand RefreshLibraryCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand StopAppCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand RefreshLogsCommand { get; }
    public ICommand SaveSettingsCommand { get; }

    public RuntimeState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(IsRunning));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsRunning => State == RuntimeState.Running;

    public string StatusColor => State switch
    {
        RuntimeState.Running => "#22C55E",
        RuntimeState.Starting or RuntimeState.Stopping => "#F59E0B",
        RuntimeState.Crashed or RuntimeState.Error => "#EF4444",
        _ => "#94A3B8"
    };

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string BusyMessage { get => _busyMessage; private set => Set(ref _busyMessage, value); }
    public string HardwareSummary { get => _hardwareSummary; private set => Set(ref _hardwareSummary, value); }
    public string ResourceSummary { get => _resourceSummary; private set => Set(ref _resourceSummary, value); }
    public string ProfileSummary { get => _profileSummary; private set => Set(ref _profileSummary, value); }
    public string AndroidVersion { get => _androidVersion; private set => Set(ref _androidVersion, value); }
    public string RootPath => _paths.Root;
    public string LogText { get => _logText; private set => Set(ref _logText, value); }

    public string SelectedLogChannel
    {
        get => _selectedLogChannel;
        set { if (Set(ref _selectedLogChannel, value)) RefreshLogs(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---- settings bound directly to the Settings tab -----------------------------------

    public AppSettings Settings => _settings.Current;

    public IReadOnlyList<string> PerformanceProfiles { get; } =
        Enum.GetNames<PerformanceProfileKind>();

    public string SelectedPerformanceProfile
    {
        get => _settings.Current.Runtime.PerformanceProfile.ToString();
        set
        {
            if (Enum.TryParse<PerformanceProfileKind>(value, out var kind))
            {
                _settings.Current.Runtime.PerformanceProfile = kind;
                OnPropertyChanged();
            }
        }
    }

    public IReadOnlyList<string> GraphicsModes { get; } = Enum.GetNames<GraphicsMode>();

    public string SelectedGraphicsMode
    {
        get => _settings.Current.Graphics.Mode.ToString();
        set
        {
            if (Enum.TryParse<GraphicsMode>(value, out var mode))
            {
                _settings.Current.Graphics.Mode = mode;
                OnPropertyChanged();
            }
        }
    }

    // ---- lifecycle ----------------------------------------------------------------------

    public async Task InitialiseAsync()
    {
        try
        {
            var hw = await _hardware.DetectAsync().ConfigureAwait(true);
            HardwareSummary =
                $"{hw.CpuName}  •  {hw.PhysicalCores} cores  •  {hw.TotalRamMB / 1024.0:n1} GB RAM  •  {hw.GpuName}";

            if (!hw.VirtualizationAvailable)
                StatusText = "Android is stopped (hardware acceleration is off — see Settings)";

            await RefreshLibraryAsync().ConfigureAwait(true);
            RefreshBackups();
            RefreshLogs();
        }
        catch (Exception ex)
        {
            _log.Error("core", "Startup checks failed.", ex);
        }
    }

    private async Task StartAsync()
    {
        await RunBusyAsync("Starting Android", async progress =>
        {
            await _runtime.StartAsync(progress).ConfigureAwait(true);
            AndroidVersion = await _runtime.GetAndroidVersionAsync().ConfigureAwait(true) ?? "";
            ProfileSummary = _runtime.ActiveProfile is { } p
                ? $"{p.Kind} • {p.MemoryMB} MB • {p.CpuCores} cores • {p.Resolution} • {p.GraphicsMode}"
                : "";
            await RefreshLibraryAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task StopAsync() =>
        await RunBusyAsync("Stopping Android", async progress =>
        {
            await _runtime.StopAsync(progress).ConfigureAwait(true);
            AndroidVersion = "";
        }).ConfigureAwait(true);

    private async Task RestartAsync() =>
        await RunBusyAsync("Restarting Android", async progress =>
            await _runtime.RestartAsync(progress).ConfigureAwait(true)).ConfigureAwait(true);

    public async Task InstallApkAsync(string path)
    {
        await RunBusyAsync("Installing app", async progress =>
        {
            var app = await _apks.InstallAsync(path, progress).ConfigureAwait(true);
            await RefreshLibraryAsync().ConfigureAwait(true);
            BusyMessage = $"{app.DisplayName} installed.";
        }).ConfigureAwait(true);
    }

    private async Task InstallApkViaPickerAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an APK to install",
            Filter = "Android app (*.apk)|*.apk",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
            await InstallApkAsync(dialog.FileName).ConfigureAwait(true);
    }

    private async Task RefreshLibraryAsync()
    {
        try
        {
            var apps = await _apks.GetLibraryAsync().ConfigureAwait(true);
            Apps.Clear();
            foreach (var a in apps) Apps.Add(a);
        }
        catch (Exception ex)
        {
            _log.Warn("core", "Could not refresh the app list.", ex);
        }
    }

    private async Task LaunchAsync(InstalledApp? app)
    {
        if (app is null) return;
        await RunBusyAsync($"Starting {app.DisplayName}", async _ =>
            await _apks.LaunchAsync(app.PackageName).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private async Task StopAppAsync(InstalledApp? app)
    {
        if (app is null) return;
        await RunBusyAsync($"Stopping {app.DisplayName}", async _ =>
            await _apks.StopAsync(app.PackageName).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private async Task UninstallAsync(InstalledApp? app)
    {
        if (app is null) return;

        // Destructive: always confirm first (spec §50).
        var answer = MessageBox.Show(
            $"Remove {app.DisplayName}?\n\nThis permanently deletes the app and its data inside Android.",
            "Remove app",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        await RunBusyAsync($"Removing {app.DisplayName}", async _ =>
        {
            await _apks.UninstallAsync(app.PackageName).ConfigureAwait(true);
            await RefreshLibraryAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task BackupAsync()
    {
        await RunBusyAsync("Creating backup", async progress =>
        {
            var info = await _backups.CreateBackupAsync(_settings.Current.ActiveProfile, progress)
                .ConfigureAwait(true);
            RefreshBackups();
            BusyMessage = $"Backup saved: {info.FileName}";
        }).ConfigureAwait(true);
    }

    private void RefreshBackups()
    {
        Backups.Clear();
        foreach (var b in _backups.ListBackups()) Backups.Add(b);
    }

    private async Task SaveSettingsAsync()
    {
        await _settings.SaveAsync().ConfigureAwait(true);
        BusyMessage = "Settings saved. They take effect the next time Android starts.";
    }

    private void RefreshLogs() =>
        LogText = string.Join(Environment.NewLine, _log.ReadRecent(SelectedLogChannel, 400));

    private async Task SampleResourcesAsync()
    {
        try
        {
            var snapshot = await _monitor.SampleAsync().ConfigureAwait(true);
            ResourceSummary = State == RuntimeState.Running
                ? $"Android RAM {snapshot.HostRuntimeRamMB:n0} MB   •   PortableDroid {snapshot.UiRamMB:n0} MB   •   Network {(snapshot.NetworkConnected ? "connected" : "off")}"
                : $"PortableDroid {snapshot.UiRamMB:n0} MB";
        }
        catch
        {
            // sampling is best-effort
        }
    }

    private async Task RunBusyAsync(string title, Func<IProgress<string>, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyMessage = title + "...";
        var progress = new Progress<string>(m => BusyMessage = m);

        try
        {
            await action(progress).ConfigureAwait(true);
        }
        catch (PortableDroidException ex)
        {
            BusyMessage = ex.Error.What;
            ShowError(ex.Error);
        }
        catch (Exception ex)
        {
            var error = PortableDroidError.Unexpected(title.ToLowerInvariant(), ex);
            _log.Error("core", title + " failed.", ex);
            BusyMessage = error.What;
            ShowError(error);
        }
        finally
        {
            IsBusy = false;
            RefreshLogs();
        }
    }

    private void OnStateChanged(object? sender, RuntimeState state)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            State = state;
            StatusText = state switch
            {
                RuntimeState.Running => string.IsNullOrEmpty(AndroidVersion)
                    ? "Android is running"
                    : $"Android {AndroidVersion} is running",
                RuntimeState.Starting => "Android is starting",
                RuntimeState.Stopping => "Android is shutting down",
                RuntimeState.Crashed => "Android stopped unexpectedly — your data is safe",
                RuntimeState.Error => "Android could not start",
                _ => "Android is stopped"
            };
        });
    }

    private void OnErrorRaised(object? sender, PortableDroidError error) =>
        Application.Current?.Dispatcher.Invoke(() => ShowError(error));

    private static void ShowError(PortableDroidError error) =>
        MessageBox.Show(error.ToUserMessage(), "PortableDroid", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            // Explorer only; never launches a user-supplied file (spec §37).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { path },
                UseShellExecute = false
            });
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _runtime.StateChanged -= OnStateChanged;
        _runtime.ErrorRaised -= OnErrorRaised;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task>? _asyncExecute;
    private readonly Action<object?>? _execute;
    private readonly Predicate<object?>? _canExecute;
    private bool _running;

    public RelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        _asyncExecute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try
        {
            _running = true;
            CommandManager.InvalidateRequerySuggested();

            if (_asyncExecute is not null) await _asyncExecute(parameter);
            else _execute?.Invoke(parameter);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
