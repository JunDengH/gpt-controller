using System.Windows;
using System.Windows.Controls;
using GptController.ViewModels;

namespace GptController.Views;

public sealed class ConnectionCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? OAuthTemplate { get; set; }

    public DataTemplate? ApiTemplate { get; set; }

    public override DataTemplate? SelectTemplate(
        object item,
        DependencyObject container) => item switch
        {
            AccountCardViewModel
            {
                CardKind: ConnectionCardKind.ApiProvider
            } => ApiTemplate,
            AccountCardViewModel => OAuthTemplate,
            _ => base.SelectTemplate(item, container)
        };
}
