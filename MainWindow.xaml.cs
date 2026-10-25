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
        _tempWavPath = Path.Combine(_tempDir, key + ".wav");
        var fileName = key + ".wav";

        bool cached = File.Exists(_tempWavPath) && new FileInfo(_tempWavPath).Length > 44;

        if (!cached)
        {
            PostToWeb(new { type = "status", text = $"デコード中: {Path.GetFileName(path)}" });
            var ok = await RunFfmpegAsync($"-y -i \"{path}\" -ac 1 -ar 44100 -vn \"{_tempWavPath}\"");
            if (!ok)
            {
                PostToWeb(new { type = "status", text = "デコード失敗" });
                return;
            }
            TrimCache();
        }
        else
        {
            try { File.SetLastAccessTimeUtc(_tempWavPath, DateTime.UtcNow); } catch { }
            PostToWeb(new { type = "status", text = "キャッシュから読み込み" });
        }

        var url = $"https://madcutter-temp.local/{fileName}";
        PostToWeb(new { type = "load", url, name = Path.GetFileName(path) });
    }

    private void TrimCache()
    {
        try
        {
            var files = new DirectoryInfo(_tempDir).GetFiles("*.wav");
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
                if (dlg.ShowDialog() == true) await LoadMediaAsync(dlg.FileName);
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
            foreach (var f in Directory.GetFiles(_tempDir, "*.wav"))
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

    private Task<bool> RunFfmpegAsync(string args)
    {
        var tcs = new TaskCompletionSource<bool>();
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, _) => { tcs.TrySetResult(p.ExitCode == 0); p.Dispose(); };
        try { p.Start(); p.BeginErrorReadLine(); }
        catch (Exception ex) { tcs.TrySetResult(false); MessageBox.Show(ex.Message); }
        return tcs.Task;
    }

    private void PostToWeb(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        Dispatcher.Invoke(() => Web.CoreWebView2?.PostWebMessageAsJson(json));
    }
}
