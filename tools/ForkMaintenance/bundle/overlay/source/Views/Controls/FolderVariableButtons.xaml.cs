using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Views.Controls
{
    public partial class FolderVariableButtons : UserControl
    {
        public static readonly DependencyProperty TargetTextBoxProperty = DependencyProperty.Register(
            nameof(TargetTextBox), typeof(TextBox), typeof(FolderVariableButtons));

        public TextBox TargetTextBox
        {
            get => (TextBox)GetValue(TargetTextBoxProperty);
            set => SetValue(TargetTextBoxProperty, value);
        }

        public FolderVariableButtons() { InitializeComponent(); }

        private void InsertVariable_Click(object sender, RoutedEventArgs e)
        {
            if (TargetTextBox == null || !((sender as Button)?.DataContext is FolderPathVariable variable)) return;
            var start = TargetTextBox.SelectionStart;
            TargetTextBox.SelectedText = variable.Token;
            TargetTextBox.Select(start + variable.Token.Length, 0);
            TargetTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            TargetTextBox.Focus();
        }
    }
}
