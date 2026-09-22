using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Excluder.Core;

namespace Excluder.App;

public partial class MainWindow
{
    private void RefreshServerView()
    {
        RangesText.Text = string.Join("\n", catalog.Finland.Ranges);
        RangeCount.Text = $"{catalog.Finland.Ranges.Length} ranges active · {catalog.Updated}";
        ExecutableText.Text = GameFound ? settings.OverwatchPath : "Overwatch executable not found.";
        ExecutableText.ToolTip = settings.OverwatchPath;
        LocateButton.Content = GameFound ? "Change" : "Locate";
    }
    private async void LocateGame(object sender, RoutedEventArgs e)
    {
        if (busy || installingUpdate) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Locate Overwatch.exe", Filter = "Overwatch executable|Overwatch.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (!OverwatchLocator.IsValid(picker.FileName)) { ShowError("Select an existing Overwatch.exe file."); return; }
        await ApplyMode(settings.Mode, false, settings with { OverwatchPath = picker.FileName });
    }
    private async Task BackgroundChecks()
    {
        // The UI and cached server list are already available. These two independent requests never gate startup.
        var catalogTask = RefreshCatalog();
        var releaseTask = settings.AutomaticUpdates ? CheckForUpdates() : Task.CompletedTask;
        await Task.WhenAll(catalogTask, releaseTask);
    }
    private async Task RefreshCatalog()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var candidate = await RemoteServices.CheckCatalog(catalog, timeout.Token);
            if (candidate == null) return;
            while (busy || installingUpdate) await Task.Delay(100, lifetime.Token);
            if (GameFound && (App.Elevated || App.Preview))
                await ApplyMode(settings.Mode, false, serverUpdate: candidate);
            else
            {
                catalogStore.Save(candidate); catalog = candidate; RefreshServerView();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log.Write("Server configuration update ignored: " + error); }
    }
    private void AutomaticUpdatesChanged(object sender, RoutedEventArgs e) => SavePreference(settings with { AutomaticUpdates = AutomaticUpdatesToggle.IsChecked == true });
    private async void CheckUpdates(object sender, RoutedEventArgs e) => await CheckForUpdates();
    private async Task CheckForUpdates()
    {
        if (checkingUpdates || installingUpdate) return;
        if (App.Preview) { UpdateStatus.Text = "Update checks are disabled in UI preview."; return; }
        checkingUpdates = true; CheckUpdatesButton.IsEnabled = false; UpdateStatus.Text = "Checking…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            availableRelease = await RemoteServices.CheckRelease(timeout.Token);
            UpdateStatus.Text = availableRelease == null ? "You're up to date" : $"v{availableRelease.Version} available";
            HomeUpdate.Visibility = InstallUpdateButton.Visibility = availableRelease == null ? Visibility.Collapsed : Visibility.Visible;
            if (availableRelease != null) HomeUpdate.Content = $"Update · v{availableRelease.Version}";
            if (updateItem != null) updateItem.Text = availableRelease == null ? "Check for updates" : $"Update to v{availableRelease.Version}";
        }
        catch (Exception error)
        {
            if (!lifetime.IsCancellationRequested) { UpdateStatus.Text = "Couldn't check for updates. Try again later."; Log.Write("Update check: " + error); }
        }
        finally { checkingUpdates = false; CheckUpdatesButton.IsEnabled = true; }
    }
    private async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        if (busy || installingUpdate || availableRelease == null || App.Preview) return;
        installingUpdate = true; SetBusy(true); HomeUpdate.IsEnabled = false;
        try
        {
            UpdateStatus.Text = "Downloading and verifying update…";
            // Refresh metadata immediately before downloading; never install an old in-memory URL blindly.
            var release = await RemoteServices.CheckRelease(lifetime.Token);
            if (release == null) { UpdateStatus.Text = "You're up to date"; return; }
            var stage = await UpdateInstaller.Prepare(release, lifetime.Token);
            UpdateStatus.Text = "Restarting to install…";
            UpdateInstaller.Launch(stage);
            SetBusy(false); Exit();
        }
        catch (Exception error)
        {
            Log.Write("Update install: " + error);
            UpdateStatus.Text = error is InvalidDataException ? "Update verification failed." : "Couldn't install update.";
            Reveal(); OpenSettings(this, new RoutedEventArgs());
        }
        finally { installingUpdate = false; SetBusy(false); HomeUpdate.IsEnabled = true; }
    }
    private async void DetectServer(object sender, RoutedEventArgs e)
    {
        if (detecting || !GameFound) { if (!GameFound) DiagnosticText.Text = "Locate Overwatch.exe first."; return; }
        if (App.Preview) { DiagnosticText.Text = "Live diagnostics are disabled in UI preview."; return; }
        detecting = true; DetectButton.IsEnabled = false; CopyDiagnosticButton.Visibility = Visibility.Collapsed;
        DiagnosticHeading.Visibility = DiagnosticDetails.Visibility = Visibility.Collapsed;
        DiagnosticText.Text = "Listening for 5 seconds… Keep the match running.";
        try
        {
            diagnostic = await ServerDiagnostics.Detect(settings.OverwatchPath!, catalog, lifetime.Token);
            DiagnosticHeading.Text = diagnostic.Heading;
            DiagnosticDetails.Text = diagnostic.Details;
            DiagnosticText.Text = diagnostic.Explanation;
            DiagnosticHeading.Visibility = DiagnosticDetails.Visibility = diagnostic.Candidate == null ? Visibility.Collapsed : Visibility.Visible;
            CopyDiagnosticButton.Visibility = diagnostic.Candidate == null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { DiagnosticText.Text = "Couldn't detect a server. See log and try again during a match."; Log.Write("Diagnostics: " + error); }
        finally { detecting = false; DetectButton.IsEnabled = true; }
    }
    private void CopyDiagnostic(object sender, RoutedEventArgs e)
    {
        if (diagnostic?.Candidate == null) return;
        try { Clipboard.SetText(diagnostic.CopyText); }
        catch (Exception error) { Log.Write(error.ToString()); DiagnosticText.Text += "\nCouldn't copy to clipboard."; }
    }
    private void OpenDiscord(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://discord.gg/143aimclub") { UseShellExecute = true }); }
        catch (Exception error) { ShowError("Couldn't open the 143 Aim Club Discord invite.\n" + error.Message, error); }
    }
    private bool ConfirmRemoval()
    {
        var dialog = new Window { Owner = this, Title = "Remove firewall rules?", Width = 348, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#18181C")!, FontFamily = FontFamily };
        var body = new StackPanel { Margin = new Thickness(22) };
        body.Children.Add(new TextBlock { Text = "Remove firewall rules?", FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "143 OW Switch will no longer affect matchmaking until the rules are recreated.\n\nStartup will be disabled and the app will exit.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 20) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var remove = new Button { Content = "Remove", Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#CC8E96")! };
        cancel.Click += (_, _) => dialog.DialogResult = false; remove.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(remove); body.Children.Add(actions); dialog.Content = body;
        return dialog.ShowDialog() == true;
    }
}
