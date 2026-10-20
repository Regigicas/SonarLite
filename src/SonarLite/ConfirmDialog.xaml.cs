using System.Windows;

namespace SonarLite;

/// <summary>Yes/cancel question with the app's theme (MessageBox ignores it); Cancel is the default.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
