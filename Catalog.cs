using System;
using System.Collections.Generic;
using System.IO;

namespace Odak;

/// <summary>Kind: launchable app, Windows tool or Settings page.</summary>
public enum EntryKind { App, Tool, Setting, Folder }

/// <summary>
/// Bilingual Windows names: each entry holds the Turkish name, the English name, extra keywords and
/// (for tools not found in the Start menu) a launch command.
/// If a Start menu app has the same name, the entry is attached to that app as an alias;
/// otherwise it becomes a separate result with its own command. So typing "device manager" finds
/// Aygıt Yöneticisi, and typing "hesap makinesi" finds Calculator (and vice versa).
/// </summary>
public static class Catalog
{
    public sealed record Item(string Tr, string En, EntryKind Kind, string? Command, string? Args = null, string Keywords = "");

    private const string SettingsIcon = @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    private static Item T(string tr, string en, string cmd, string keys = "", string? args = null) => new(tr, en, EntryKind.Tool, cmd, args, keys);
    private static Item S(string tr, string en, string uri, string keys = "") => new(tr, en, EntryKind.Setting, uri, null, keys);
    private static Item F(string tr, string en, string shell, string keys = "") => new(tr, en, EntryKind.Folder, shell, null, keys);
    /// <summary>Alias only: makes a Start menu app findable in both languages (no command).</summary>
    private static Item A(string tr, string en, string keys = "") => new(tr, en, EntryKind.App, null, null, keys);

    public static readonly Item[] Items =
    {
        // ---- Windows tools
        T("Aygıt Yöneticisi", "Device Manager", "devmgmt.msc", "donanım sürücü driver hardware aygit"),
        T("Disk Yönetimi", "Disk Management", "diskmgmt.msc", "bölüm partition biçimlendir format sürücü harfi"),
        T("Bilgisayar Yönetimi", "Computer Management", "compmgmt.msc"),
        T("Olay Görüntüleyicisi", "Event Viewer", "eventvwr.msc", "günlük log hata error"),
        T("Hizmetler", "Services", "services.msc", "servis service"),
        T("Görev Zamanlayıcı", "Task Scheduler", "taskschd.msc", "zamanlanmış görev scheduled task"),
        T("Yerel Grup İlkesi Düzenleyicisi", "Local Group Policy Editor", "gpedit.msc", "gpedit group policy grup ilkesi"),
        T("Yerel Güvenlik İlkesi", "Local Security Policy", "secpol.msc", "secpol"),
        T("Yerel Kullanıcılar ve Gruplar", "Local Users and Groups", "lusrmgr.msc", "lusrmgr"),
        T("Performans İzleyicisi", "Performance Monitor", "perfmon.msc", "perfmon"),
        T("Sertifika Yöneticisi", "Certificate Manager", "certmgr.msc", "certmgr sertifika certificate"),
        T("Yazdırma Yönetimi", "Print Management", "printmanagement.msc"),
        T("Kayıt Defteri Düzenleyicisi", "Registry Editor", "regedit.exe", "regedit kayıt defteri registry"),
        T("Görev Yöneticisi", "Task Manager", "taskmgr.exe", "taskmgr işlem process uygulama kapat"),
        T("Kaynak İzleyicisi", "Resource Monitor", "resmon.exe", "resmon"),
        T("Sistem Bilgisi", "System Information", "msinfo32.exe", "msinfo32 donanım bilgisi specs"),
        T("Sistem Yapılandırması", "System Configuration", "msconfig.exe", "msconfig"),
        T("Komut İstemi", "Command Prompt", "cmd.exe", "cmd konsol console"),
        T("Windows PowerShell", "Windows PowerShell", "powershell.exe", "powershell ps"),
        T("Terminal", "Windows Terminal", "wt.exe", "wt konsol console"),
        T("Denetim Masası", "Control Panel", "control.exe", "control panel"),
        T("Programlar ve Özellikler", "Programs and Features", "appwiz.cpl", "program kaldır uninstall"),
        T("Ağ Bağlantıları", "Network Connections", "ncpa.cpl", "ncpa adaptör adapter ethernet"),
        T("Güç Seçenekleri", "Power Options", "powercfg.cpl", "güç planı power plan"),
        T("Ses Aygıtları", "Sound Devices", "mmsys.cpl", "hoparlör speaker mikrofon microphone kayıt playback"),
        T("Fare Özellikleri", "Mouse Properties", "main.cpl", "imleç cursor pointer"),
        T("Sistem Özellikleri", "System Properties", "sysdm.cpl", "bilgisayar adı computer name"),
        T("Gelişmiş Sistem Ayarları", "Advanced System Settings", "SystemPropertiesAdvanced.exe", "sanal bellek virtual memory performans"),
        T("Ortam Değişkenleri", "Environment Variables", "rundll32.exe", "path env değişken variable", "sysdm.cpl,EditEnvironmentVariables"),
        T("Tarih ve Saat", "Date and Time", "timedate.cpl", "saat dilimi time zone"),
        T("Bölge", "Region", "intl.cpl", "biçim format bölgesel regional"),
        T("İnternet Seçenekleri", "Internet Options", "inetcpl.cpl", "internet properties"),
        T("Windows Defender Güvenlik Duvarı", "Windows Defender Firewall", "firewall.cpl", "güvenlik duvarı firewall"),
        T("Kullanıcı Hesapları", "User Accounts", "netplwiz.exe", "netplwiz kullanıcı user"),
        T("Kimlik Bilgileri Yöneticisi", "Credential Manager", "control.exe", "şifre parola password kimlik credential", "/name Microsoft.CredentialManager"),
        T("Aygıtlar ve Yazıcılar", "Devices and Printers", "control.exe", "yazıcı printer", "printers"),
        T("Windows Araçları", "Windows Tools", "control.exe", "yönetimsel araçlar administrative tools", "admintools"),
        T("Disk Temizleme", "Disk Cleanup", "cleanmgr.exe", "cleanmgr temizle clean"),
        T("Sürücüleri Birleştir ve İyileştir", "Defragment and Optimize Drives", "dfrgui.exe", "defrag birleştir optimize"),
        T("Sistem Geri Yükleme", "System Restore", "rstrui.exe", "geri yükleme noktası restore point"),
        T("Kurtarma Sürücüsü", "Recovery Drive", "RecoveryDrive.exe"),
        T("Ekran Klavyesi", "On-Screen Keyboard", "osk.exe", "osk"),
        T("Büyüteç", "Magnifier", "magnify.exe", "yakınlaştır zoom"),
        T("Ekran Okuyucusu", "Narrator", "narrator.exe"),
        T("Karakter Eşlem", "Character Map", "charmap.exe", "charmap sembol symbol"),
        T("Uzak Masaüstü Bağlantısı", "Remote Desktop Connection", "mstsc.exe", "mstsc rdp uzak masaüstü remote desktop"),
        T("Windows Bellek Tanılama", "Windows Memory Diagnostic", "MdSched.exe", "ram bellek testi memory test"),
        T("DirectX Tanılama Aracı", "DirectX Diagnostic Tool", "dxdiag.exe", "dxdiag ekran kartı graphics"),
        T("Ses Düzeyi Karıştırıcısı", "Volume Mixer", "sndvol.exe", "sndvol ses seviyesi volume"),
        T("Ekran Renk Kalibrasyonu", "Display Color Calibration", "dccw.exe", "dccw"),
        T("ODBC Veri Kaynakları", "ODBC Data Sources", "odbcad32.exe", "odbc"),
        T("Windows Özellikleri", "Windows Features", "optionalfeatures.exe", "özellik aç kapat turn windows features on off hyper-v"),
        T("Not Defteri", "Notepad", "notepad.exe", "metin text"),
        T("Hesap Makinesi", "Calculator", "calc.exe", "calc hesapla"),
        T("Paint", "Paint", "mspaint.exe", "çizim draw resim"),
        T("Ekran Alıntısı Aracı", "Snipping Tool", "snippingtool.exe", "ekran görüntüsü screenshot kırp snip"),
        T("Dosya Gezgini", "File Explorer", "explorer.exe", "explorer dosyalar files klasör"),
        T("Çalıştır", "Run", "explorer.exe", "run dialog çalıştır", "shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}"),

        // ---- Folders
        F("Bu Bilgisayar", "This PC", "shell:MyComputerFolder", "bilgisayarım my computer diskler drives"),
        F("Geri Dönüşüm Kutusu", "Recycle Bin", "shell:RecycleBinFolder", "çöp kutusu trash silinenler"),
        F("İndirilenler", "Downloads", "shell:Downloads"),
        F("Belgeler", "Documents", "shell:Personal", "dökümanlar"),
        F("Masaüstü", "Desktop", "shell:Desktop"),
        F("Resimler", "Pictures", "shell:My Pictures", "fotoğraflar"),
        F("Müzik", "Music", "shell:My Music"),
        F("Videolar", "Videos", "shell:My Video"),
        F("Başlangıç Klasörü", "Startup Folder", "shell:Startup", "otomatik başlat autostart"),
        F("AppData Klasörü", "AppData Folder", "shell:AppData", "roaming"),
        F("Yazı Tipleri", "Fonts", "shell:Fonts", "font"),
        F("Ağ", "Network", "shell:NetworkPlacesFolder", "paylaşım share"),

        // ---- Settings pages
        S("Ayarlar", "Settings", "ms-settings:", "settings ayar"),
        S("Sistem", "System", "ms-settings:system"),
        S("Ekran", "Display", "ms-settings:display", "çözünürlük resolution parlaklık brightness ölçek scale monitör"),
        S("Ses Ayarları", "Sound Settings", "ms-settings:sound", "ses sound hoparlör speaker"),
        S("Bildirimler", "Notifications", "ms-settings:notifications"),
        S("Rahatsız Etmeyin", "Do Not Disturb", "ms-settings:notifications", "odaklanma focus sessiz"),
        S("Güç ve Pil", "Power & Battery", "ms-settings:powersleep", "pil batarya battery uyku sleep enerji"),
        S("Depolama", "Storage", "ms-settings:storagesense", "disk alanı storage sense yer aç"),
        S("Pano", "Clipboard", "ms-settings:clipboard", "kopyala yapıştır geçmiş history"),
        S("Çoklu Görev", "Multitasking", "ms-settings:multitasking", "snap pencere window"),
        S("Etkinleştirme", "Activation", "ms-settings:activation", "lisans license ürün anahtarı product key"),
        S("Kurtarma", "Recovery", "ms-settings:recovery", "sıfırla reset"),
        S("Hakkında", "About", "ms-settings:about", "sistem bilgisi pc adı cihaz özellikleri specs"),
        S("Geliştiriciler İçin", "For Developers", "ms-settings:developers", "geliştirici modu developer mode"),
        S("Bluetooth ve Cihazlar", "Bluetooth & Devices", "ms-settings:bluetooth", "bluetooth eşleştir pair kulaklık headphones"),
        S("Yazıcılar ve Tarayıcılar", "Printers & Scanners", "ms-settings:printers", "yazıcı printer tarayıcı scanner"),
        S("Fare Ayarları", "Mouse Settings", "ms-settings:mousetouchpad", "fare mouse"),
        S("Dokunmatik Yüzey", "Touchpad", "ms-settings:devices-touchpad", "touchpad"),
        S("Otomatik Kullan", "AutoPlay", "ms-settings:autoplay", "autoplay usb"),
        S("Ağ ve İnternet", "Network & Internet", "ms-settings:network", "internet ağ network"),
        S("Wi-Fi", "Wi-Fi", "ms-settings:network-wifi", "kablosuz wireless wifi"),
        S("Ethernet", "Ethernet", "ms-settings:network-ethernet", "kablolu wired"),
        S("VPN", "VPN", "ms-settings:network-vpn"),
        S("Proxy", "Proxy", "ms-settings:network-proxy", "vekil sunucu"),
        S("Mobil Etkin Nokta", "Mobile Hotspot", "ms-settings:network-mobilehotspot", "hotspot paylaş"),
        S("Uçak Modu", "Airplane Mode", "ms-settings:network-airplanemode", "uçuş flight"),
        S("Kişiselleştirme", "Personalization", "ms-settings:personalization"),
        S("Arka Plan", "Background", "ms-settings:personalization-background", "duvar kağıdı wallpaper"),
        S("Renkler", "Colors", "ms-settings:colors", "karanlık mod dark mode açık mod light mode vurgu accent"),
        S("Temalar", "Themes", "ms-settings:themes", "tema theme"),
        S("Kilit Ekranı", "Lock Screen", "ms-settings:lockscreen"),
        S("Başlat Menüsü", "Start Menu", "ms-settings:personalization-start", "başlat start"),
        S("Görev Çubuğu", "Taskbar", "ms-settings:taskbar", "taskbar"),
        S("Gece Işığı", "Night Light", "ms-settings:nightlight", "mavi ışık blue light"),
        S("Yüklü Uygulamalar", "Installed Apps", "ms-settings:appsfeatures", "uygulama kaldır uninstall apps features"),
        S("Varsayılan Uygulamalar", "Default Apps", "ms-settings:defaultapps", "varsayılan tarayıcı default browser"),
        S("Başlangıç Uygulamaları", "Startup Apps", "ms-settings:startupapps", "açılışta başlayan startup"),
        S("İsteğe Bağlı Özellikler", "Optional Features", "ms-settings:optionalfeatures"),
        S("Hesaplar", "Accounts", "ms-settings:accounts", "hesap account microsoft hesabı"),
        S("Bilgileriniz", "Your Info", "ms-settings:yourinfo", "profil profile"),
        S("Oturum Açma Seçenekleri", "Sign-in Options", "ms-settings:signinoptions", "şifre parola password pin windows hello yüz face parmak izi fingerprint"),
        S("E-posta ve Hesaplar", "Email & Accounts", "ms-settings:emailandaccounts", "e-posta email"),
        S("Aile", "Family", "ms-settings:family-group", "ebeveyn parental"),
        S("Tarih ve Saat Ayarları", "Date & Time Settings", "ms-settings:dateandtime", "saat dilimi time zone"),
        S("Dil ve Bölge", "Language & Region", "ms-settings:regionlanguage", "dil language bölge region"),
        S("Klavye", "Keyboard", "ms-settings:keyboard", "giriş dili input"),
        S("Yazma", "Typing", "ms-settings:typing", "otomatik düzelt autocorrect"),
        S("Konuşma", "Speech", "ms-settings:speech", "ses tanıma speech recognition"),
        S("Oyun Modu", "Game Mode", "ms-settings:gaming-gamemode", "oyun game"),
        S("Xbox Game Bar", "Xbox Game Bar", "ms-settings:gaming-gamebar", "game bar kayıt record"),
        S("Erişilebilirlik", "Accessibility", "ms-settings:easeofaccess", "erişim kolaylığı ease of access"),
        S("Metin Boyutu", "Text Size", "ms-settings:easeofaccess-display", "yazı boyutu font size büyüt"),
        S("Altyazılar", "Captions", "ms-settings:easeofaccess-closedcaptioning", "altyazı subtitles"),
        S("Gizlilik ve Güvenlik", "Privacy & Security", "ms-settings:privacy", "gizlilik privacy izinler permissions"),
        S("Konum", "Location", "ms-settings:privacy-location", "gps konum servisleri location services"),
        S("Kamera İzinleri", "Camera Privacy", "ms-settings:privacy-webcam", "kamera webcam"),
        S("Mikrofon İzinleri", "Microphone Privacy", "ms-settings:privacy-microphone", "mikrofon microphone"),
        S("Cihazımı Bul", "Find My Device", "ms-settings:findmydevice", "kayıp lost"),
        S("Windows Update", "Windows Update", "ms-settings:windowsupdate", "güncelleme update güncelleştirme"),
        S("Windows Güvenliği", "Windows Security", "windowsdefender:", "antivirüs antivirus defender virüs virus tehdit threat"),

        // ---- Alias only: make Start menu apps findable in both languages
        A("Fotoğraflar", "Photos", "resim galeri gallery"),
        A("Saat", "Clock", "alarm zamanlayıcı timer kronometre stopwatch"),
        A("Takvim", "Calendar"),
        A("Posta", "Mail", "e-posta email"),
        A("Outlook", "Outlook", "e-posta email posta"),
        A("Hava Durumu", "Weather"),
        A("Haritalar", "Maps"),
        A("Kamera", "Camera", "webcam"),
        A("Microsoft Store", "Microsoft Store", "mağaza store uygulama indir"),
        A("Ses Kaydedici", "Sound Recorder", "ses kaydı voice recorder"),
        A("Medya Oynatıcı", "Media Player", "müzik music video oynatıcı player"),
        A("Filmler ve TV", "Movies & TV", "film video"),
        A("Kişiler", "People", "rehber contacts"),
        A("Yapışkan Notlar", "Sticky Notes", "not notes"),
        A("Telefon Bağlantısı", "Phone Link", "telefon phone android"),
        A("İpuçları", "Tips"),
        A("Geri Bildirim Merkezi", "Feedback Hub"),
        A("Hızlı Yardım", "Quick Assist", "uzaktan yardım remote help"),
        A("Haberler", "News"),
        A("Yapılacaklar", "To Do", "görev tasks"),
        A("Clipchamp", "Clipchamp", "video düzenleyici video editor"),
        A("Windows Yedekleme", "Windows Backup", "yedek backup"),
        A("Dev Home", "Dev Home"),
        A("Başlarken", "Get Started"),
        A("Microsoft Edge", "Microsoft Edge", "tarayıcı browser internet"),
        A("Google Chrome", "Google Chrome", "chrome tarayıcı browser internet"),
        A("Mozilla Firefox", "Mozilla Firefox", "firefox tarayıcı browser internet"),
    };

    /// <summary>Whether the command actually exists on this PC (e.g. Home editions have no gpedit.msc).</summary>
    public static bool IsAvailable(Item item)
    {
        if (item.Command == null) return false;
        var cmd = item.Command;
        if (cmd.Contains(':')) return true; // ms-settings:, shell:, windowsdefender:
        return ResolvePath(cmd) != null;
    }

    /// <summary>Full path of the command (in System32, Windows or on the PATH).</summary>
    public static string? ResolvePath(string cmd)
    {
        if (Path.IsPathRooted(cmd)) return File.Exists(cmd) ? cmd : null;
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var dir in new[] { sys, win })
        {
            var p = Path.Combine(dir, cmd);
            if (File.Exists(p)) return p;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), cmd);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        // App execution aliases of Store apps (e.g. wt.exe)
        var aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", cmd);
        return File.Exists(aliases) ? aliases : null;
    }

    /// <summary>Icon source: the Settings app for settings, the shell path for folders, the file itself for tools.</summary>
    public static string? IconSource(Item item) => item.Kind switch
    {
        EntryKind.Setting => item.Command == "windowsdefender:"
            ? @"shell:AppsFolder\Microsoft.SecHealthUI_8wekyb3d8bbwe!SecHealthUI"
            : SettingsIcon,
        EntryKind.Folder => item.Command,
        _ when item.Args is { } a && a.StartsWith("shell:") => null,
        _ when item.Command == "control.exe" && item.Args != null => null,
        _ => item.Command == null ? null : ResolvePath(item.Command),
    };
}
