using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Odak;

/// <summary>
/// Downloads YouTube and Instagram links as MP3/MP4. Uses the open-source yt-dlp and ffmpeg;
/// both are downloaded on first use from their official GitHub releases into %LOCALAPPDATA%\Spotlight\tools
/// and checked against the SHA-256 checksums those releases publish before they are ever run.
/// </summary>
public static class Converter
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private const string YtDlpSums = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    private const string FfmpegSums = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256";

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

    /// <summary>Is this a supported site? (YouTube, YouTube Music, Shorts, Instagram)</summary>
    public static bool IsSupported(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return host == "youtu.be" || host == "youtube.com" || host.EndsWith(".youtube.com") || host == "instagram.com" || host.EndsWith(".instagram.com");
    }

    /// <summary>Downloads and converts; returns the file's full path when done.</summary>
    public static async Task<string> DownloadAsync(string url, bool audio, IProgress<(double fraction, string status)> progress, CancellationToken ct = default)
    {
        await EnsureToolsAsync(progress, ct);
        Directory.CreateDirectory(OutputDir);

        var args = new List<string>
        {
            "--ignore-config", "--newline", "--no-playlist", "--no-mtime",
            "--ffmpeg-location", ToolsDir,
            "-o", Path.Combine(OutputDir, "%(title).120B [%(id)s].%(ext)s"),
            "--print", "after_move:filepath", "--no-simulate",
            "--embed-metadata",
        };
        if (audio)
            args.AddRange(new[] { "-x", "--audio-format", "mp3", "--audio-quality", "0" });
        else
            // H.264 + AAC is preferred so the file plays in any player.
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

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException(L.T("Could not start the converter.", "Dönüştürücü başlatılamadı."));
        string? outputPath = null;
        var errors = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
        proc.BeginErrorReadLine();

        progress.Report((0, L.T("Preparing the link…", "Bağlantı hazırlanıyor…")));
        while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            var m = ProgressRx.Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                progress.Report((pct / 100.0 * 0.95, L.T($"Downloading… {pct:0}%", $"İndiriliyor… %{pct:0}")));
            else if (line.StartsWith("[ExtractAudio]") || line.StartsWith("[Merger]") || line.StartsWith("[Metadata]"))
                progress.Report((0.97, audio ? L.T("Converting to MP3…", "MP3'e dönüştürülüyor…") : L.T("Preparing MP4…", "MP4 hazırlanıyor…")));
            else if (line.Length > 3 && Path.IsPathRooted(line.Trim()) && File.Exists(line.Trim()))
                outputPath = line.Trim();
        }
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var err = errors.ToString().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR")) ?? L.T("Unknown error.", "Bilinmeyen hata.");
            throw new InvalidOperationException(err.Replace("ERROR: ", ""));
        }
        return outputPath ?? OutputDir;
    }

    /// <summary>Downloads yt-dlp and ffmpeg if missing; yt-dlp updates itself weekly (the sites change often).</summary>
    private static async Task EnsureToolsAsync(IProgress<(double, string)> progress, CancellationToken ct)
    {
        await ToolsLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(ToolsDir);
            if (!File.Exists(YtDlp))
            {
                var sum = await ExpectedHashAsync(YtDlpSums, "yt-dlp.exe", ct);
                await DownloadFileAsync(YtDlpUrl, YtDlp, sum, f => progress.Report((f * 0.2, L.T($"Downloading the converter (1/2)… {f * 100:0}%", $"Dönüştürücü indiriliyor (1/2)… %{f * 100:0}"))), ct);
            }
            else if (DateTime.Now - File.GetLastWriteTime(YtDlp) > TimeSpan.FromDays(7))
            {
                progress.Report((0, L.T("Updating the converter…", "Dönüştürücü güncelleniyor…")));
                try
                {
                    using var up = Process.Start(new ProcessStartInfo(YtDlp, "-U") { UseShellExecute = false, CreateNoWindow = true });
                    if (up != null) await up.WaitForExitAsync(ct);
                    File.SetLastWriteTime(YtDlp, DateTime.Now);
                }
                catch { /* if the update fails, continue with the current version */ }
            }

            if (!File.Exists(Ffmpeg))
            {
                // Inside our own folder: a fixed name in the shared temp folder could be swapped before it is unpacked.
                var zip = Path.Combine(ToolsDir, "ffmpeg.zip");
                var sum = await ExpectedHashAsync(FfmpegSums, "ffmpeg-master-latest-win64-gpl.zip", ct);
                await DownloadFileAsync(FfmpegUrl, zip, sum, f => progress.Report((0.2 + f * 0.6, L.T($"Downloading the audio/video component (2/2)… {f * 100:0}%", $"Ses/video bileşeni indiriliyor (2/2)… %{f * 100:0}"))), ct);
                progress.Report((0.8, L.T("Installing components…", "Bileşenler kuruluyor…")));
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

    /// <summary>Reads a tool's published checksum list ("&lt;hex&gt;  &lt;file name&gt;" lines).</summary>
    private static async Task<string> ExpectedHashAsync(string sumsUrl, string fileName, CancellationToken ct)
    {
        var sums = await Http.GetStringAsync(sumsUrl, ct);
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim() == fileName && parts[0].Length == 64) return parts[0];
        }
        throw new InvalidOperationException(L.T($"No published checksum for {fileName}.", $"{fileName} için yayımlanmış sağlama değeri yok."));
    }

    private static async Task DownloadFileAsync(string url, string target, string sha256, Action<double> onProgress, CancellationToken ct)
    {
        var tmp = target + ".part";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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
                hash.AppendData(buffer, 0, n);
                read += n;
                if (total > 0) onProgress((double)read / total);
            }
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException(L.T("The download failed verification and was deleted.", "İndirilen dosya doğrulanamadı ve silindi."));
        }
        File.Move(tmp, target, true);
    }
}
