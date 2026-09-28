using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WeChatMessageReaderAssistant.App.Services;

public sealed class NotificationListenerService
{
    private readonly HashSet<uint> _seenNotificationIds = new();
    private readonly Dictionary<string, DateTimeOffset> _recentMessageKeys = new();
    private readonly TimeSpan _deduplicateWindow = TimeSpan.FromSeconds(15);
    private UserNotificationListener? _listener;

    public event EventHandler<NotificationMessage>? NotificationReceived;

    public async Task<string> RequestAccessAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return "当前 Windows 版本不支持此通知监听接口。";
        }

        _listener = UserNotificationListener.Current;
        var status = await _listener.RequestAccessAsync();
        return status switch
        {
            UserNotificationListenerAccessStatus.Allowed => "通知监听权限已允许。",
            UserNotificationListenerAccessStatus.Denied => "通知监听权限被拒绝。请到 Windows 设置、隐私、通知中允许本软件读取通知。",
            UserNotificationListenerAccessStatus.Unspecified => "通知监听权限未确定。请根据系统提示允许通知访问。",
            _ => "通知监听权限状态未知：" + status
        };
    }

    public async Task<int> MarkCurrentNotificationsAsSeenAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return 0;
        }

        _listener ??= UserNotificationListener.Current;
        var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        foreach (var notification in notifications)
        {
            _seenNotificationIds.Add(notification.Id);
        }

        CleanupRecentMessageKeys(DateTimeOffset.Now);
        return notifications.Count;
    }

    public async Task<IReadOnlyList<NotificationMessage>> ReadCurrentNotificationsAsync(bool includeNonWeChat)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return Array.Empty<NotificationMessage>();
        }

        _listener ??= UserNotificationListener.Current;
        var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        var result = new List<NotificationMessage>();

        foreach (var notification in notifications.OrderBy(n => n.CreationTime))
        {
            if (_seenNotificationIds.Contains(notification.Id))
            {
                continue;
            }

            _seenNotificationIds.Add(notification.Id);
            var message = ConvertNotification(notification);
            if (message == null)
            {
                continue;
            }

            if (IsDuplicate(message))
            {
                continue;
            }

            if (!includeNonWeChat && !message.IsProbablyWeChat)
            {
                continue;
            }

            result.Add(message);
            NotificationReceived?.Invoke(this, message);
        }

        return result;
    }

    public void ResetSeenNotifications()
    {
        _seenNotificationIds.Clear();
        _recentMessageKeys.Clear();
    }

    public NotificationMessage ParseForTest(string appName, string title, string body)
    {
        return BuildMessage(appName, title, body, DateTimeOffset.Now);
    }

    private bool IsDuplicate(NotificationMessage message)
    {
        var now = DateTimeOffset.Now;
        CleanupRecentMessageKeys(now);

        var key = $"{message.AppName}|{message.Title}|{message.Body}".ToLowerInvariant();
        if (_recentMessageKeys.TryGetValue(key, out var lastSeen) && now - lastSeen < _deduplicateWindow)
        {
            return true;
        }

        _recentMessageKeys[key] = now;
        return false;
    }

    private void CleanupRecentMessageKeys(DateTimeOffset now)
    {
        var expiredKeys = _recentMessageKeys
            .Where(pair => now - pair.Value > _deduplicateWindow)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _recentMessageKeys.Remove(key);
        }
    }

    private static NotificationMessage? ConvertNotification(UserNotification notification)
    {
        try
        {
            var appName = notification.AppInfo.DisplayInfo.DisplayName;
            var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                        ?? new List<string>();

            var title = texts.Count > 0 ? texts[0] : string.Empty;
            var body = texts.Count > 1 ? string.Join(" ", texts.Skip(1)) : string.Empty;

            return BuildMessage(appName, title, body, notification.CreationTime);
        }
        catch
        {
            return null;
        }
    }

    private static NotificationMessage BuildMessage(string appName, string title, string body, DateTimeOffset createdAt)
    {
        appName = appName.Trim();
        title = title.Trim();
        body = body.Trim();

        var combined = string.Join(" ", new[] { appName, title, body }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var isWeChat = combined.Contains("微信", StringComparison.OrdinalIgnoreCase)
                       || combined.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
                       || combined.Contains("Weixin", StringComparison.OrdinalIgnoreCase);

        var isVoice = combined.Contains("语音", StringComparison.OrdinalIgnoreCase)
                      || combined.Contains("[语音]", StringComparison.OrdinalIgnoreCase)
                      || combined.Contains("voice", StringComparison.OrdinalIgnoreCase);

        var sender = ExtractSender(title, body);
        var speakText = isVoice
            ? $"{sender}给您发来一条语音消息。"
            : BuildTextSpeakMessage(sender, body);

        return new NotificationMessage(appName, title, body, createdAt, isWeChat, isVoice, speakText);
    }

    private static string ExtractSender(string title, string body)
    {
        if (!string.IsNullOrWhiteSpace(title) && !title.Contains("微信", StringComparison.OrdinalIgnoreCase))
        {
            return title;
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            var separators = new[] { ':', '：' };
            var index = body.IndexOfAny(separators);
            if (index > 0 && index <= 30)
            {
                return body[..index].Trim();
            }
        }

        return !string.IsNullOrWhiteSpace(title) ? title : "有人";
    }

    private static string BuildTextSpeakMessage(string sender, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return $"{sender}给您发来一条消息。";
        }

        var content = body;
        var separators = new[] { ':', '：' };
        var index = body.IndexOfAny(separators);
        if (index > 0 && index <= 30)
        {
            content = body[(index + 1)..].Trim();
        }

        const int maxLength = 120;
        content = content.Length > maxLength ? content[..maxLength] + "，后面内容已省略。" : content;
        return $"{sender}说，{content}";
    }
}
