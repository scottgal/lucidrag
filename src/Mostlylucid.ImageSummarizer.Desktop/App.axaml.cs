using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Mostlylucid.ImageSummarizer.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // Load image from command-line if provided (for shell integration)
            if (Program.Args.Length > 0)
            {
                mainWindow.LoadFromArgs(Program.Args);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
