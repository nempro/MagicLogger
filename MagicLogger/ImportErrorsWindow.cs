using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MagicLogger;

internal sealed class ImportErrorsWindow : Window
{
    public ImportErrorSource? ErrorToRetry { get; private set; }
    public ImportErrorSource? ErrorToDismiss { get; private set; }

    public ImportErrorsWindow(IReadOnlyList<ImportErrorSource> errors, bool readOnly)
    {
        Title = "取込エラー";
        Width = 760;
        Height = 540;
        MinWidth = 560;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#211F1A");
        Foreground = Brush("#E7DDC9");

        var root = new DockPanel { Margin = new Thickness(20) };
        Content = root;
        var heading = new TextBlock
        {
            Text = "取込エラーのあるファイル",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var description = new TextBlock
        {
            Text = "件数は会話数ではなく、失敗した取込元ファイルの数です。通知を閉じても失敗履歴は残ります。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#BDB29E"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(description, Dock.Top);
        root.Children.Add(description);
        var close = new Button { Content = "閉じる", Padding = new Thickness(18, 7, 18, 7), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        var list = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        foreach (var error in errors)
        {
            var exists = File.Exists(error.Path);
            var zipNeedsReplacement = error.Error.Contains("ZIPファイルを読み取れませんでした", StringComparison.Ordinal);
            var availability = !exists ? "元ファイルが見つからないため再試行できません" :
                zipNeedsReplacement ? "ZIPを修復・差し替えた後に再試行できます" : "再試行できます";
            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var border = new Border
            {
                Background = Brush("#302B23"), BorderBrush = Brush("#5B5144"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5), Padding = new Thickness(14), Child = card
            };
            list.Children.Add(border);
            card.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(error.Path), FontSize = 17, FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#E5C78B")
            });
            AddDetail(card, "場所", error.Path);
            AddDetail(card, "失敗日時", error.FailedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            AddDetail(card, "理由", string.IsNullOrWhiteSpace(error.Error) ? "取込が完了していません" : error.Error);
            AddDetail(card, "再試行", availability);
            if (error.Dismissed) AddDetail(card, "通知", "閉じています（失敗履歴は保持）");

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            card.Children.Add(actions);
            var retry = new Button { Content = "このファイルを再試行", IsEnabled = exists && !readOnly,
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
            retry.Click += (_, _) => { ErrorToRetry = error; DialogResult = true; };
            actions.Children.Add(retry);
            var dismiss = new Button { Content = "通知を閉じる", IsEnabled = !error.Dismissed && !readOnly,
                Padding = new Thickness(12, 5, 12, 5) };
            dismiss.Click += (_, _) => { ErrorToDismiss = error; DialogResult = true; };
            actions.Children.Add(dismiss);
        }
    }

    private static void AddDetail(Panel parent, string label, string value)
    {
        parent.Children.Add(new TextBlock
        {
            Text = $"{label}: {value}", TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#D4C9B5"), Margin = new Thickness(0, 5, 0, 0)
        });
    }

    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
}
