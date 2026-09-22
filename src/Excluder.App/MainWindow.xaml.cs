using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Excluder.Core;
using Forms = System.Windows.Forms;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Excluder.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore store = new(App.Preview ? Path.Combine(Path.GetTempPath(), "143OWServerSwitch.Preview") : Log.DirectoryPath);
    private readonly FirewallController firewall = new(App.Preview ? new PreviewFirewall() : new WindowsFirewall());
    private Settings settings = new();
    private Forms.NotifyIcon? tray;
    private Forms.ToolStripMenuItem? soloItem, partyItem, startupItem;
    private readonly Icon soloIcon = MakeIcon(true), partyIcon = MakeIcon(false), unknownIcon = MakeIcon(false, true);
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool busy, exiting, verified, configLoaded;

    public MainWindow() { InitializeComponent(); Closing += OnClosing; }

    public async void Initialize()
    {
        Log.Write(App.Preview ? "UI preview started (no system changes)" : "App started");
        try
        {
            settings = store.Load(); configLoaded = true;
            if (!App.Preview)
            {
                // The registry is authoritative for startup; refresh the path after moving/updating the exe.
                settings.RunOnStartup = StartupService.Enabled;
                if (settings.RunOnStartup) StartupService.Set(true);
            }
            RestorePosition(); SyncToggles();
        }
        catch (Exception e) { ShowError("Couldn't load settings. Fix config.json before retrying.\n" + e.Message, e); }
        CreateTray();
        if (!settings.StartMinimized || !configLoaded || (!App.Preview && !App.Elevated)) Reveal();
        if (configLoaded) await ApplyMode(settings.Mode, false);
        monitor.Tick += async (_, _) => await RefreshHealth();
        monitor.Start();
    }

    public void Reveal() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void RestorePosition()
    {
        if (settings.WindowLeft is not double left || settings.WindowTop is not double top || !double.IsFinite(left) || !double.IsFinite(top)) return;
        // WPF uses device-independent units. Translate each monitor's working area before accepting saved coordinates.
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            var point = new NativePoint { X = area.Left, Y = area.Top };
            var handle = MonitorFromPoint(point, 2);
            uint dpiX = 96, dpiY = 96;
            try { GetDpiForMonitor(handle, 0, out dpiX, out dpiY); } catch { }
            var scale = dpiX / 96d;
            if (left >= area.Left / scale && top >= area.Top / scale &&
                left + Width <= area.Right / scale && top + 48 <= area.Bottom / scale)
            { WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top; return; }
        }
    }
    private void CreateTray()
    {
        tray = new Forms.NotifyIcon { Icon = unknownIcon, Text = "143 Server Excluder — checking", Visible = true };
        var menu = new Forms.ContextMenuStrip { BackColor = System.Drawing.Color.FromArgb(24,24,28), ForeColor = System.Drawing.Color.White, ShowImageMargin = false };
        menu.Items.Add(new Forms.ToolStripMenuItem("143 OW Server Switch") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        soloItem = new Forms.ToolStripMenuItem("SOLO — Block GEN1", null, async (_, _) => await ApplyMode(Mode.Solo));
        partyItem = new Forms.ToolStripMenuItem("PARTY — Allow GEN1", null, async (_, _) => await ApplyMode(Mode.Party));
        menu.Items.Add(soloItem); menu.Items.Add(partyItem); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) => Reveal());
        startupItem = new Forms.ToolStripMenuItem("Run on startup", null, (_, _) => SetStartup(!settings.RunOnStartup));
        menu.Items.Add(startupItem); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { if (!busy) Exit(); });
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Reveal(); };
        SyncToggles();
    }
    private async Task ApplyMode(Mode mode, bool notify = true)
    {
        if (busy) return;
        if (!configLoaded) { ShowError("Settings have not loaded. Fix config.json and retry."); return; }
        if (!App.Preview && !App.Elevated)
        { ShowError("Administrator permission is required to modify Windows Firewall."); return; }
        SetBusy(true);
        var next = settings with { Mode = mode };
        try
        {
            await Task.Run(() => firewall.Apply(mode, () => store.Save(next)));
            settings = next; verified = true;
            ErrorPanel.Visibility = Visibility.Collapsed;
            var active = await Task.Run(() => firewall.IsActive);
            Render(active);
            Log.Write("Mode changed: " + mode);
            if (notify && settings.Notifications && tray != null)
                tray.ShowBalloonTip(2000, "143 OW Server Switch", active || mode == Mode.Party
                    ? mode == Mode.Solo ? "SOLO enabled. GEN1 ranges are blocked." : "PARTY enabled. App rules are disabled."
                    : "SOLO rules saved, but Windows Firewall or local policy is inactive.", Forms.ToolTipIcon.Info);
        }
        catch (Exception e)
        {
            verified = false; Render(false);
            ShowError("Couldn't update Windows Firewall.\n" + e.Message, e);
        }
        finally { SetBusy(false); }
    }
    private async Task RefreshHealth()
    {
        if (busy || !configLoaded) return;
        SetBusy(true);
        try
        {
            var health = await Task.Run(() => (Matches: firewall.Check(settings.Mode), Active: firewall.IsActive));
            verified = health.Matches; Render(health.Active);
        }
        catch (Exception e) { verified = false; Render(false); Log.Write(e.ToString()); }
        finally { SetBusy(false); }
    }
    private void Render(bool active)
    {
        bool solo = settings.Mode == Mode.Solo;
        Status.Text = !verified ? "CHECK RULES" : solo && !active ? "NOT ACTIVE" : solo ? "BLOCKED" : "AVAILABLE";
        Subtitle.Text = !verified ? "Rules differ from your saved mode" : solo ? active ? "Finland server excluded" : "Enable Windows Firewall to block GEN1" : "Normal matchmaking";
        Health.Text = App.Preview ? "PREVIEW · no system changes" : !verified ? "Click SOLO or PARTY to repair" : !active ? "Firewall off or managed by policy" : solo ? "Firewall active" : "App blocking disabled";
        Animate(SoloButton, verified && solo ? "#60519B" : "#202026");
        Animate(PartyButton, verified && !solo ? "#60519B" : "#202026");
        if (tray != null)
        {
            tray.Icon = !verified || (solo && !active) ? unknownIcon : solo ? soloIcon : partyIcon;
            tray.Text = "143 OW Server Switch — " + (!verified ? "CHECK RULES" : solo && !active ? "NOT ACTIVE" : solo ? "SOLO" : "PARTY");
            soloItem!.Checked = verified && solo; partyItem!.Checked = verified && !solo;
        }
    }
    private static void Animate(Button button, string hex)
    {
        var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var old = (button.Background as SolidColorBrush)?.Color ?? color;
        var brush = new SolidColorBrush(old); button.Background = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(140)));
    }
    private void SetBusy(bool value)
    {
        busy = value; SoloButton.IsEnabled = PartyButton.IsEnabled = !value;
        if (soloItem != null) soloItem.Enabled = partyItem!.Enabled = !value;
        SettingsPanel.IsEnabled = !value;
        StartupToggle.IsEnabled = MinimizedToggle.IsEnabled = !value;
        if (startupItem != null) startupItem.Enabled = !value;
    }
    private void ShowError(string message, Exception? error = null)
    {
        if (error != null) Log.Write(error.ToString());
        ErrorText.Text = message; AdminButton.Visibility = App.Elevated || App.Preview ? Visibility.Collapsed : Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Visible; Reveal();
    }
    private void SyncToggles()
    {
        StartupToggle.IsChecked = StartupSettings.IsChecked = settings.RunOnStartup;
        MinimizedToggle.IsChecked = MinimizedSettings.IsChecked = settings.StartMinimized;
        NotificationsToggle.IsChecked = settings.Notifications;
        if (startupItem != null) startupItem.Checked = settings.RunOnStartup;
    }
    private void SavePreference(Settings next)
    {
        if (!configLoaded) { SyncToggles(); return; }
        try { store.Save(next); settings = next; }
        catch (Exception e) { ShowError("Couldn't save settings.\n" + e.Message, e); }
        SyncToggles();
    }
    private void SetStartup(bool enabled)
    {
        if (!configLoaded || busy) return;
        bool old = settings.RunOnStartup;
        try
        {
            if (!App.Preview) StartupService.Set(enabled);
            var next = settings with { RunOnStartup = enabled };
            store.Save(next); settings = next;
        }
        catch (Exception e)
        {
            try { if (!App.Preview) StartupService.Set(old); } catch (Exception rollback) { Log.Write(rollback.ToString()); }
            ShowError("Couldn't change startup settings.\n" + e.Message, e);
        }
        SyncToggles();
    }
    private void StartupChanged(object sender, RoutedEventArgs e) => SetStartup(((CheckBox)sender).IsChecked == true);
    private void MinimizedChanged(object sender, RoutedEventArgs e) => SavePreference(settings with { StartMinimized = ((CheckBox)sender).IsChecked == true });
    private void NotificationsChanged(object sender, RoutedEventArgs e) => SavePreference(settings with { Notifications = NotificationsToggle.IsChecked == true });
    private async void SelectSolo(object sender, RoutedEventArgs e) => await ApplyMode(Mode.Solo);
    private async void SelectParty(object sender, RoutedEventArgs e) => await ApplyMode(Mode.Party);
    private void HideWindow(object sender, RoutedEventArgs e) { SavePosition(); Hide(); }
    private void DragTitle(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is not Button && e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void OpenSettings(object sender, RoutedEventArgs e) { Home.Visibility = Visibility.Collapsed; SettingsPanel.Visibility = Visibility.Visible; Height = 574; }
    private void CloseSettings(object sender, RoutedEventArgs e) { SettingsPanel.Visibility = Visibility.Collapsed; Home.Visibility = Visibility.Visible; Height = 398; }
    private void DismissError(object sender, RoutedEventArgs e) => ErrorPanel.Visibility = Visibility.Collapsed;
    private void RestartAdmin(object sender, RoutedEventArgs e) { if (App.Elevate(false)) Exit(); }
    private async void RetryMode(object sender, RoutedEventArgs e)
    {
        if (!configLoaded)
        {
            try { settings = store.Load(); configLoaded = true; SyncToggles(); }
            catch (Exception error) { ShowError("Couldn't load settings.\n" + error.Message, error); return; }
        }
        await ApplyMode(settings.Mode, false);
    }
    private async void CheckRules(object sender, RoutedEventArgs e)
    {
        await RefreshHealth();
        MessageBox.Show(this, verified ? "Everything looks good.\n" + Health.Text : "Rules need repair. Click SOLO or PARTY to restore them.", "Check rules", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void OpenFirewall(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("mmc.exe", "wf.msc") { UseShellExecute = true }); }
        catch (Exception error) { ShowError(error.Message, error); }
    }
    private async void RemoveRules(object sender, RoutedEventArgs e)
    {
        if (busy || MessageBox.Show(this, "Remove this app's firewall rules, disable its startup entry and exit?\nYour other firewall rules will remain. Rules are recreated if you launch the app again.", "Remove firewall rules", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            await Task.Run(firewall.RemoveOwned);
            if (!App.Preview) StartupService.Set(false);
            settings = settings with { Mode = Mode.Party, RunOnStartup = false };
            store.Save(settings); Log.Write("Managed rules removed"); Exit();
        }
        catch (Exception error) { verified = false; Render(false); ShowError("Couldn't finish cleanup.\n" + error.Message, error); }
        finally { SetBusy(false); }
    }
    private void SavePosition()
    {
        if (!configLoaded || busy || !IsLoaded) return;
        SavePreference(settings with { WindowLeft = Left, WindowTop = Top });
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; SavePosition(); Hide(); } }
    private void Exit()
    {
        SavePosition(); exiting = true; monitor.Stop();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        soloIcon.Dispose(); partyIcon.Dispose(); unknownIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
    private static Icon MakeIcon(bool solo, bool unknown = false)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(System.Drawing.Color.Transparent);
        using var fill = new SolidBrush(System.Drawing.ColorTranslator.FromHtml(solo ? "#60519B" : "#555560"));
        graphics.FillEllipse(fill, 2, 2, 28, 28);
        using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2.5f);
        if (unknown) { graphics.DrawLine(pen, 16, 8, 16, 18); graphics.DrawEllipse(pen, 15, 23, 1, 1); }
        else if (solo) { graphics.DrawRectangle(pen, 10, 14, 12, 10); graphics.DrawArc(pen, 11, 7, 10, 13, 180, 180); }
        else { graphics.DrawLine(pen, 9, 16, 14, 21); graphics.DrawLine(pen, 14, 21, 24, 11); }
        var handle = bitmap.GetHicon();
        try { using var icon = System.Drawing.Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}

internal sealed class PreviewFirewall : IFirewallStore
{
    private readonly List<Rule> rules = [];
    public IReadOnlyList<Rule> Read() => rules.ToArray();
    public void Remove(string name) => rules.RemoveAll(r => r.Name == name);
    public void Add(Rule rule) => rules.Add(rule);
    public bool IsFirewallActive() => true;
}
