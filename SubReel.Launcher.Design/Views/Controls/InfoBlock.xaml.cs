using System.Windows;
using System.Windows.Controls;

namespace SubReel.Launcher.Design.Views.Controls;

public partial class InfoBlock : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(InfoBlock), new PropertyMetadata(""));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(InfoBlock), new PropertyMetadata(""));

    public InfoBlock() => InitializeComponent();

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
}
