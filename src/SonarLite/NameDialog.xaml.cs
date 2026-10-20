using System.Windows;
using System.Windows.Controls;

namespace SonarLite;

/// <summary>Asks for a preset name; <paramref name="validate"/> returns an error message or null.</summary>
public partial class NameDialog : Window
{
    private readonly Func<string, string?> _validate;

    public NameDialog(string title, string initial, Func<string, string?> validate)
    {
        InitializeComponent();
        _validate = validate;
        Title = title;
        NameBox.Text = initial;
        NameBox.SelectAll();
    }

    public string Result => NameBox.Text.Trim();

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var error = _validate(Result);
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = error is null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
