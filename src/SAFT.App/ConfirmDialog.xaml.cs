using System.Windows;
using System.Windows.Controls;

namespace SAFT.App;

/// <summary>
/// A small Yes/No confirmation dialog with fully custom button wording (WPF's built-in
/// MessageBox only offers fixed "Yes"/"No"/"OK"/"Cancel" labels, and the task's confirmations
/// call for specific, exact button text).
/// </summary>
public partial class ConfirmDialog : Window
{
    public bool Result { get; private set; }

    public ConfirmDialog(string message, string yesText, string noText)
    {
        InitializeComponent();
        MessageText.Text = message;
        YesButton.Content = new TextBlock { Text = yesText, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        NoButton.Content = new TextBlock { Text = noText, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    }

    private void OnYesClick(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnNoClick(object sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}
