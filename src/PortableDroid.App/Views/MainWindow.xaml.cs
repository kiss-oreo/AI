using System.Windows;
using PortableDroid.App.ViewModels;
using PortableDroid.Core.Apk;

namespace PortableDroid.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await _viewModel.InitialiseAsync();
    }

    /// <summary>Accepts a dragged APK only if it really looks like one (spec §15).</summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetApkPath(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!TryGetApkPath(e, out var path)) return;
        e.Handled = true;
        await _viewModel.InstallApkAsync(path!);
    }

    private static bool TryGetApkPath(DragEventArgs e, out string? path)
    {
        path = null;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return false;

        var candidate = files[0];
        if (!ApkInspector.LooksLikeApk(candidate)) return false;

        path = candidate;
        return true;
    }
}
