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
    private readonly CatalogStore catalogStore;
    private ServerCatalog catalog = ServerCatalog.Bundled();
    private readonly CancellationTokenSource lifetime = new();
    private ReleaseInfo? availableRelease;
    private MatchDiagnostic? diagnostic;
    private bool checkingUpdates, installingUpdate, detecting;
    private Forms.NotifyIcon? tray;
    private Forms.ToolStripMenuItem? soloItem, partyItem, startupItem, updateItem;
    private readonly Icon soloIcon = MakeIcon(true), partyIcon = MakeIcon(false), unknownIcon = MakeIcon(false, true);
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool busy, exiting, verified, configLoaded;

    public MainWindow() { InitializeComponent(); catalogStore = new(store.DirectoryPath); Closing += OnClosing; }

    private bool GameFound => App.Preview || OverwatchLocator.IsValid(settings.OverwatchPath);
    private Rule[] Desired(Mode mode, Settings? config = null, ServerCatalog? servers = null) => Servers.Desired(mode,
        (config ?? settings).OverwatchPath!, [(servers ?? catalog).Finland]);

    public async void Initialize()
    {
        Log.Write(App.Preview ? "UI preview started (no system changes)" : "App started");
        try
        {
            settings = store.Load(); configLoaded = true;
            if (!settings.StartMinimized) Reveal();
            catalog = catalogStore.Load(error => Log.Write("Server cache: " + error));
            if (App.Preview) settings.OverwatchPath = @"C:\Preview\Overwatch.exe";
            else if (string.IsNullOrWhiteSpace(settings.OverwatchPath)) settings.OverwatchPath = await Task.Run(OverwatchLocator.Find);
            if (!App.Preview)
            {
                // The registry is authoritative for startup; refresh the path after moving/updating the exe.
                settings.RunOnStartup = StartupService.Enabled;
                if (settings.RunOnStartup) StartupService.Set(true);
            }
            store.Save(settings);
            RestorePosition(); SyncToggles();
        }
        catch (Exception e) { ShowError("Couldn't load settings. Fix config.json before retrying.\n" + e.Message, e); }
        CreateTray();
        if (!settings.StartMinimized || !configLoaded || !GameFound || (!App.Preview && !App.Elevated)) Reveal();
        RefreshServerView();
        if (!App.Preview && App.Elevated)
        {
            try { await Task.Run(firewall.DisableUnscoped); }
            catch (Exception error) { ShowError("Couldn't disable legacy global rules.\n" + error.Message, error); return; }
        }
        if (configLoaded) await ApplyMode(settings.Mode, false);
        if (!App.Preview) _ = BackgroundChecks();
        if (configLoaded && !GameFound && string.IsNullOrWhiteSpace(settings.OverwatchPath)) LocateGame(this, new RoutedEventArgs());
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
        tray = new Forms.NotifyIcon { Icon = unknownIcon, Text = "143 OW Switch — checking", Visible = true };
        var menu = new Forms.ContextMenuStrip { BackColor = System.Drawing.Color.FromArgb(24,24,28), ForeColor = System.Drawing.Color.White, ShowImageMargin = false };
        menu.Items.Add(new Forms.ToolStripMenuItem("143 OW Switch") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        soloItem = new Forms.ToolStripMenuItem("BLOCK", null, async (_, _) => await ApplyMode(Mode.Solo));
        partyItem = new Forms.ToolStripMenuItem("UNBLOCK", null, async (_, _) => await ApplyMode(Mode.Party));
        menu.Items.Add(soloItem); menu.Items.Add(partyItem); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) => Reveal());
        startupItem = new Forms.ToolStripMenuItem("Run on startup", null, (_, _) => SetStartup(!settings.RunOnStartup));
        updateItem = new Forms.ToolStripMenuItem("Check for updates", null, async (_, _) =>
        { if (availableRelease != null) InstallUpdate(this, new RoutedEventArgs()); else { Reveal(); OpenSettings(this, new RoutedEventArgs()); await CheckForUpdates(); } });
        menu.Items.Add(updateItem); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { if (!busy && !installingUpdate) Exit(); });
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Reveal(); };
        SyncToggles();
    }
    private async Task<bool> ApplyMode(Mode mode, bool notify = true, Settings? candidate = null, ServerCatalog? serverUpdate = null)
    {
        if (busy) return false;
        if (!configLoaded) { ShowError("Settings have not loaded. Fix config.json and retry."); return false; }
        if (!App.Preview && !App.Elevated)
        { ShowError("Administrator permission is required to modify Windows Firewall."); return false; }
        if (!App.Preview && !OverwatchLocator.IsValid((candidate ?? settings).OverwatchPath))
        { verified = false; Render(false); ShowError("Overwatch executable not found."); return false; }
        SetBusy(true);
        var next = (candidate ?? settings) with { Mode = mode };
        try
        {
            await Task.Run(() => firewall.Apply(Desired(mode, next, serverUpdate), () =>
            {
                if (serverUpdate != null) catalogStore.Save(serverUpdate);
                else store.Save(next);
            }));
            if (serverUpdate != null) catalog = serverUpdate;
            settings = next; verified = true;
            RulesResult.Text = ""; RepairButton.Visibility = Visibility.Collapsed;
            RefreshServerView();
            ErrorPanel.Visibility = Visibility.Collapsed;
            var active = await Task.Run(() => firewall.IsActive);
            Render(active);
            Log.Write("Mode changed: " + mode);
            if (notify && settings.Notifications && tray != null)
                tray.ShowBalloonTip(2000, "143 OW Switch", active || mode == Mode.Party
                    ? mode == Mode.Solo ? "BLOCK enabled. GEN1 is blocked." : "UNBLOCK enabled. Normal matchmaking restored."
                    : "BLOCK rules saved, but Windows Firewall or local policy is inactive.", Forms.ToolTipIcon.Info);
            return true;
        }
        catch (Exception e)
        {
            verified = false; Render(false);
            ShowError("Couldn't update Windows Firewall.\n" + e.Message, e);
            return false;
        }
        finally { SetBusy(false); }
    }
    private async Task RefreshHealth()
    {
        if (busy || !configLoaded || installingUpdate) return;
        if (!GameFound) { verified = false; Render(false); RefreshServerView(); return; }
        SetBusy(true);
        try
        {
            var health = await Task.Run(() => (Matches: firewall.Check(Desired(settings.Mode)), Active: firewall.IsActive));
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
        Health.Text = !verified ? "Click BLOCK or UNBLOCK to repair" : !active ? "Firewall off or managed by policy" : solo ? "Firewall active" : "App blocking disabled";
        if (!GameFound) { Status.Text = "LOCATE GAME"; Subtitle.Text = "Overwatch executable not found."; Health.Text = "Select Overwatch.exe in Settings"; }
#if DEBUG
        if (App.Preview) Health.Text = "PREVIEW · no system changes";
#endif
        Animate(SoloButton, verified && solo ? "#60519B" : "#202026");
        Animate(PartyButton, verified && !solo ? "#60519B" : "#202026");
        if (tray != null)
        {
            tray.Icon = !verified || (solo && !active) ? unknownIcon : solo ? soloIcon : partyIcon;
            tray.Text = "143 OW Switch — " + (!verified ? "CHECK RULES" : solo && !active ? "NOT ACTIVE" : solo ? "BLOCK" : "UNBLOCK");
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
        ErrorLocate.Visibility = GameFound ? Visibility.Collapsed : Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Visible; Reveal();
    }
    private void SyncToggles()
    {
        StartupToggle.IsChecked = StartupSettings.IsChecked = settings.RunOnStartup;
        MinimizedToggle.IsChecked = MinimizedSettings.IsChecked = settings.StartMinimized;
        NotificationsToggle.IsChecked = settings.Notifications;
        AutomaticUpdatesToggle.IsChecked = settings.AutomaticUpdates;
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
        if (busy) return;
        if (!GameFound) { RulesResult.Text = "Overwatch executable not found."; return; }
        SetBusy(true);
        try
        {
            int count = await Task.Run(() => firewall.RepairCount(Desired(settings.Mode)));
            RulesResult.Text = count == 0 ? "Everything looks good." : $"{count} firewall rule{(count == 1 ? " needs" : "s need")} repair.";
            RepairButton.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception error) { Log.Write(error.ToString()); RulesResult.Text = "Couldn't check rules."; }
        finally { SetBusy(false); }
    }
    private void OpenFirewall(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("mmc.exe", "wf.msc") { UseShellExecute = true }); }
        catch (Exception error) { ShowError(error.Message, error); }
    }
    private async void RemoveRules(object sender, RoutedEventArgs e)
    {
        if (busy || !ConfirmRemoval()) return;
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
        SavePosition(); exiting = true; monitor.Stop(); lifetime.Cancel(); ServerDiagnostics.Stop();
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
