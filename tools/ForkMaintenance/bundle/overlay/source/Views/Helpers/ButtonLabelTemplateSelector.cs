using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Helpers
{
    public sealed class ButtonLabelTemplateSelector : DataTemplateSelector
    {
        public DataTemplate LabelTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            return item is string ? LabelTemplate : null;
        }
    }
}
