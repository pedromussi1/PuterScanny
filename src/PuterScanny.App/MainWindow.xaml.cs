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
        _vm.TabRequested += OnTabRequested;
        _vm.Chat.QuestionAsked += (_, question) => Dispatcher.BeginInvoke(() => ScrollToQuestion(question), System.Windows.Threading.DispatcherPriority.Loaded);
        if (_vm.IsElevated) Title = "PuterScanny (Administrator)";
        ShowTheme();
        AppTheme.Changed += (_, _) => ShowTheme();
        Loaded += async (_, _) =>
        {
            if (App.StartupRequest is { } request) await _vm.StartFromCommandLineAsync(request);
        };
    }

    private void OnTabRequested(object? sender, string tab)
    {
        if (tab == MainViewModel.AskTab)
        {
            ResultsTabs.SelectedItem = AskTabItem;
            Dispatcher.BeginInvoke(() => ChatInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        else if (tab == MainViewModel.FoldersTab)
        {
            ResultsTabs.SelectedItem = FoldersTabItem;
            // Scroll the selected item into view once the tree has made its rows.
            Dispatcher.BeginInvoke(BringSelectedNodeIntoView, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Puts the question just asked at the top of the conversation, so its answer reads from the start
    /// (scrolling to the bottom would show only the end of a long answer, or look like nothing happened).</summary>
    private void ScrollToQuestion(ChatMessage question)
    {
        ChatScroll.UpdateLayout();
        if (ChatMessages.ItemContainerGenerator.ContainerFromItem(question) is not FrameworkElement container) { ChatScroll.ScrollToEnd(); return; }
        double top = container.TranslatePoint(new Point(0, 0), ChatScroll).Y + ChatScroll.VerticalOffset;
        ChatScroll.ScrollToVerticalOffset(Math.Max(0, top - 4));
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e) => AppTheme.Set(ThemeToggle.IsChecked == true);

    private void ShowTheme()
    {
        bool dark = AppTheme.IsDark;
        ThemeToggle.IsChecked = dark;
        ThemeIcon.Text = dark ? "" : ""; // sun / moon
        ThemeToggle.ToolTip = dark ? "Switch to light mode" : "Switch to dark mode";
    }

    private void BringSelectedNodeIntoView()
    {
        if (_vm.SelectedNode is not { } target) return;
        FolderTree.UpdateLayout();
        TreeViewItem? Find(ItemsControl parent)
        {
            foreach (var data in parent.Items)
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(data) is not TreeViewItem item) continue;
                if (ReferenceEquals(data, target)) return item;
                if (item.IsExpanded)
                {
                    item.UpdateLayout();
                    if (Find(item) is { } found) return found;
                }
            }
            return null;
        }
        if (Find(FolderTree) is { } container)
        {
            container.IsSelected = true;
            container.BringIntoView();
            container.Focus();
        }
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
