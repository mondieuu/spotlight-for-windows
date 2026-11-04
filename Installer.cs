using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Odak;

/// <summary>
/// Setup and trace-free uninstall. No admin rights needed: everything is installed for the current user only.
///
/// EVERY location setup touches (all of them are removed on uninstall):
///   • %LOCALAPPDATA%\Programs\Spotlight\            → the program
///   • %LOCALAPPDATA%\Spotlight\                     → file index, usage counts, yt-dlp/ffmpeg
///   • %TEMP%\.net\Spotlight\                        → .NET single-file app cache
///   • Start menu and (if chosen) desktop shortcuts
///   • HKCU\...\Run\Spotlight                        → "Start with Windows"
///   • HKCU\...\Uninstall\Spotlight                  → Settings &gt; Apps entry
///   • (optional) Downloads\Spotlight                 → MP3/MP4 files the user downloaded
/// </summary>
public static class Installer
{
    public const string AppName = "Spotlight";
    public const string Version = "1.1.0";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Spotlight";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");
    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    private static string LegacyDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odak");
    private static string BundleCache => Path.Combine(Path.GetTempPath(), ".net", AppName);
    private static string SetupBundleCache => Path.Combine(Path.GetTempPath(), ".net", AppName + "Setup");
    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>Whether it was launched as the setup file ("SpotlightSetup.exe").</summary>
    public static bool IsSetupExe =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("Setup", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>Copies the program and creates the shortcuts and the "Apps" entry.</summary>
    public static void Install(bool startWithWindows, bool desktopShortcut)
    {
        StopOtherInstances();
        Directory.CreateDirectory(InstallDir);
        File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);

        CreateShortcut(StartMenuLink, InstalledExe, "--open");
        if (desktopShortcut) CreateShortcut(DesktopLink, InstalledExe, "--open");
        else TryDelete(DesktopLink);

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (startWithWindows) run.SetValue(AppName, $"\"{InstalledExe}\" --tray");
            else run.DeleteValue(AppName, throwOnMissingValue: false);
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", $"\"{InstalledExe}\",0");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>Launches the installed Spotlight.</summary>
    public static void LaunchInstalled() =>
        Process.Start(new ProcessStartInfo(InstalledExe, "--open") { UseShellExecute = true, WorkingDirectory = InstallDir });

    /// <summary>Removes everything. The program folder is deleted by a hidden command after this process exits.</summary>
    public static void Uninstall(bool deleteDownloads)
    {
        StopOtherInstances();

        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(AppName, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        TryDeleteDir(DataDir);
        TryDeleteDir(LegacyDataDir);
        if (deleteDownloads) TryDeleteDir(Converter.OutputDir);

        // A running exe cannot delete itself: a hidden PowerShell waits until this process exits (however long
        // the window stays open), then deletes the program folder and the .NET cache.
        var targets = new[] { InstallDir, BundleCache, SetupBundleCache };
        var list = string.Join(",", targets.Select(t => "'" + t.Replace("'", "''") + "'"));
        var script =
            $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; " +
            $"foreach ($i in 1..10) {{ foreach ($t in @({list})) {{ if (Test-Path -LiteralPath $t) {{ Remove-Item -LiteralPath $t -Recurse -Force -ErrorAction SilentlyContinue }} }}; " +
            $"if (-not (@({list}) | Where-Object {{ Test-Path -LiteralPath $_ }})) {{ break }}; Start-Sleep -Milliseconds 500 }}";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    private static bool IsInstalledCopy() =>
        string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Closes running Spotlight instances (except this process).</summary>
    private static void StopOtherInstances()
    {
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(AppName).Concat(Process.GetProcessesByName("Odak")))
        {
            try
            {
                if (p.Id == me) continue;
                p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    private static void CreateShortcut(string link, string target, string args)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = target;
        shortcut.Arguments = args;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target);
        shortcut.IconLocation = target + ",0";
        shortcut.Description = "Search apps, files and Windows settings";
        shortcut.Save();
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
