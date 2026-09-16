using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using WinMtr.Desktop.Services;

namespace WinMtr.Desktop.Views;

internal sealed class AboutWindow : Window
{
    internal AboutWindow(AppCredit? credit = null)
    {
        credit ??= AppCredit.Default;
        Title = $"About {AppCredit.ProductName}";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var repository = new Button
        {
            Content = credit.RepositoryUrl,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        repository.Click += async (_, _) =>
        {
            try
            {
                if (Launcher is { } launcher)
                    await launcher.LaunchUriAsync(new Uri(credit.RepositoryUrl));
            }
            catch (Exception)
            {
                // A missing browser must not disturb the shell.
            }
        };
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock { Text = AppCredit.ProductName, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = credit.VersionLabel, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = $"by {credit.Author}", HorizontalAlignment = HorizontalAlignment.Center },
                repository,
            },
        };
    }
}
