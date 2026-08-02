using System.Windows;
using System.Windows.Input;

namespace GptController.Views;

public partial class TextPromptDialog : Window
{
    public TextPromptDialog(
        string title,
        string label,
        string initialValue)
    {
        InitializeComponent();
        Title = title;
        DialogTitleText.Text = title;
        FieldLabelText.Text = label;
        ValueTextBox.Text = initialValue;
        UpdateSaveButton();
    }

    public string Value => ValueTextBox.Text.Trim();

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ValueTextBox.Focus();
        ValueTextBox.SelectAll();
    }

    private void TitleBar_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The pointer can be released before WPF enters DragMove.
        }
    }

    private void ValueTextBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateSaveButton();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueTextBox.Text))
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private void UpdateSaveButton()
    {
        if (SaveButton is not null)
        {
            SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(ValueTextBox.Text);
        }
    }
}
