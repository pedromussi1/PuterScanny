using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PuterScanny.App.ViewModels;

namespace PuterScanny.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.ConfirmCleanup = rows =>
        {
            var dialog = new CleanupConfirmWindow(rows, _vm.IsAdmin) { Owner = this };
            return dialog.ShowDialog() == true ? (dialog.Method, dialog.CreateRestorePoint) : null;
        };
        _vm.LoadHistory();
        if (_vm.IsElevated) Title = "PuterScanny (Administrator)";
        Loaded += async (_, _) =>
        {
            if (App.StartupRequest is { } request) await _vm.StartFromCommandLineAsync(request);
        };
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue as TreeNodeViewModel;

    private void LargestFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: FileRow row }) _vm.RevealFileCommand.Execute(row.Path);
    }

    private void RecentFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: RecentFileRow row }) _vm.RevealFileCommand.Execute(row.Path);
    }

    private void CleanupRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: CleanupRow row }) _vm.RevealFileCommand.Execute(row.Path);
    }

    private void Treemap_Opened(object? sender, PuterScanny.App.Controls.TreemapNode node) => _vm.OpenTreemapNode(node);

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = DataContext;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.CancelIfScanning();
        base.OnClosing(e);
    }

    private void OpenLocalAi_Click(object sender, RoutedEventArgs e)
    {
        new LocalAiWindow(_vm.Ai) { Owner = this }.ShowDialog();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
