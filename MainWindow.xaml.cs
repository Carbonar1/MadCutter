using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MadCutter;

public partial class MainWindow : Window
{
    private string? _currentMediaPath;
    private string? _tempWavPath;
    private readonly string _ffmpegPath;
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private Settings _settings = new();
    private const long CacheMaxBytes = 2L * 1024 * 1024 * 1024; // 2GB

    private const int RecentMax = 15;

    private class Settings
    {
        public string? LastExportDir { get; set; }
        public List<string> Recent { get; set; } = new();
    }

    public MainWindow()
    {
        InitializeComponent();
        _ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        _tempDir = Path.Combine(Path.GetTempPath(), "MadCutter", "cache");
        Directory.CreateDirectory(_tempDir);

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MadCutter");
        Directory.CreateDirectory(appDataDir);
        _settingsPath = Path.Combine(appDataDir, "settings.json");
        LoadSettings();
        EnsureFfmpegExtracted();

        Loaded += OnLoaded;
    }

    private void EnsureFfmpegExtracted()
    {
        if (File.Exists(_ffmpegPath)) return;
        var zipPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.zip");
        if (!File.Exists(zipPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(_ffmpegPath)!;
            Directory.CreateDirectory(dir);
            using var zf = ZipFile.OpenRead(zipPath);
            var entry = zf.Entries.FirstOrDefault(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;
            entry.ExtractToFile(_ffmpegPath, overwrite: true);
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
        }
        catch { _settings = new Settings(); }
    }

    private void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await Web.EnsureCoreWebView2Async();
        Web.CoreWebView2.WebMessageReceived += OnWebMessage;
        try { Web.AllowExternalDrop = false; } catch { }

        var webDir = Path.Combine(AppContext.BaseDirectory, "web");
        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "madcutter.local", webDir, CoreWebView2HostResourceAccessKind.Allow);

        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "madcutter-temp.local", _tempDir, CoreWebView2HostResourceAccessKind.Allow);

        Web.Source = new Uri("https://madcutter.local/index.html");
        Web.CoreWebView2.NavigationCompleted += (_, _) => SendRecent();
    }

    private void SendRecent()
    {
        var list = _settings.Recent?.Where(File.Exists).ToList() ?? new List<string>();
        PostToWeb(new { type = "recent", list });
    }

    private void AddRecent(string path)
    {
        _settings.Recent ??= new List<string>();
        _settings.Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _settings.Recent.Insert(0, path);
        if (_settings.Recent.Count > RecentMax)
            _settings.Recent.RemoveRange(RecentMax, _settings.Recent.Count - RecentMax);
        SaveSettings();
        SendRecent();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 0) return;
        await LoadMediaAsync(files[0]);
    }

    private static string CacheKey(string path)
    {
        var fi = new FileInfo(path);
        var raw = $"{fi.FullName}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static readonly HashSet<string> DirectPlayExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac"
    };

    private static readonly HashSet<string> AlwaysReencodeExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".webm", ".avi", ".ts", ".flv"
    };

    private async Task LoadMediaAsync(string path)
    {
        if (!File.Exists(_ffmpegPath))
        {
            MessageBox.Show($"ffmpeg.exe が無い:\n{_ffmpegPath}\n\nffmpeg/ffmpeg.exe を配置してください。");
            return;
        }

        if (!File.Exists(path))
        {
            PostToWeb(new { type = "status", text = "ファイルが見つからない: " + path });
            return;
        }

        _currentMediaPath = path;
        AddRecent(path);
        var key = CacheKey(path);
        var srcExt = Path.GetExtension(path).ToLowerInvariant();

        string cacheExt;
        string cachePath;
        bool needDecode = false;

        if (DirectPlayExt.Contains(srcExt))
        {
            cacheExt = srcExt;
            cachePath = Path.Combine(_tempDir, key + cacheExt);
            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length == 0)
            {
                PostToWeb(new { type = "status", text = $"読み込み中: {Path.GetFileName(path)}" });
                try { File.Copy(path, cachePath, true); TrimCache(); }
                catch { needDecode = true; }
            }
            else
            {
                try { File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow); } catch { }
                PostToWeb(new { type = "status", text = "キャッシュから読み込み" });
            }
        }
        else if (AlwaysReencodeExt.Contains(srcExt))
        {
            cacheExt = ".m4a";
            cachePath = Path.Combine(_tempDir, key + cacheExt);
            needDecode = true;
        }
        else
        {
            var codec = await ProbeAudioCodecAsync(path);
            PostToWeb(new { type = "log", text = $"probe codec={codec ?? "(unknown)"} src={Path.GetFileName(path)}" });
            var (streamExt, streamArgs) = PickStreamCopyTarget(codec);

            if (streamExt != null)
            {
                cacheExt = streamExt;
                cachePath = Path.Combine(_tempDir, key + cacheExt);
                if (!File.Exists(cachePath) || new FileInfo(cachePath).Length < 44)
                {
                    PostToWeb(new { type = "status", text = $"音声抽出中: {Path.GetFileName(path)}" });
                    var ok = await RunFfmpegAsync($"-y -i \"{path}\" {streamArgs} \"{cachePath}\"");
                    if (!ok)
                    {
                        Debug.WriteLine($"stream copy failed codec={codec} err={_lastFfmpegErr}");
                        try { if (File.Exists(cachePath)) File.Delete(cachePath); } catch { }
                        needDecode = true;
                    }
                    else
                    {
                        TrimCache();
                    }
                }
                else
                {
                    try { File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow); } catch { }
                    PostToWeb(new { type = "status", text = "キャッシュから読み込み" });
                }
            }
            else
            {
                cacheExt = ".m4a";
                cachePath = Path.Combine(_tempDir, key + cacheExt);
                needDecode = true;
            }
        }

        if (needDecode)
        {
            cacheExt = ".wav";
            cachePath = Path.Combine(_tempDir, key + cacheExt);
            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length < 44)
            {
                PostToWeb(new { type = "status", text = $"デコード中(wav): {Path.GetFileName(path)}" });
                var ok = await RunFfmpegAsync($"-y -i \"{path}\" -ac 1 -ar 8000 -vn -c:a pcm_s16le \"{cachePath}\"");
                if (!ok)
                {
                    PostToWeb(new { type = "status", text = $"デコード失敗: {_lastFfmpegErr}" });
                    return;
                }
                TrimCache();
            }
            else
            {
                try { File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow); } catch { }
            }
        }

        _tempWavPath = cachePath;
        if (!File.Exists(cachePath))
        {
            PostToWeb(new { type = "status", text = $"cacheファイル生成失敗: {cachePath} / err={_lastFfmpegErr}" });
            return;
        }
        var sz = new FileInfo(cachePath).Length;
        PostToWeb(new { type = "status", text = $"cache ok ({sz:N0} bytes, {cacheExt})" });
        PostToWeb(new { type = "log", text = $"cache={cachePath} size={sz}" });
        var url = $"https://madcutter-temp.local/{key}{cacheExt}";
        PostToWeb(new { type = "load", url, name = Path.GetFileName(path) });
    }

    private void TrimCache()
    {
        try
        {
            var files = new DirectoryInfo(_tempDir).GetFiles();
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (total <= CacheMaxBytes) return;
            Array.Sort(files, (a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
            foreach (var f in files)
            {
                if (total <= CacheMaxBytes) break;
                total -= f.Length;
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            if (type == "export")
            {
                var inSec = root.GetProperty("in").GetDouble();
                var outSec = root.GetProperty("out").GetDouble();
                var format = root.TryGetProperty("format", out var fm) ? fm.GetString() ?? "mp3" : "mp3";
                var mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? "cbr" : "cbr";
                var value = root.TryGetProperty("value", out var v) ? v.GetInt32() : 192;
                var suggested = root.TryGetProperty("fname", out var fn) ? fn.GetString() : null;
                await ExportAsync(inSec, outSec, format, mode, value, suggested);
            }
            else if (type == "openPath")
            {
                var p = root.GetProperty("path").GetString();
                if (!string.IsNullOrEmpty(p)) await LoadMediaAsync(p);
            }
            else if (type == "clearRecent")
            {
                _settings.Recent = new List<string>();
                SaveSettings();
                SendRecent();
            }
            else if (type == "pickFile")
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "メディア|*.mp4;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.mov;*.mkv|全ファイル|*.*"
                };
                if (dlg.ShowDialog(this) == true) await LoadMediaAsync(dlg.FileName);
            }
            else if (type == "clearCache")
            {
                ClearCache();
                PostToWeb(new { type = "status", text = "キャッシュ削除" });
            }
        }
        catch (Exception ex)
        {
            PostToWeb(new { type = "status", text = "エラー: " + ex.Message });
        }
    }

    private void ClearCache()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_tempDir))
            {
                if (_tempWavPath != null && string.Equals(f, _tempWavPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private static (string ext, string filter) FormatInfo(string format) => format switch
    {
        "wav" => ("wav", "WAV|*.wav"),
        "aac" => ("m4a", "AAC (m4a)|*.m4a"),
        "ogg" => ("ogg", "OGG Vorbis|*.ogg"),
        _     => ("mp3", "MP3|*.mp3"),
    };

    private static string BuildCodecArgs(string format, string mode, int value)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        switch (format)
        {
            case "wav":
                return "-c:a pcm_s16le -ar 44100";
            case "aac":
            {
                var kbps = Math.Clamp(value, 32, 320);
                return $"-c:a aac -b:a {kbps}k";
            }
            case "ogg":
            {
                var q = Math.Clamp(value, 0, 10);
                return $"-c:a libvorbis -q:a {q.ToString(ci)}";
            }
            default: // mp3
                if (mode == "vbr")
                {
                    var q = Math.Clamp(value, 0, 9);
                    return $"-c:a libmp3lame -q:a {q}";
                }
                else
                {
                    var kbps = Math.Clamp(value, 32, 320);
                    return $"-c:a libmp3lame -b:a {kbps}k";
                }
        }
    }

    private async Task ExportAsync(double inSec, double outSec, string format, string mode, int value, string? suggestedName)
    {
        if (_currentMediaPath == null) return;
        if (outSec <= inSec)
        {
            PostToWeb(new { type = "status", text = "in/out 範囲が不正" });
            return;
        }

        var (ext, filter) = FormatInfo(format);
        var defaultName = Path.GetFileNameWithoutExtension(_currentMediaPath) + "_cut." + ext;
        if (!string.IsNullOrWhiteSpace(suggestedName))
        {
            var s = suggestedName.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            if (!s.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
            {
                var noExt = Path.GetFileNameWithoutExtension(s);
                s = noExt + "." + ext;
            }
            defaultName = s;
        }

        var dlg = new SaveFileDialog
        {
            Filter = filter,
            FileName = defaultName,
            DefaultExt = ext,
            AddExtension = true
        };
        if (!string.IsNullOrEmpty(_settings.LastExportDir) && Directory.Exists(_settings.LastExportDir))
            dlg.InitialDirectory = _settings.LastExportDir;
        if (dlg.ShowDialog() != true) return;

        _settings.LastExportDir = Path.GetDirectoryName(dlg.FileName);
        SaveSettings();

        PostToWeb(new { type = "status", text = "出力中..." });
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var dur = outSec - inSec;
        var codecArgs = BuildCodecArgs(format, mode, value);

        var args = $"-y -ss {inSec.ToString(ci)} -t {dur.ToString(ci)} " +
                   $"-i \"{_currentMediaPath}\" -vn {codecArgs} \"{dlg.FileName}\"";
        var ok = await RunFfmpegAsync(args);
        PostToWeb(new { type = "status", text = ok ? "出力完了: " + dlg.FileName : "出力失敗" });
    }

    private async Task<string?> ProbeAudioCodecAsync(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-hide_banner -i \"{path}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var m = System.Text.RegularExpressions.Regex.Match(stderr, @"Audio:\s*([A-Za-z0-9_]+)");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }
        catch { return null; }
    }

    private static (string? ext, string args) PickStreamCopyTarget(string? codec)
    {
        switch (codec)
        {
            case "aac":    return (".m4a", "-vn -c:a copy -bsf:a aac_adtstoasc -movflags +faststart");
            case "mp3":    return (".mp3", "-vn -c:a copy");
            case "vorbis": return (".ogg", "-vn -c:a copy");
            case "opus":   return (".ogg", "-vn -c:a copy");
            case "flac":   return (".flac", "-vn -c:a copy");
            case "pcm_s16le":
            case "pcm_s24le":
            case "pcm_s32le":
            case "pcm_f32le":
            case "pcm_s16be":
            case "pcm_s24be":
                           return (".wav", "-vn -c:a pcm_s16le");
            default:       return (null, "");
        }
    }

    private string _lastFfmpegErr = "";

    private async Task<bool> RunFfmpegAsync(string args)
    {
        _lastFfmpegErr = "";
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        };
        try
        {
            using var p = Process.Start(psi)!;
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
            {
                var lines = stderr.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                _lastFfmpegErr = lines.Count > 0 ? lines[^1].Trim() : "exit=" + p.ExitCode;
                Debug.WriteLine("ffmpeg args: " + args);
                Debug.WriteLine("ffmpeg stderr:\n" + stderr);
                return false;
            }
            return true;
        }
        catch (Exception ex) { _lastFfmpegErr = ex.Message; return false; }
    }

    private void PostToWeb(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        Dispatcher.Invoke(() => Web.CoreWebView2?.PostWebMessageAsJson(json));
    }
}
