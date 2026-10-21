using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace SqlBackup.Desktop;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        var assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "Unknown";
        Version.Text = "Version " + version.Split('+')[0];
    }

    private void AuthorWebsite_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://bujarademi.com") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DocumentationStatus.Text = "Open https://bujarademi.com in your browser.";
        }

        e.Handled = true;
    }

    private void Docs_Click(object sender, RoutedEventArgs e)
    {
        const string documentationUrl = "https://github.com/bujar-ademi/SqlBackup/tree/main/docs";
        try
        {
            Process.Start(new ProcessStartInfo(documentationUrl) { UseShellExecute = true });
            DocumentationStatus.Text = "Online documentation opened.";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DocumentationStatus.Text = "Open the project's docs folder on GitHub in your browser.";
        }
    }
}