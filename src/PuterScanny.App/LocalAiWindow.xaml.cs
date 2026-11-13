using System.Windows;
using PuterScanny.App.ViewModels;

namespace PuterScanny.App;

/// <summary>Turning the local AI on, downloading or choosing a model, and explaining the current scan in one go.</summary>
public partial class LocalAiWindow : Window
{
    public LocalAiWindow(AiController ai)
    {
        InitializeComponent();
        DataContext = ai;
        ai.Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
