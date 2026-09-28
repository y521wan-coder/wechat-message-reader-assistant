using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WeChatMessageReaderAssistant.App.Services;

public sealed class WeChatReadHistoryService
{
    private const int MaxReadMessageKeys = 5000;
    private const string LegacyHistoryPath = @"D:\WeChatMessageReaderAssistant\config\wechat-read-history.json";
    private readonly string _historyPath;
    private readonly List<string> _readKeys = new();
    private readonly HashSet<string> _readKeySet = new(StringComparer.OrdinalIgnoreCase);

    public WeChatReadHistoryService()
    {
        var configDirectory = Path.Combine(AppContext.BaseDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        _historyPath = Path.Combine(configDirectory, "wechat-read-history.json");
        TryMigrateLegacyHistory();
        Load();
    }

    public string HistoryPath => _historyPath;

    public bool HasRead(WeChatChatMessage message)
    {
        return !string.IsNullOrWhiteSpace(message.StableKey) && _readKeySet.Contains(message.StableKey);
    }

    public void MarkRead(IEnumerable<WeChatChatMessage> messages)
    {
        var changed = false;
        foreach (var message in messages)
        {
            var key = message.StableKey?.Trim();
            if (string.IsNullOrWhiteSpace(key) || _readKeySet.Contains(key))
            {
                continue;
            }

            _readKeySet.Add(key);
            _readKeys.Add(key);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        Trim();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return;
            }

            var json = File.ReadAllText(_historyPath);
            var data = JsonSerializer.Deserialize<WeChatReadHistoryData>(json) ?? new WeChatReadHistoryData();
            foreach (var key in data.ReadMessageKeys ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(key) || _readKeySet.Contains(key))
                {
                    continue;
                }

                _readKeySet.Add(key);
                _readKeys.Add(key);
            }

            Trim();
        }
        catch
        {
            _readKeys.Clear();
            _readKeySet.Clear();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
        var data = new WeChatReadHistoryData
        {
            UpdatedAt = DateTime.Now,
            ReadMessageKeys = _readKeys.ToList()
        };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_historyPath, json);
    }

    private void TryMigrateLegacyHistory()
    {
        try
        {
            if (File.Exists(_historyPath) || !File.Exists(LegacyHistoryPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            File.Copy(LegacyHistoryPath, _historyPath, overwrite: false);
        }
        catch
        {
            // 已读历史迁移失败不影响微信新消息朗读主功能。
        }
    }

    private void Trim()
    {
        if (_readKeys.Count <= MaxReadMessageKeys)
        {
            return;
        }

        var removeCount = _readKeys.Count - MaxReadMessageKeys;
        for (var i = 0; i < removeCount; i++)
        {
            _readKeySet.Remove(_readKeys[i]);
        }

        _readKeys.RemoveRange(0, removeCount);
    }

    private sealed class WeChatReadHistoryData
    {
        public DateTime UpdatedAt { get; set; }
        public List<string> ReadMessageKeys { get; set; } = new();
    }
}
