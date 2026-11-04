using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Odak;

/// <summary>
/// YouTube ve Instagram bağlantılarını MP3/MP4 olarak indirir. Açık kaynaklı yt-dlp ve ffmpeg
/// kullanır; ikisi de ilk kullanımda resmi GitHub sürümlerinden %LOCALAPPDATA%\Odak\tools klasörüne indirilir.
/// </summary>
public static class Converter
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim ToolsLock = new(1, 1);
    private static readonly Regex ProgressRx = new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);

    private static string ToolsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotlight", "tools");
    private static string YtDlp => Path.Combine(ToolsDir, "yt-dlp.exe");
    private static string Ffmpeg => Path.Combine(ToolsDir, "ffmpeg.exe");

    public static string OutputDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Spotlight");

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Spotlight/1.0");
        return http;
    }

    /// <summary>Desteklenen site mi? (YouTube, YouTube Music, Shorts, Instagram)</summary>
    public static bool IsSupported(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return host == "youtu.be" || host.EndsWith("youtube.com") || host.EndsWith("instagram.com");
    }

    /// <summary>İndirir ve dönüştürür; bittiğinde dosyanın tam yolunu döndürür.</summary>
    public static async Task<string> DownloadAsync(string url, bool audio, IProgress<(double fraction, string status)> progress, CancellationToken ct = default)
    {
        await EnsureToolsAsync(progress, ct);
        Directory.CreateDirectory(OutputDir);

        var args = new List<string>
        {
            "--newline", "--no-playlist", "--no-mtime",
            "--ffmpeg-location", ToolsDir,
            "-o", Path.Combine(OutputDir, "%(title).120B [%(id)s].%(ext)s"),
            "--print", "after_move:filepath", "--no-simulate",
            "--embed-metadata",
        };
        if (audio)
            args.AddRange(new[] { "-x", "--audio-format", "mp3", "--audio-quality", "0" });
        else
            // Her oynatıcıda açılsın diye H.264 + AAC tercih edilir.
            args.AddRange(new[] { "-f", "bv*[vcodec^=avc1]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b", "--merge-output-format", "mp4" });
        args.Add(url);

        var psi = new ProcessStartInfo(YtDlp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Dönüştürücü başlatılamadı.");
        string? outputPath = null;
        var errors = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
        proc.BeginErrorReadLine();

        progress.Report((0, "Bağlantı hazırlanıyor…"));
        while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            var m = ProgressRx.Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                progress.Report((pct / 100.0 * 0.95, $"İndiriliyor… %{pct:0}"));
            else if (line.StartsWith("[ExtractAudio]") || line.StartsWith("[Merger]") || line.StartsWith("[Metadata]"))
                progress.Report((0.97, audio ? "MP3'e dönüştürülüyor…" : "MP4 hazırlanıyor…"));
            else if (line.Length > 3 && Path.IsPathRooted(line.Trim()) && File.Exists(line.Trim()))
                outputPath = line.Trim();
        }
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var err = errors.ToString().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR")) ?? "Bilinmeyen hata.";
            throw new InvalidOperationException(err.Replace("ERROR: ", ""));
        }
        return outputPath ?? OutputDir;
    }

    /// <summary>yt-dlp ve ffmpeg yoksa indirir; yt-dlp haftada bir kendini günceller (siteler sık değişir).</summary>
    private static async Task EnsureToolsAsync(IProgress<(double, string)> progress, CancellationToken ct)
    {
        await ToolsLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(ToolsDir);
            if (!File.Exists(YtDlp))
            {
                await DownloadFileAsync(YtDlpUrl, YtDlp, f => progress.Report((f * 0.2, $"Dönüştürücü indiriliyor (1/2)… %{f * 100:0}")), ct);
            }
            else if (DateTime.Now - File.GetLastWriteTime(YtDlp) > TimeSpan.FromDays(7))
            {
                progress.Report((0, "Dönüştürücü güncelleniyor…"));
                try
                {
                    using var up = Process.Start(new ProcessStartInfo(YtDlp, "-U") { UseShellExecute = false, CreateNoWindow = true });
                    if (up != null) await up.WaitForExitAsync(ct);
                    File.SetLastWriteTime(YtDlp, DateTime.Now);
                }
                catch { /* güncellenemezse mevcut sürümle devam */ }
            }

            if (!File.Exists(Ffmpeg))
            {
                var zip = Path.Combine(Path.GetTempPath(), "odak-ffmpeg.zip");
                await DownloadFileAsync(FfmpegUrl, zip, f => progress.Report((0.2 + f * 0.6, $"Ses/video bileşeni indiriliyor (2/2)… %{f * 100:0}")), ct);
                progress.Report((0.8, "Bileşenler kuruluyor…"));
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(zip);
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            entry.ExtractToFile(Ffmpeg, true);
                        else if (entry.FullName.EndsWith("bin/ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                            entry.ExtractToFile(Path.Combine(ToolsDir, "ffprobe.exe"), true);
                    }
                }, ct);
                try { File.Delete(zip); } catch { }
            }
        }
        finally { ToolsLock.Release(); }
    }

    private static async Task DownloadFileAsync(string url, string target, Action<double> onProgress, CancellationToken ct)
    {
        var tmp = target + ".part";
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) onProgress((double)read / total);
            }
        }
        File.Move(tmp, target, true);
    }
}
