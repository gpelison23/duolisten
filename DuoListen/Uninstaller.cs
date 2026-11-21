using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace DuoListen;

// ---------------------------------------------------------------------------
// Remove tudo o que o DuoListen cria no PC:
//  - tira do mudo os alto-falantes que o app silenciou
//  - %AppData%\DuoListen (configurações)
//  - %TEMP%\.net\DuoListen (arquivos que o .exe extrai ao abrir)
//  - registro do ícone da bandeja (HKCU\Control Panel\NotifyIconSettings)
//  - DuoListen.exe e os arquivos que vieram no zip, na pasta do .exe
// Arquivos em uso só podem ser apagados depois que o app fecha, então um
// script temporário espera o processo terminar e faz a limpeza final.
// ---------------------------------------------------------------------------
static class Uninstaller
{
    static readonly string[] ZipFiles = { "INSTALACAO.txt", "LEIA-ME.txt", "UninstallDuoListen.bat" };

    public static void Run()
    {
        RestoreMutedSpeakers();
        CleanTrayRegistry();
        ScheduleFileDeletion();
    }

    public static void RestoreMutedSpeakers() => AudioDevices.RestoreWindowsAudio(Config.Load());

    static void CleanTrayRegistry()
    {
        string exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (root == null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("ExecutablePath") is string path &&
                    (string.Equals(path, exe, StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(path).Equals("DuoListen.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    k.Dispose();
                    try { root.DeleteSubKeyTree(name, false); } catch { }
                }
            }
        }
        catch { }
    }

    static void ScheduleFileDeletion()
    {
        string exe = Environment.ProcessPath ?? "";
        string dir = Path.GetDirectoryName(exe) ?? "";
        int pid = Environment.ProcessId;

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        if (exe.EndsWith("DuoListen.exe", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"del /f /q \"{exe}\" >nul 2>&1");
        foreach (var f in ZipFiles)
            sb.AppendLine($"del /f /q \"{Path.Combine(dir, f)}\" >nul 2>&1");
        sb.AppendLine("rmdir /s /q \"%APPDATA%\\DuoListen\" >nul 2>&1");
        sb.AppendLine("rmdir /s /q \"%TEMP%\\.net\\DuoListen\" >nul 2>&1");
        // Só remove a pasta do app se ficou vazia (nunca apaga outros arquivos do usuário)
        sb.AppendLine($"rmdir \"{dir}\" >nul 2>&1");
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");

        string script = Path.Combine(Path.GetTempPath(), $"duolisten-uninstall-{pid}.cmd");
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }
}
