using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Wpf.Ui.Appearance;

namespace DuoListen;

public partial class App : Application
{
    // Só uma cópia do app por vez. Abrir o .exe de novo (por exemplo com o app escondido
    // na bandeja) só traz a janela da cópia que já está rodando.
    Mutex _instanceMutex;
    EventWaitHandle _showSignal;

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    const int ASFW_ANY = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Modos usados pelo UninstallDuoListen.bat (sem abrir janela)
        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Uninstaller.Run();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--restore-audio", StringComparer.OrdinalIgnoreCase))
        {
            Uninstaller.RestoreMutedSpeakers();
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(true, @"Local\DuoListen.SingleInstance", out bool firstInstance);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DuoListen.Show");
        if (!firstInstance)
        {
            AllowSetForegroundWindow(ASFW_ANY); // deixa a outra cópia vir para a frente
            _showSignal.Set();
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray()),
            null, Timeout.Infinite, false);

        // Segue o tema (claro/escuro) e a cor de destaque do Windows
        ApplicationThemeManager.ApplySystemTheme(true);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
