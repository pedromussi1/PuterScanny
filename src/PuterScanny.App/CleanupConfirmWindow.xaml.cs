using System.Windows;
using PuterScanny.App.ViewModels;
using PuterScanny.Cleanup;

namespace PuterScanny.App;

/// <summary>Shows exactly what will be removed and asks how, before anything happens.</summary>
public partial class CleanupConfirmWindow : Window
{
    public CleanupConfirmWindow(IReadOnlyList<CleanupRow> rows, bool isAdmin)
    {
        InitializeComponent();
        long bytes = rows.Sum(r => r.Size);
        Headline.Text = $"Clean up {rows.Count:N0} item{(rows.Count == 1 ? "" : "s")} ({Display.Size(bytes)})?";
        Items.ItemsSource = rows;

        int review = rows.Count(r => r.VerdictValue == PuterScanny.Core.Models.Verdict.Review);
        if (review > 0)
        {
            ReviewWarning.Text = $"{review:N0} of these are marked \"Review\": PuterScanny thinks they can go, but you should be sure you don't need them.";
            ReviewWarning.Visibility = Visibility.Visible;
        }

        RestorePoint.IsEnabled = isAdmin;
        RestorePointNote.Text = isAdmin
            ? "Lets you roll Windows back if something goes wrong. Your clean-up can also be undone from PuterScanny either way."
            : "Needs administrator rights (tick Deep scan before scanning). Your clean-up can still be undone from PuterScanny.";
    }

    public RemovalMethod Method => UseQuarantine.IsChecked == true ? RemovalMethod.Quarantine : RemovalMethod.RecycleBin;

    public bool CreateRestorePoint => RestorePoint.IsChecked == true && RestorePoint.IsEnabled;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
