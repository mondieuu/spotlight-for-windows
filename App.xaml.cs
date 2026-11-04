using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Odak;

/// <summary>
/// Odak arka planda çalışır: tepside simgesi durur, Alt+Boşluk ile arama penceresi açılır.
/// Tek kopya çalışır; ikinci kez başlatılırsa açık olan kopya kalır.
/// </summary>
public partial class App : Application
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private Mutex? _single;
    private SearchWindow? _window;
    private Forms.NotifyIcon? _tray;
    private string? _lastFile;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Kaldırma (Ayarlar › Uygulamalar ya da tepsi menüsü) ve kurulum ("SpotlightSetup.exe") modları.
        if (e.Args.Contains("--uninstall"))
        {
            if (e.Args.Contains("--quiet"))
            {
                Installer.Uninstall(deleteDownloads: false);
                Shutdown();
                return;
            }
            ShowSetup(uninstall: true);
            return;
        }
        if (Installer.IsSetupExe)
        {
            ShowSetup(uninstall: false);
            return;
        }

        _single = new Mutex(true, "Spotlight.TekKopya", out bool first);
        if (!first)
        {
            Shutdown();
            return;
        }

        _window = new SearchWindow();
        _window.InitializeHidden();
        _window.Notify = ShowNotification;
        CreateTray();

        // İlk açılışta kısayolu hatırlatmak için bir kez göster.
        if (!e.Args.Contains("--tray")) _window.ShowSpotlight();
    }

    private void ShowSetup(bool uninstall)
    {
        var window = new SetupWindow(uninstall);
        window.Closed += (_, _) => Shutdown();
        window.Show();
        window.Activate();
    }

    private void CreateTray()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/odak.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = $"Spotlight — {_window!.HotkeyText}",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => _window.ShowSpotlight();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_lastFile != null && File.Exists(_lastFile))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastFile}\"") { UseShellExecute = true });
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add($"Aç ({_window.HotkeyText})", null, (_, _) => _window.ShowSpotlight());
        menu.Items.Add("Ayarlar…", null, (_, _) => SettingsWindow.Open());
        menu.Items.Add("İndirilenler klasörü", null, (_, _) =>
        {
            Directory.CreateDirectory(Converter.OutputDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Converter.OutputDir}\"") { UseShellExecute = true });
        });
        var startup = new Forms.ToolStripMenuItem("Windows ile başlat") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (Installer.IsInstalled)
        {
            menu.Items.Add("Spotlight'ı kaldır…", null, (_, _) =>
                Process.Start(new ProcessStartInfo(Installer.InstalledExe, "--uninstall") { UseShellExecute = true }));
        }
        menu.Items.Add("Çıkış", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
    }

    private void ShowNotification(string title, string text, string? file)
    {
        _lastFile = file;
        _tray?.ShowBalloonTip(4000, title, text, file != null ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
    }

    // ---- "Windows ile başlat" (yalnızca bu kullanıcı için, HKCU\...\Run)

    public static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Spotlight") != null;
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        if (enabled) key.SetValue("Spotlight", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("Spotlight", throwOnMissingValue: false);
    }

    private void Quit()
    {
        _window?.UnregisterHotkey();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _single?.ReleaseMutex();
        Shutdown();
    }
}
