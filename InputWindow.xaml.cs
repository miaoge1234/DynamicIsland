using System.Windows;
using System.Windows.Input;

namespace DynamicIsland;

/// <summary>一个很小的单行输入框（加待办用），外观和灵动岛一致。</summary>
public partial class InputWindow : Window
{
    public InputWindow(string title, string prompt, string initial = "")
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public Action<string>? Confirmed { get; set; }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        string text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            Close();
            return;
        }

        Confirmed?.Invoke(text);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
