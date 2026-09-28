using System;
using System.IO;
using System.Text.Json;

namespace WeChatMessageReaderAssistant.App.Services;

public sealed class AppSettings
{
    public int SpeechRate { get; set; } = 10;
    public int SpeechVolume { get; set; } = 100;
    public string SpeechBackend { get; set; } = "AUTO";
    public string GlobalMonitorHotkey { get; set; } = "Ctrl+Alt+M";
    public bool OnlyWeChatNotifications { get; set; } = true;
    public bool AutoReadWeChatNotifications { get; set; } = true;
    public int WeChatMonitorIntervalMilliseconds { get; set; } = 500;
}

public sealed class AppSettingsService
{
    private const string LegacySettingsPath = @"D:\WeChatMessageReaderAssistant\config\settings.json";
    private readonly string _settingsPath;

    public AppSettingsService()
    {
        var configDirectory = Path.Combine(AppContext.BaseDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        _settingsPath = Path.Combine(configDirectory, "settings.json");
        TryMigrateLegacySettings();
    }

    public string SettingsPath => _settingsPath;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.SpeechRate = Math.Clamp(settings.SpeechRate, -10, 10);
            settings.SpeechVolume = Math.Clamp(settings.SpeechVolume, 0, 100);
            settings.SpeechBackend = NormalizeSpeechBackend(settings.SpeechBackend);
            settings.GlobalMonitorHotkey = NormalizeGlobalMonitorHotkey(settings.GlobalMonitorHotkey);
            settings.WeChatMonitorIntervalMilliseconds = Math.Clamp(settings.WeChatMonitorIntervalMilliseconds, 300, 1000);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.SpeechRate = Math.Clamp(settings.SpeechRate, -10, 10);
        settings.SpeechVolume = Math.Clamp(settings.SpeechVolume, 0, 100);
        settings.SpeechBackend = NormalizeSpeechBackend(settings.SpeechBackend);
        settings.GlobalMonitorHotkey = NormalizeGlobalMonitorHotkey(settings.GlobalMonitorHotkey);
        settings.WeChatMonitorIntervalMilliseconds = Math.Clamp(settings.WeChatMonitorIntervalMilliseconds, 300, 1000);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    private void TryMigrateLegacySettings()
    {
        try
        {
            if (File.Exists(_settingsPath) || !File.Exists(LegacySettingsPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.Copy(LegacySettingsPath, _settingsPath, overwrite: false);
        }
        catch
        {
            // 配置迁移失败不影响主功能；会使用默认设置并在新位置保存。
        }
    }

    private static string NormalizeSpeechBackend(string? backend)
    {
        if (string.Equals(backend, "ZDSRAPI", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "ZDSROfficial", StringComparison.OrdinalIgnoreCase))
        {
            return "ZDSRAPI";
        }

        if (string.Equals(backend, "AUTO", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "Auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "AutoDetect", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "ScreenReaderAuto", StringComparison.OrdinalIgnoreCase))
        {
            return "AUTO";
        }

        if (string.Equals(backend, "BAOYI", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "BoySynth", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "Baoyi", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "BoyCtrl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "byctrl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "BaoyiBoyCtrl", StringComparison.OrdinalIgnoreCase))
        {
            return "BAOYI";
        }

        if (string.Equals(backend, "NVDA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "NVDAAPI", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "NVDAController", StringComparison.OrdinalIgnoreCase))
        {
            return "NVDA";
        }

        if (string.IsNullOrWhiteSpace(backend))
        {
            return "AUTO";
        }

        return "SAPI";
    }

    private static string NormalizeGlobalMonitorHotkey(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return "Ctrl+Alt+M";
        }

        var normalized = hotkey.Trim().Replace("Control", "Ctrl", StringComparison.OrdinalIgnoreCase);
        if (normalized.Equals("Ctrl+Alt+M", StringComparison.OrdinalIgnoreCase))
        {
            return "Ctrl+Alt+M";
        }

        if (normalized.Equals("Ctrl+Shift+M", StringComparison.OrdinalIgnoreCase))
        {
            return "Ctrl+Shift+M";
        }

        if (normalized.Equals("Alt+Shift+M", StringComparison.OrdinalIgnoreCase))
        {
            return "Alt+Shift+M";
        }

        if (normalized.Equals("Ctrl+Alt+W", StringComparison.OrdinalIgnoreCase))
        {
            return "Ctrl+Alt+W";
        }

        if (normalized.Equals("Off", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("None", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("关闭", StringComparison.OrdinalIgnoreCase))
        {
            return "Off";
        }

        return "Ctrl+Alt+M";
    }

}


