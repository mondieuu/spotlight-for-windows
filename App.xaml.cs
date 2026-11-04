using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Odak;

/// <summary>
/// Odak runs in the background: its icon sits in the tray, and Alt+Space opens the search window.
/// Only one instance runs; if launched again, the instance already running stays.
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

        // Uninstall (Settings › Apps or the tray menu) and setup ("SpotlightSetup.exe") modes.
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

        // Show once on first launch as a reminder of the shortcut.
        if (!e.Args.Contains("--tray")) _window.ShowSpotlight();
        // --settings: open the Settings window directly (e.g. from a shortcut).
        if (e.Args.Contains("--settings")) SettingsWindow.Open(_window.HotkeyText);
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

        BuildMenu();
        // Rebuild the menu when the language changes in Settings.
        AppSettings.Changed += () => Dispatcher.Invoke(BuildMenu);
    }

    private void BuildMenu()
    {
        if (_tray == null || _window == null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.T($"Open ({_window.HotkeyText})", $"Aç ({_window.HotkeyText})"), null, (_, _) => _window.ShowSpotlight());
        menu.Items.Add(L.T("Settings…", "Ayarlar…"), null, (_, _) => SettingsWindow.Open(_window.HotkeyText));
        menu.Items.Add(L.T("Downloads folder", "İndirilenler klasörü"), null, (_, _) =>
        {
            Directory.CreateDirectory(Converter.OutputDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Converter.OutputDir}\"") { UseShellExecute = true });
        });
        var startup = new Forms.ToolStripMenuItem(L.T("Start with Windows", "Windows ile başlat")) { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (Installer.IsInstalled)
        {
            menu.Items.Add(L.T("Uninstall Spotlight…", "Spotlight'ı kaldır…"), null, (_, _) =>
                Process.Start(new ProcessStartInfo(Installer.InstalledExe, "--uninstall") { UseShellExecute = true }));
        }
        menu.Items.Add(L.T("Quit", "Çıkış"), null, (_, _) => Quit());
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private void ShowNotification(string title, string text, string? file)
    {
        _lastFile = file;
        _tray?.ShowBalloonTip(4000, title, text, file != null ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
    }

    // ---- "Start with Windows" (current user only, HKCU\...\Run)

    internal static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Spotlight") != null;
    }

    internal static void SetStartup(bool enabled)
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
