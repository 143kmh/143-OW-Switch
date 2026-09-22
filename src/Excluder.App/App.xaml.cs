using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace Excluder.App;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? listener;
    public static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static bool Preview { get; private set; }
    public static bool Elevate(bool startup)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = true, Verb = "runas", Arguments = startup ? "--startup" : "" });
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return false; }
        catch (Exception e) { Log.Write(e.ToString()); MessageBox.Show("Could not restart as administrator.\n" + e.Message); return false; }
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        Preview = e.Args.Contains("--preview");
#endif
        if (e.Args is ["--install-update", var stage]) { await UpdateInstaller.Install(stage); Shutdown(); return; }
        if (!Preview && !Elevated && Elevate(e.Args.Contains("--startup"))) { Shutdown(); return; }
        var id = WindowsIdentity.GetCurrent().User!.Value;
        var suffix = Preview ? ".Preview" : Elevated ? ".Admin" : ".User";
        instance = new Mutex(true, @"Local\143OWServerSwitch." + id + suffix, out var first);
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\143OWServerSwitch.Show." + id + suffix);
        if (!first) { activation.Set(); Shutdown(); return; }
        var window = new MainWindow();
        MainWindow = window;
        listener = ThreadPool.RegisterWaitForSingleObject(activation,
            (_, _) => Dispatcher.BeginInvoke(window.Reveal), null, Timeout.Infinite, false);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write(args.Exception.ToString());
            MessageBox.Show("An unexpected error occurred.\n" + args.Exception.Message, "143 Server Excluder");
            args.Handled = true;
        };
        window.Initialize();
        if (e.Args is ["--update-receipt", var receipt]) _ = UpdateInstaller.AcknowledgeAndClean(receipt);
        if (e.Args.Contains("--update-failed")) MessageBox.Show("Couldn't install update. The previous version was restored.", "143 OW Switch");
    }
    protected override void OnExit(ExitEventArgs e)
    {
        ServerDiagnostics.Stop();
        listener?.Unregister(null); activation?.Dispose(); instance?.Dispose();
        base.OnExit(e);
    }
}
