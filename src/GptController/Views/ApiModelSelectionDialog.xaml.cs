using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using GptController.Models;

namespace GptController.Views;

public partial class ApiModelSelectionDialog : Window
{
    private readonly ObservableCollection<ModelListItem> _models = [];
    private readonly Func<CancellationToken, Task<IReadOnlyList<ApiModelDescriptor>>>? _refresh;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ICollectionView _view;
    private readonly string _currentModel;

    public ApiModelSelectionDialog(
        string providerName,
        IReadOnlyList<ApiModelDescriptor> models,
        string selectedModel,
        bool requiresPaidValidation,
        Func<CancellationToken, Task<IReadOnlyList<ApiModelDescriptor>>>? refresh = null)
    {
        _currentModel = selectedModel;
        InitializeComponent();
        ProviderNameText.Text = providerName;
        FeeNoticeBorder.Visibility = requiresPaidValidation
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyButton.Content = requiresPaidValidation ? "验证并应用" : "应用模型";
        _refresh = refresh;
        RefreshButton.IsEnabled = refresh is not null;
        ReplaceModels(models);
        _view = CollectionViewSource.GetDefaultView(_models);
        _view.Filter = FilterModel;
        ModelsList.ItemsSource = _view;
        ModelsList.SelectedItem = _models.FirstOrDefault(item =>
            string.Equals(item.Id, selectedModel, StringComparison.Ordinal));
        UpdateState();
    }

    public string? Value => (ModelsList.SelectedItem as ModelListItem)?.Id;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        SearchTextBox.Focus();
        ModelsList.ScrollIntoView(ModelsList.SelectedItem);
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private bool FilterModel(object item) =>
        item is ModelListItem model &&
        (string.IsNullOrWhiteSpace(SearchTextBox.Text) ||
         model.Id.Contains(SearchTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
         model.DisplayName.Contains(SearchTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));

    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _view?.Refresh();
        ResultCountText.Text = $"{_view?.Cast<object>().Count() ?? _models.Count} 个模型";
    }

    private void Selection_Changed(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => UpdateState();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refresh is null)
        {
            return;
        }

        var selected = Value;
        RefreshButton.IsEnabled = false;
        RefreshStatusText.Text = "正在获取模型…";
        try
        {
            var models = await _refresh(_lifetime.Token);
            ReplaceModels(models);
            _view.Refresh();
            ModelsList.SelectedItem = _models.FirstOrDefault(item =>
                string.Equals(item.Id, selected, StringComparison.Ordinal));
            RefreshStatusText.Text = $"已获取 {_models.Count} 个模型";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the dialog cancels an in-flight refresh.
        }
        catch (Exception exception)
        {
            RefreshStatusText.Text = exception.Message;
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private void ReplaceModels(IReadOnlyList<ApiModelDescriptor> models)
    {
        _models.Clear();
        foreach (var model in models
                     .DistinctBy(item => item.Id, StringComparer.Ordinal)
                     .OrderBy(item => item.IsSnapshot)
                     .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            _models.Add(new ModelListItem(
                model.Id,
                model.EffectiveDisplayName,
                DescribeMetadata(model),
                model.IsSnapshot,
                string.Equals(model.Id, _currentModel, StringComparison.Ordinal)));
        }

        ResultCountText.Text = $"{_models.Count} 个模型";
    }

    private static string DescribeMetadata(ApiModelDescriptor model) => string.Join(" · ",
        new[]
        {
            model.Id,
            model.ContextWindowTokens is { } context ? $"上下文 {context:N0}" : null,
            model.MaxOutputTokens is { } output ? $"输出上限 {output:N0}" : null,
            model.InputModalities.Contains("image") ? "支持图像输入" : null,
            model.Features.Contains("function-calling") ? "工具调用" : null,
            model.ReasoningEfforts.Count > 0 ? "推理 " + string.Join(" / ", model.ReasoningEfforts) : null
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private void UpdateState()
    {
        if (ApplyButton is not null)
        {
            ApplyButton.IsEnabled = ModelsList?.SelectedItem is ModelListItem;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Value is not null)
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
            // Pointer released before WPF entered DragMove.
        }
    }

    private sealed record ModelListItem(string Id, string DisplayName, string MetadataText, bool IsSnapshot, bool IsCurrent);
}
