using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace ThermoSync;

public partial class App : Application
{
    // Uma única instância: abrir o THERMOSYNC de novo só traz a janela que já está aberta para a frente.
    private static Mutex? _single;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                MessageBox.Show("Erro fatal no ThermoSync:\n\n" + ex.Message + "\n\n" + ex.StackTrace,
                                "ThermoSync - Erro Fatal", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Erro de execução no ThermoSync:\n\n" + args.Exception.Message + "\n\n" + args.Exception.StackTrace,
                            "ThermoSync - Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        bool first;
        try { _single = new Mutex(true, @"Local\ThermoSync.SingleInstance", out first); }
        catch (UnauthorizedAccessException) { first = false; }

        if (!first)
        {
            IntPtr h = FindWindow(null, "THERMOSYNC");
            if (h != IntPtr.Zero)
            {
                ShowWindow(h, 9); // SW_RESTORE
                ShowWindow(h, 5); // SW_SHOW
                SetForegroundWindow(h);
                Shutdown();
                return;
            }
            // Se a janela não existe (ex: processo anterior travado em segundo plano), prossegue normalmente
        }
        base.OnStartup(e);
        bool startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var win = new MainWindow(startMinimized);
        if (startMinimized)
        {
            new System.Windows.Interop.WindowInteropHelper(win).EnsureHandle();
        }
        else
        {
            win.Show();
        }
    }
}
