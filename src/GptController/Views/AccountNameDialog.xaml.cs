using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GptController.Views;

public partial class AccountNameDialog : Window
{
    public AccountNameDialog(string nickname)
    {
        InitializeComponent();
        NicknameTextBox.Text = nickname;
        UpdateSaveButton();
    }

    public string Value => NicknameTextBox.Text.Trim();

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        NicknameTextBox.Focus();
        NicknameTextBox.SelectAll();
    }

    private void Nickname_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SaveButton is not null)
        {
            UpdateSaveButton();
        }
    }

    private void UpdateSaveButton() => SaveButton.IsEnabled = Value.Length is > 0 and <= 40;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SaveButton.IsEnabled) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The pointer can be released before WPF enters DragMove.
        }
    }
}
