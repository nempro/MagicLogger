using System.Windows;

namespace MagicLogger;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Schema v", StringComparison.Ordinal) || ex.Message.Contains("schema version", StringComparison.Ordinal))
        {
            MessageBox.Show(ex.Message, "MagicLogger: DBの版が新しいため起動を中止しました", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
