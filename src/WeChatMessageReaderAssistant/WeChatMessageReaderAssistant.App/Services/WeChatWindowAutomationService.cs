using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace WeChatMessageReaderAssistant.App.Services;

public sealed record WeChatWindowScanResult(
    bool Found,
    string Summary,
    IReadOnlyList<string> WindowDescriptions,
    IReadOnlyList<string> VisibleTexts);

public sealed record WeChatChatMessage(
    string ChatName,
    string Text,
    string MessageType,
    string StableKey,
    bool IsOutgoing,
    string DirectionDebug,
    string SpeakerName);

public sealed record WeChatChatViewportState(
    bool Found,
    bool IsNearBottom,
    string Summary);

public sealed record WeChatFocusedElementState(
    bool IsWeChat,
    bool IsInputField,
    string Summary);

public sealed class WeChatWindowAutomationService
{
    private static readonly string[] WeChatProcessKeywords =
    {
        "wechat",
        "weixin",
        "wxwork",
        "wechatappex"
    };

    private static readonly string[] WeChatTextKeywords =
    {
        "微信",
        "wechat",
        "weixin",
        "wecom"
    };

    public WeChatWindowScanResult ScanVisibleWeChatText(int maxTextCount = 160)
    {
        try
        {
            var processDescriptions = DescribeWeChatProcesses();
            var windows = FindCandidateWindows(includeUiaRootFallback: true);
            if (windows.Count == 0)
            {
                return new WeChatWindowScanResult(
                    false,
                    "没有找到微信窗口。已检测到的微信相关进程数量：" + processDescriptions.Count + "。请确认电脑版微信主窗口已经打开，不只是停留在托盘。",
                    processDescriptions,
                    Array.Empty<string>());
            }

            var descriptions = new List<string>();
            descriptions.AddRange(processDescriptions);

            var texts = new List<string>();
            foreach (var candidate in windows)
            {
                descriptions.Add(candidate.Description);

                AutomationElement? element = null;
                try
                {
                    element = AutomationElement.FromHandle(candidate.Handle);
                }
                catch
                {
                    element = candidate.Element;
                }

                if (element == null)
                {
                    continue;
                }

                CollectTexts(element, texts, maxTextCount, TreeWalker.ControlViewWalker, "控件视图");
                if (texts.Count < maxTextCount)
                {
                    CollectTexts(element, texts, maxTextCount, TreeWalker.RawViewWalker, "原始视图");
                }

                if (texts.Count >= maxTextCount)
                {
                    break;
                }
            }

            var summary = texts.Count > 0
                ? $"找到 {windows.Count} 个疑似微信窗口，读取到 {texts.Count} 条可见文本。"
                : $"找到 {windows.Count} 个疑似微信窗口，但没有读取到可见文本。微信可能没有向 Windows 无障碍接口暴露聊天内容，或者当前窗口不是聊天主界面。";

            return new WeChatWindowScanResult(true, summary, descriptions, texts);
        }
        catch (Exception ex)
        {
            return new WeChatWindowScanResult(
                false,
                "检测微信窗口失败：" + ex.Message,
                Array.Empty<string>(),
                Array.Empty<string>());
        }
    }

    public string? TryReadFocusedWeChatInputText()
    {
        return TryReadFocusedWeChatInputText(out _);
    }

    public WeChatFocusedElementState GetFocusedElementState()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null)
            {
                return new WeChatFocusedElementState(false, false, "当前没有焦点控件");
            }

            var automationId = SafeGet(() => focused.Current.AutomationId) ?? string.Empty;
            var className = SafeGet(() => focused.Current.ClassName) ?? string.Empty;
            var processId = SafeGet(() => focused.Current.ProcessId);
            var isWeChat = GetWeChatProcessIds().Contains(processId);
            var isInputField = isWeChat
                && (automationId.Equals("chat_input_field", StringComparison.OrdinalIgnoreCase)
                    || className.Contains("ChatInputField", StringComparison.OrdinalIgnoreCase));

            var summary = $"焦点控件：IsWeChat={isWeChat}；IsInputField={isInputField}；AutomationId={automationId}；ClassName={className}；ProcessId={processId}";
            return new WeChatFocusedElementState(isWeChat, isInputField, summary);
        }
        catch (Exception ex)
        {
            return new WeChatFocusedElementState(false, false, "读取当前焦点控件失败：" + ex.Message);
        }
    }

    public string? TryReadFocusedWeChatInputText(out string reason)
    {
        reason = string.Empty;
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null)
            {
                reason = "\u5f53\u524d\u6ca1\u6709\u7126\u70b9\u63a7\u4ef6";
                return null;
            }

            var automationId = SafeGet(() => focused.Current.AutomationId) ?? string.Empty;
            var className = SafeGet(() => focused.Current.ClassName) ?? string.Empty;
            var processId = SafeGet(() => focused.Current.ProcessId);

            if (!automationId.Equals("chat_input_field", StringComparison.OrdinalIgnoreCase)
                && !className.Contains("ChatInputField", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"\u5f53\u524d\u7126\u70b9\u4e0d\u662f\u5fae\u4fe1\u8f93\u5165\u6846\uff0c\u81ea\u52a8\u5316ID\uff1a{automationId}\uff0c\u7c7b\u540d\uff1a{className}";
                return null;
            }

            if (!GetWeChatProcessIds().Contains(processId))
            {
                reason = $"\u5f53\u524d\u8f93\u5165\u6846\u8fdb\u7a0b\u4e0d\u662f\u5fae\u4fe1\u8fdb\u7a0b\uff0c\u8fdb\u7a0bID\uff1a{processId}";
                return null;
            }

            var text = GetElementTextValue(focused);
            text = NormalizeText(text);
            if (string.IsNullOrWhiteSpace(text) || text.Equals("\u8f93\u5165", StringComparison.OrdinalIgnoreCase))
            {
                reason = "\u5fae\u4fe1\u8f93\u5165\u6846\u4e3a\u7a7a\uff0c\u6216\u8005\u53ea\u8bfb\u5230\u5360\u4f4d\u6587\u5b57\uff1a\u8f93\u5165";
                return null;
            }

            reason = "\u5df2\u4ece\u5f53\u524d\u5fae\u4fe1\u8f93\u5165\u6846\u8bfb\u53d6\u5230\u6587\u672c";
            return text;
        }
        catch (Exception ex)
        {
            reason = "\u8bfb\u53d6\u5fae\u4fe1\u8f93\u5165\u6846\u5931\u8d25\uff1a" + ex.Message;
            return null;
        }
    }

    public IReadOnlyList<WeChatChatMessage> ReadCurrentChatMessages(int maxMessages = 80, bool includeDirection = false)
    {
        // Inspired by the NVDA PC WeChat Enhancement add-on: locate the real chat_message_list,
        // then read its direct UIA list-item children instead of scanning the whole WeChat window.
        // This avoids treating the session list as chat messages.
        var chatWindows = FindCandidateWindows(includeUiaRootFallback: false)
            .Where(w => w.Description.Contains("mmui::ChatSingleWindow", StringComparison.OrdinalIgnoreCase)
                        || w.Description.Contains("Qt51514QWindowIcon", StringComparison.OrdinalIgnoreCase)
                        || w.Description.Contains("mmui::MainWindow", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.Description.Contains("可见：是", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.Description.Contains("mmui::ChatSingleWindow", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.Description.Contains("mmui::MainWindow", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var messages = new List<WeChatChatMessage>();
        foreach (var window in chatWindows)
        {
            AutomationElement? element = null;
            try
            {
                element = AutomationElement.FromHandle(window.Handle);
            }
            catch
            {
                element = window.Element;
            }

            if (element == null)
            {
                continue;
            }

            var chatName = TryGetChatName(element);
            var lists = FindChatMessageLists(element);
            foreach (var list in lists)
            {
                CollectDirectMessageListChildren(list, chatName, messages, maxMessages, includeDirection);
                if (messages.Count >= maxMessages)
                {
                    break;
                }
            }

            if (messages.Count >= maxMessages)
            {
                break;
            }
        }

        return messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .GroupBy(m => m.StableKey)
            .Select(g => g.First())
            .ToList();
    }


    public WeChatChatViewportState GetCurrentChatViewportState()
    {
        try
        {
            var chatWindows = FindCandidateWindows(includeUiaRootFallback: false)
                .Where(w => w.Description.Contains("mmui::ChatSingleWindow", StringComparison.OrdinalIgnoreCase)
                            || w.Description.Contains("Qt51514QWindowIcon", StringComparison.OrdinalIgnoreCase)
                            || w.Description.Contains("mmui::MainWindow", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(w => w.Description.Contains("可见：是", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(w => w.Description.Contains("mmui::ChatSingleWindow", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(w => w.Description.Contains("mmui::MainWindow", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var window in chatWindows)
            {
                AutomationElement? element = null;
                try
                {
                    element = AutomationElement.FromHandle(window.Handle);
                }
                catch
                {
                    element = window.Element;
                }

                if (element == null)
                {
                    continue;
                }

                foreach (var list in FindChatMessageLists(element))
                {
                    if (TryGetScrollState(list, out var verticalPercent, out var verticalViewSize, out var scrollable))
                    {
                        if (!scrollable || verticalViewSize >= 99.9 || verticalPercent < 0)
                        {
                            return new WeChatChatViewportState(true, true, "微信聊天列表没有可滚动的历史区域，按接近底部处理。");
                        }

                        var isNearBottom = verticalPercent >= 97.0;
                        var summary = isNearBottom
                            ? $"微信聊天列表接近底部，滚动位置 {verticalPercent:0.0}%。允许朗读新消息。"
                            : $"微信聊天列表不在底部，滚动位置 {verticalPercent:0.0}%。判断您可能正在查看历史消息，本次不朗读可见旧消息。";
                        return new WeChatChatViewportState(true, isNearBottom, summary);
                    }
                }
            }

            return new WeChatChatViewportState(false, true, "没有读取到微信聊天列表滚动位置，保持原来的朗读策略。");
        }
        catch (Exception ex)
        {
            return new WeChatChatViewportState(false, true, "读取微信聊天列表滚动位置失败，保持原来的朗读策略。错误：" + ex.Message);
        }
    }

    private static bool TryGetScrollState(AutomationElement element, out double verticalPercent, out double verticalViewSize, out bool verticallyScrollable)
    {
        verticalPercent = -1;
        verticalViewSize = 100;
        verticallyScrollable = false;
        try
        {
            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && pattern is ScrollPattern scrollPattern)
            {
                verticalPercent = scrollPattern.Current.VerticalScrollPercent;
                verticalViewSize = scrollPattern.Current.VerticalViewSize;
                verticallyScrollable = scrollPattern.Current.VerticallyScrollable;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }


    public string BuildCurrentChatUiaMessageDiagnostics(int maxItems = 12, int maxSiblingsPerItem = 12)
    {
        var builder = new StringBuilder();
        try
        {
            var chatWindows = FindCandidateWindows(includeUiaRootFallback: true)
                .Where(w => w.Description.Contains("mmui::ChatSingleWindow", StringComparison.OrdinalIgnoreCase)
                            || w.Description.Contains("Qt51514QWindowIcon", StringComparison.OrdinalIgnoreCase)
                            || w.Description.Contains("mmui::MainWindow", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(w => w.Description.Contains("mmui::ChatSingleWindow", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(w => w.Description.Contains("mmui::MainWindow", StringComparison.OrdinalIgnoreCase))
                .ToList();

            builder.AppendLine("\u5fae\u4fe1 UIA \u6d88\u606f\u5b50\u63a7\u4ef6\u8bca\u65ad\uff1a\u7528\u4e8e\u5206\u6790\u6d88\u606f\u6b63\u6587\u3001\u6635\u79f0\u6309\u94ae\u548c\u65b9\u5411\u5224\u65ad\u3002");
            builder.AppendLine("\u8bf4\u660e\uff1a\u5982\u679c\u6ca1\u6709\u51fa\u73b0\u5934\u50cf\u6216\u6635\u79f0\u6309\u94ae\uff0c\u5c31\u4e0d\u80fd\u53ea\u9760 UIA \u7a33\u5b9a\u533a\u5206\u6211\u548c\u5bf9\u65b9\u3002");
            builder.AppendLine("\u5019\u9009\u804a\u5929\u7a97\u53e3\u6570\u91cf\uff1a" + chatWindows.Count);

            foreach (var window in chatWindows.Take(2))
            {
                AutomationElement? root = null;
                try
                {
                    root = AutomationElement.FromHandle(window.Handle);
                }
                catch
                {
                    root = window.Element;
                }

                if (root == null)
                {
                    continue;
                }

                var chatName = TryGetChatName(root);
                builder.AppendLine("\u7a97\u53e3\uff1a" + window.Description);
                builder.AppendLine("\u804a\u5929\u540d\uff1a" + chatName);
                var lists = FindChatMessageLists(root);
                builder.AppendLine("\u6d88\u606f\u5217\u8868\u6570\u91cf\uff1a" + lists.Count);

                foreach (var list in lists.Take(2))
                {
                    var listRect = SafeGet(() => list.Current.BoundingRectangle);
                    builder.AppendLine($"\u6d88\u606f\u5217\u8868\uff1aAutomationId={SafeGet(() => list.Current.AutomationId)}\uff1bClass={SafeGet(() => list.Current.ClassName)}\uff1bRect={FormatRect(listRect)}");
                    AutomationElementCollection children;
                    try
                    {
                        children = list.FindAll(TreeScope.Children, Condition.TrueCondition);
                    }
                    catch (Exception ex)
                    {
                        builder.AppendLine("\u8bfb\u53d6\u6d88\u606f\u5217\u8868\u5b50\u63a7\u4ef6\u5931\u8d25\uff1a" + ex.Message);
                        continue;
                    }

                    builder.AppendLine("\u6d88\u606f\u5217\u8868\u76f4\u63a5\u5b50\u63a7\u4ef6\u6570\u91cf\uff1a" + children.Count);
                    var start = Math.Max(0, children.Count - Math.Max(maxItems, 1));
                    for (var i = start; i < children.Count; i++)
                    {
                        var child = children[i];
                        AppendElementDiagnostic(builder, child, $"\u6d88\u606f\u9879[{i}]", chatName, children, i);

                        AutomationElementCollection descendants;
                        try
                        {
                            descendants = child.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                        }
                        catch
                        {
                            continue;
                        }

                        var descendantCount = Math.Min(descendants.Count, maxSiblingsPerItem);
                        for (var d = 0; d < descendantCount; d++)
                        {
                            AppendElementDiagnostic(builder, descendants[d], $"  \u540e\u4ee3[{d}]", chatName, null, -1);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine("\u5fae\u4fe1 UIA \u6d88\u606f\u5b50\u63a7\u4ef6\u8bca\u65ad\u5931\u8d25\uff1a" + ex.Message);
        }

        return builder.ToString().Trim();
    }

    private static void AppendElementDiagnostic(StringBuilder builder, AutomationElement element, string prefix, string chatName, AutomationElementCollection? siblings, int index)
    {
        var name = NormalizeText(SafeGet(() => element.Current.Name) ?? string.Empty);
        var automationId = SafeGet(() => element.Current.AutomationId) ?? string.Empty;
        var className = SafeGet(() => element.Current.ClassName) ?? string.Empty;
        var controlType = SafeGet(() => element.Current.ControlType.ProgrammaticName) ?? string.Empty;
        var rect = SafeGet(() => element.Current.BoundingRectangle);
        var runtimeId = GetRuntimeIdText(element);
        var nearby = siblings != null && index >= 0 ? FindNearbySpeakerButtonName(siblings, index, name) : null;
        var nearbyText = nearby == null ? "\u65e0" : $"{nearby.Value.Name}@{nearby.Value.Index}";
        builder.AppendLine($"{prefix}\uff1bType={controlType}\uff1bClass={className}\uff1bAutomationId={automationId}\uff1bName={name}\uff1bRect={FormatRect(rect)}\uff1bRuntimeId={runtimeId}\uff1b\u9644\u8fd1\u6309\u94ae={nearbyText}\uff1b\u804a\u5929\u540d={chatName}");
    }

    private static string FormatRect(System.Windows.Rect rect)
    {
        if (rect.IsEmpty)
        {
            return "\u672a\u77e5";
        }

        return $"{rect.Left:0},{rect.Top:0},{rect.Right:0},{rect.Bottom:0}";
    }

    private static string TryGetChatName(AutomationElement root)
    {
        var title = SafeGet(() => root.Current.Name) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(title)
            && !title.Equals("Weixin", StringComparison.OrdinalIgnoreCase)
            && !title.Equals("\u5fae\u4fe1", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeText(title);
        }

        var texts = new List<string>();
        CollectTexts(root, texts, 60, TreeWalker.ControlViewWalker, "ControlView");
        foreach (var line in texts)
        {
            if (line.Contains("current_chat_name_label", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.LastIndexOf("\u6587\u672c\uff1a", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var name = line[(idx + 3)..].Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
            }
        }

        return "\u5f53\u524d\u804a\u5929";
    }

    private static IReadOnlyList<AutomationElement> FindChatMessageLists(AutomationElement root)
    {
        var result = new List<AutomationElement>();
        try
        {
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "chat_message_list");
            var lists = root.FindAll(TreeScope.Descendants, condition);
            foreach (AutomationElement list in lists)
            {
                var className = SafeGet(() => list.Current.ClassName) ?? string.Empty;
                var controlType = SafeGet(() => list.Current.ControlType.ProgrammaticName) ?? string.Empty;
                if (className.Contains("RecyclerListView", StringComparison.OrdinalIgnoreCase)
                    || controlType.Contains("List", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(list);
                }
            }
        }
        catch
        {
            // Ignore UIA search errors and return what we have.
        }

        // Some WeChat builds expose the root itself as the message list.
        try
        {
            if ((SafeGet(() => root.Current.AutomationId) ?? string.Empty).Equals("chat_message_list", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(root);
            }
        }
        catch
        {
            // Ignore.
        }

        return result.Distinct().ToList();
    }

    private static void CollectDirectMessageListChildren(AutomationElement messageList, string chatName, List<WeChatChatMessage> messages, int maxMessages, bool includeDirection)
    {
        var beforeCount = messages.Count;
        AutomationElementCollection children;
        try
        {
            children = messageList.FindAll(TreeScope.Children, Condition.TrueCondition);
        }
        catch
        {
            return;
        }

        var startIndex = Math.Max(0, children.Count - Math.Max(maxMessages * 2, 30));
        for (var index = startIndex; index < children.Count && messages.Count < maxMessages; index++)
        {
            TryAddTextMessage(children[index], messageList, chatName, messages, index, maxMessages, includeDirection, children);
        }

        // Fallback: some WeChat builds wrap the text item below the direct child.
        // The fallback is still limited inside chat_message_list, so it should not read the session list.
        if (messages.Count == beforeCount && messages.Count < maxMessages)
        {
            CollectDescendantTextItems(messageList, chatName, messages, maxMessages, includeDirection);
        }
    }

    private static void CollectDescendantTextItems(AutomationElement messageList, string chatName, List<WeChatChatMessage> messages, int maxMessages, bool includeDirection)
    {
        AutomationElementCollection descendants;
        try
        {
            descendants = messageList.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        }
        catch
        {
            return;
        }

        var startIndex = Math.Max(0, descendants.Count - Math.Max(maxMessages * 3, 60));
        for (var index = startIndex; index < descendants.Count && messages.Count < maxMessages; index++)
        {
            TryAddTextMessage(descendants[index], messageList, chatName, messages, index, maxMessages, includeDirection, null);
        }
    }

    private static void TryAddTextMessage(AutomationElement item, AutomationElement messageList, string chatName, List<WeChatChatMessage> messages, int index, int maxMessages, bool includeDirection, AutomationElementCollection? siblings)
    {
        if (messages.Count >= maxMessages)
        {
            return;
        }

        var automationId = SafeGet(() => item.Current.AutomationId) ?? string.Empty;
        var className = SafeGet(() => item.Current.ClassName) ?? string.Empty;
        var controlType = SafeGet(() => item.Current.ControlType.ProgrammaticName) ?? string.Empty;
        var name = NormalizeText(SafeGet(() => item.Current.Name) ?? string.Empty);

        if (!IsWeChatTextMessageItem(automationId, className, controlType, name))
        {
            return;
        }

        const string messageType = "\u6587\u5b57";
        var runtimeId = GetRuntimeIdText(item);
        // Include both RuntimeId and current text. Some WeChat virtualized list items can be reused
        // while their displayed text changes; using RuntimeId + text avoids "reads once, then stops".
        // Do not include the volatile list index when RuntimeId exists, otherwise the same visible
        // message may be announced repeatedly when WeChat recycles or reorders virtualized rows.
        var stableKey = !string.IsNullOrWhiteSpace(runtimeId)
            ? $"{chatName}|{messageType}|runtime|{runtimeId}|text|{name}"
            : $"{chatName}|{messageType}|index|{index}|text|{name}";

        if (messages.Any(m => m.StableKey.Equals(stableKey, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Stable text-only reading path: skip speaker and direction detection to avoid wrong attribution and reduce polling work.
        var speakerName = string.Empty;
        var direction = includeDirection
            ? DetermineMessageDirection(item, messageList, name, chatName, siblings, index)
            : (false, "\u6b63\u5f0f\u6717\u8bfb\u8def\u5f84\u53ea\u8bfb\u6587\u5b57\u6b63\u6587\uff0c\u5df2\u8df3\u8fc7\u8bf4\u8bdd\u4eba\u548c\u65b9\u5411\u5224\u65ad");
        messages.Add(new WeChatChatMessage(chatName, name, messageType, stableKey, direction.Item1, direction.Item2, speakerName));
    }

    private static (bool IsOutgoing, string Debug) DetermineMessageDirection(AutomationElement item, AutomationElement messageList, string messageText, string chatName, AutomationElementCollection? siblings, int siblingIndex)
    {
        try
        {
            var listRect = messageList.Current.BoundingRectangle;
            if (listRect.IsEmpty || listRect.Width <= 0)
            {
                return (false, "\u63a7\u4ef6\u4f4d\u7f6e\u5224\u65ad\uff1a\u6d88\u606f\u5217\u8868\u77e9\u5f62\u65e0\u6548");
            }

            var listCenterX = listRect.Left + listRect.Width / 2.0;
            var threshold = Math.Max(12.0, listRect.Width * 0.06);

            var siblingDirection = DetermineDirectionByNearbySpeakerButton(siblings, siblingIndex, chatName, messageText);
            if (siblingDirection.HasValue)
            {
                return siblingDirection.Value;
            }

            // Do not use the message list item's clickable point first: in WeChat 4.x it is often
            // exposed as the center of the whole row, so every message becomes x == list center.
            // Prefer the real text/bubble descendant rectangle; that is more likely to sit left or right.
            var descendantRect = FindBestDescendantRect(item, messageText, listRect);
            if (descendantRect.HasValue)
            {
                return DecideByRect(descendantRect.Value, listCenterX, threshold, "\u5b50\u63a7\u4ef6\u6587\u5b57\u6216\u6c14\u6ce1\u77e9\u5f62", listRect);
            }

            var itemRect = item.Current.BoundingRectangle;
            if (!itemRect.IsEmpty && itemRect.Width > 0)
            {
                var itemCenterX = itemRect.Left + itemRect.Width / 2.0;
                if (itemRect.Width >= listRect.Width * 0.80)
                {
                    var clickablePoint = FindReliableClickablePoint(item, listCenterX, threshold);
                    if (clickablePoint.HasValue)
                    {
                        return DecideByPoint(clickablePoint.Value.X, listCenterX, threshold, "\u6d88\u606f\u9879\u6216\u5b50\u63a7\u4ef6\u53ef\u70b9\u51fb\u4f4d\u7f6e", listRect);
                    }

                    return (false, $"\u63a7\u4ef6\u4f4d\u7f6e\u5224\u65ad\uff1a\u6d88\u606f\u9879\u51e0\u4e4e\u5360\u6ee1\u5217\u8868\uff0c\u5b50\u63a7\u4ef6\u548c\u53ef\u70b9\u51fb\u4f4d\u7f6e\u4e5f\u4e0d\u8db3\u4ee5\u533a\u5206\uff1bitem\uff1a{itemRect.Left:0}-{itemRect.Right:0}\uff1blist\uff1a{listRect.Left:0}-{listRect.Right:0}");
                }

                return DecideByRect(itemRect, listCenterX, threshold, "\u6d88\u606f\u9879\u77e9\u5f62", listRect);
            }

            return (false, "\u63a7\u4ef6\u4f4d\u7f6e\u5224\u65ad\uff1a\u6d88\u606f\u9879\u77e9\u5f62\u65e0\u6548");
        }
        catch (Exception ex)
        {
            return (false, "\u63a7\u4ef6\u4f4d\u7f6e\u5224\u65ad\u5931\u8d25\uff1a" + ex.Message);
        }
    }



    private static string GetNearbySpeakerName(AutomationElementCollection? siblings, int messageIndex, string messageText)
    {
        if (siblings == null || messageIndex < 0 || messageIndex >= siblings.Count)
        {
            return string.Empty;
        }

        var nearby = FindNearbySpeakerButtonName(siblings, messageIndex, messageText);
        return nearby == null ? string.Empty : NormalizeSpeakerName(nearby.Value.Name);
    }

    private static (bool IsOutgoing, string Debug)? DetermineDirectionByNearbySpeakerButton(AutomationElementCollection? siblings, int siblingIndex, string chatName, string messageText)
    {
        if (siblings == null || siblingIndex < 0 || siblingIndex >= siblings.Count)
        {
            return null;
        }

        var normalizedChatName = NormalizeSpeakerName(chatName);
        if (string.IsNullOrWhiteSpace(normalizedChatName))
        {
            return null;
        }

        // Conservative rule: only enable nickname-based direction when the current chat name
        // appears as a nearby sender/avatar button somewhere in this message list. This usually
        // means a one-to-one chat. In group chats the chat name is the group name, not a sender,
        // so we avoid marking every group member as "me".
        if (!ListContainsSpeakerButtonName(siblings, normalizedChatName))
        {
            return null;
        }

        var nearby = FindNearbySpeakerButtonName(siblings, siblingIndex, messageText);
        if (nearby == null)
        {
            return null;
        }

        var speakerName = NormalizeSpeakerName(nearby.Value.Name);
        if (string.IsNullOrWhiteSpace(speakerName))
        {
            return null;
        }

        var isIncoming = speakerName.Equals(normalizedChatName, StringComparison.OrdinalIgnoreCase);
        if (isIncoming)
        {
            return (false, $"\u6635\u79f0\u6309\u94ae\u5224\u65ad\uff1a\u5bf9\u65b9\u53d1\u6765\uff1b\u8bf4\u8bdd\u4eba={speakerName}\uff1b\u804a\u5929\u540d={normalizedChatName}\uff1b\u6309\u94ae\u7d22\u5f15={nearby.Value.Index}\uff1b\u6d88\u606f\u7d22\u5f15={siblingIndex}");
        }

        return (true, $"\u6635\u79f0\u6309\u94ae\u5224\u65ad\uff1a\u6211\u53d1\u51fa\uff1b\u9644\u8fd1\u6309\u94ae={speakerName}\uff1b\u804a\u5929\u540d={normalizedChatName}\uff1b\u6309\u94ae\u7d22\u5f15={nearby.Value.Index}\uff1b\u6d88\u606f\u7d22\u5f15={siblingIndex}");
    }

    private static bool ListContainsSpeakerButtonName(AutomationElementCollection siblings, string normalizedName)
    {
        for (var i = 0; i < siblings.Count; i++)
        {
            var element = siblings[i];
            if (!IsLikelySpeakerButton(element, string.Empty, out var name))
            {
                continue;
            }

            if (NormalizeSpeakerName(name).Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (string Name, int Index)? FindNearbySpeakerButtonName(AutomationElementCollection siblings, int messageIndex, string messageText)
    {
        const int searchRadius = 5;
        for (var distance = 1; distance <= searchRadius; distance++)
        {
            var before = messageIndex - distance;
            if (before >= 0 && IsLikelySpeakerButton(siblings[before], messageText, out var beforeName))
            {
                return (beforeName, before);
            }

            var after = messageIndex + distance;
            if (after < siblings.Count && IsLikelySpeakerButton(siblings[after], messageText, out var afterName))
            {
                return (afterName, after);
            }
        }

        return null;
    }

    private static bool IsLikelySpeakerButton(AutomationElement element, string messageText, out string name)
    {
        name = NormalizeText(SafeGet(() => element.Current.Name) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var controlType = SafeGet(() => element.Current.ControlType.ProgrammaticName) ?? string.Empty;
        var className = SafeGet(() => element.Current.ClassName) ?? string.Empty;
        var automationId = SafeGet(() => element.Current.AutomationId) ?? string.Empty;

        var looksLikeButton = controlType.Contains("Button", StringComparison.OrdinalIgnoreCase)
            || className.Contains("Button", StringComparison.OrdinalIgnoreCase)
            || automationId.Contains("avatar", StringComparison.OrdinalIgnoreCase)
            || automationId.Contains("sender", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeButton)
        {
            return false;
        }

        if (ShouldIgnoreSpeakerButtonName(name) || name.Equals(NormalizeText(messageText), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool ShouldIgnoreSpeakerButtonName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var value = NormalizeText(name);
        if (ShouldIgnoreCandidateMessage(value))
        {
            return true;
        }

        var uiLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "\u53d1\u9001", "\u641c\u7d22", "\u66f4\u591a", "\u5173\u95ed", "\u6700\u5c0f\u5316", "\u6700\u5927\u5316", "\u7f6e\u9876", "\u804a\u5929\u8bb0\u5f55", "\u8bed\u97f3\u901a\u8bdd", "\u89c6\u9891\u901a\u8bdd", "\u8868\u60c5", "\u6587\u4ef6"
        };

        if (uiLabels.Contains(value))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeSpeakerName(string name)
    {
        var value = NormalizeText(name ?? string.Empty).Trim();
        if (value.EndsWith(" \u5934\u50cf", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^3].Trim();
        }
        if (value.EndsWith("\u5934\u50cf", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^2].Trim();
        }
        return value.Trim('\uff1a', ':', '\uff1b', ';', '\u3002', '.', '\uff0c', ',', ' ');
    }

    private static (bool IsOutgoing, string Debug) DecideByRect(System.Windows.Rect rect, double listCenterX, double threshold, string source, System.Windows.Rect listRect)
    {
        var centerX = rect.Left + rect.Width / 2.0;
        if (centerX > listCenterX + threshold || (rect.Left > listCenterX && rect.Right > listCenterX + threshold))
        {
            return (true, $"\u65b9\u5411=\u6211\u53d1\u51fa\uff1b\u4f9d\u636e={source}\uff1brect={FormatRect(rect)}\uff1b\u5217\u8868\u4e2d\u5fc3={listCenterX:0}\uff1blist={listRect.Left:0}-{listRect.Right:0}");
        }

        if (centerX < listCenterX - threshold || (rect.Right < listCenterX && rect.Left < listCenterX - threshold))
        {
            return (false, $"\u65b9\u5411=\u5bf9\u65b9\u53d1\u6765\uff1b\u4f9d\u636e={source}\uff1brect={FormatRect(rect)}\uff1b\u5217\u8868\u4e2d\u5fc3={listCenterX:0}\uff1blist={listRect.Left:0}-{listRect.Right:0}");
        }

        return (false, $"\u65b9\u5411\u4e0d\u786e\u5b9a\uff0c\u9ed8\u8ba4\u5f53\u4f5c\u5bf9\u65b9\u53d1\u6765\uff1b\u4f9d\u636e={source}\uff1brect={FormatRect(rect)}\uff1b\u5217\u8868\u4e2d\u5fc3={listCenterX:0}\uff1blist={listRect.Left:0}-{listRect.Right:0}");
    }

    private static (bool IsOutgoing, string Debug) DecideByPoint(double x, double listCenterX, double threshold, string source, System.Windows.Rect listRect)
    {
        if (x > listCenterX + threshold)
        {
            return (true, $"\u65b9\u5411=\u6211\u53d1\u51fa\uff1b\u4f9d\u636e={source}\uff1bx={x:0}\uff1b\u5217\u8868\u4e2d\u5fc3={listCenterX:0}\uff1blist={listRect.Left:0}-{listRect.Right:0}");
        }

        if (x < listCenterX - threshold)
        {
            return (false, $"\u65b9\u5411=\u5bf9\u65b9\u53d1\u6765\uff1b\u4f9d\u636e={source}\uff1bx={x:0}\uff1b\u5217\u8868\u4e2d\u5fc3={listCenterX:0}\uff1blist={listRect.Left:0}-{listRect.Right:0}");
        }

        return (false, $"\u65b9\u5411\u4e0d\u786e\u5b9a\uff0c\u9ed8\u8ba4\u5f53\u4f5c\u5bf9\u65b9\u53d1\u6765\uff1b\u4f9d\u636e={source}\uff1bx={x:0}\uff1b\u5217\u8868\u4e2d\u5fc3={listCenterX:0}\uff1blist={listRect.Left:0}-{listRect.Right:0}");
    }

    private static bool TryGetElementClickablePoint(AutomationElement element, out System.Windows.Point point)
    {
        try
        {
            return element.TryGetClickablePoint(out point);
        }
        catch
        {
            point = default;
            return false;
        }
    }

    private static System.Windows.Rect? FindBestDescendantRect(AutomationElement item, string messageText, System.Windows.Rect listRect)
    {
        AutomationElementCollection descendants;
        try
        {
            descendants = item.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        }
        catch
        {
            return null;
        }

        var exactMatches = new List<(System.Windows.Rect Rect, double Area, double Width)>();
        var namedCandidates = new List<(System.Windows.Rect Rect, double Area, double Width)>();

        foreach (AutomationElement descendant in descendants)
        {
            var text = NormalizeText(GetElementTextValue(descendant));
            var rect = SafeGet(() => descendant.Current.BoundingRectangle);
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            var area = rect.Width * rect.Height;
            if (!string.IsNullOrWhiteSpace(messageText) && text.Equals(messageText, StringComparison.OrdinalIgnoreCase))
            {
                exactMatches.Add((rect, area, rect.Width));
            }
            else if (!string.IsNullOrWhiteSpace(text) || IsLikelyMessageBubbleElement(descendant))
            {
                namedCandidates.Add((rect, area, rect.Width));
            }
        }

        if (exactMatches.Count > 0)
        {
            return exactMatches
                .OrderBy(c => c.Width >= listRect.Width * 0.80)
                .ThenBy(c => c.Area)
                .First().Rect;
        }

        if (namedCandidates.Count > 0)
        {
            return namedCandidates
                .OrderBy(c => c.Width >= listRect.Width * 0.80)
                .ThenBy(c => c.Area)
                .First().Rect;
        }

        return null;
    }

    private static bool IsLikelyMessageBubbleElement(AutomationElement element)
    {
        var automationId = SafeGet(() => element.Current.AutomationId) ?? string.Empty;
        var className = SafeGet(() => element.Current.ClassName) ?? string.Empty;
        return automationId.Contains("chat_bubble", StringComparison.OrdinalIgnoreCase)
            || automationId.Contains("bubble", StringComparison.OrdinalIgnoreCase)
            || className.Contains("ChatTextItemView", StringComparison.OrdinalIgnoreCase)
            || className.Contains("Bubble", StringComparison.OrdinalIgnoreCase);
    }

    private static System.Windows.Point? FindReliableClickablePoint(AutomationElement item, double listCenterX, double threshold)
    {
        if (TryGetElementClickablePoint(item, out var itemPoint) && Math.Abs(itemPoint.X - listCenterX) > threshold)
        {
            return itemPoint;
        }

        AutomationElementCollection descendants;
        try
        {
            descendants = item.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        }
        catch
        {
            return null;
        }

        for (var i = 0; i < descendants.Count; i++)
        {
            if (TryGetElementClickablePoint(descendants[i], out var point) && Math.Abs(point.X - listCenterX) > threshold)
            {
                return point;
            }
        }

        return null;
    }

    private static string GetElementTextValue(AutomationElement element)
    {
        var name = SafeGet(() => element.Current.Name) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
            {
                return valuePattern.Current.Value ?? string.Empty;
            }
        }
        catch
        {
            // Ignore ValuePattern errors.
        }

        return string.Empty;
    }

    private static bool IsWeChatTextMessageItem(string automationId, string className, string controlType, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var isListItem = controlType.Contains("ListItem", StringComparison.OrdinalIgnoreCase);
        var isMessageItem = automationId.Equals("chat_message_list.qt_scrollarea_viewport.chat_bubble_item_view", StringComparison.OrdinalIgnoreCase);
        var isTextMessageClass = className.Contains("ChatTextItemView", StringComparison.OrdinalIgnoreCase);
        var isVoiceToTextLike = LooksLikeVoiceToTextMessage(text);

        // PC WeChat exposes messages under chat_message_list.
        // Text messages use ChatTextItemView. Some voice messages that have been converted
        // to text may use another Chat*ItemView class, so allow them only when the visible
        // text clearly contains a voice-duration prefix plus converted text.
        if (!isListItem || (!isTextMessageClass && !isVoiceToTextLike))
        {
            return false;
        }

        if (!isMessageItem && !string.IsNullOrWhiteSpace(automationId))
        {
            return false;
        }

        return !ShouldIgnoreCandidateMessage(text);
    }


    private static bool LooksLikeVoiceToTextMessage(string text)
    {
        var value = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(value) || !value.Contains("语音", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasDuration = System.Text.RegularExpressions.Regex.IsMatch(value, "语音\\s*\\d{1,3}\\s*(秒|\"|”|')");
        if (!hasDuration)
        {
            return false;
        }

        // Only treat it as readable when there is actual converted text after the duration.
        var withoutDuration = System.Text.RegularExpressions.Regex.Replace(value, "^\\s*语音\\s*\\d{1,3}\\s*(秒|\"|”|')\\s*", string.Empty).Trim();
        return withoutDuration.Length >= 2;
    }

    private static bool ShouldIgnoreCandidateMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var value = NormalizeText(text);

        if (value.Contains("\u6d88\u606f\u514d\u6253\u6270", StringComparison.OrdinalIgnoreCase)
            || value.Contains("[\u8bed\u97f3]", StringComparison.OrdinalIgnoreCase)
            || value.Contains("[\u56fe\u7247]", StringComparison.OrdinalIgnoreCase)
            || value.Contains("[\u89c6\u9891]", StringComparison.OrdinalIgnoreCase)
            || value.Contains("[\u6587\u4ef6]", StringComparison.OrdinalIgnoreCase)
            || value.Contains("[\u52a8\u753b\u8868\u60c5]", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(value, @"\[\d+\u6761\]"))
        {
            return true;
        }
        var uiLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "\u5fae\u4fe1", "Weixin", "\u6d88\u606f", "\u8f93\u5165", "\u53d1\u9001", "\u804a\u5929\u4fe1\u606f", "\u804a\u5929\u8bb0\u5f55", "\u7f6e\u9876", "\u6700\u5c0f\u5316", "\u6700\u5927\u5316", "\u5173\u95ed",
            "\u53d1\u9001\u8868\u60c5(Alt+E)", "\u53d1\u9001\u6536\u85cf", "\u53d1\u9001\u6587\u4ef6", "\u622a\u56fe", "\u9690\u85cf\u7a97\u53e3\u622a\u56fe", "\u5fae\u4fe1\u8bed\u97f3\u8f93\u5165\u6587\u5b57", "\u53d1\u8bed\u97f3 ( \u6309\u4f4f\u5de6 Alt )"
        };

        if (uiLabels.Contains(value))
        {
            return true;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{1,2}:\d{2}$"))
        {
            return true;
        }

        return false;
    }

    private static string GetRuntimeIdText(AutomationElement element)
    {
        try
        {
            var runtimeId = element.GetRuntimeId();
            return runtimeId == null || runtimeId.Length == 0 ? string.Empty : string.Join(".", runtimeId);
        }
        catch
        {
            return string.Empty;
        }
    }
    private static List<WindowCandidate> FindCandidateWindows(bool includeUiaRootFallback)
    {
        var result = new List<WindowCandidate>();
        var processIds = GetWeChatProcessIds();

        var currentProcessId = Process.GetCurrentProcess().Id;
        foreach (var info in EnumerateTopLevelWindows())
        {
            if (info.ProcessId == currentProcessId)
            {
                continue;
            }

            var text = $"{info.Title} {info.ClassName}";
            var matchesText = WeChatTextKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
            var matchesProcess = processIds.Contains(info.ProcessId);
            var classLooksLikeWeChat = info.ClassName.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
                                   || info.ClassName.Contains("Weixin", StringComparison.OrdinalIgnoreCase)
                                   || info.ClassName.Contains("ChatWnd", StringComparison.OrdinalIgnoreCase)
                                   || info.ClassName.Contains("Qt", StringComparison.OrdinalIgnoreCase)
                                   || info.ClassName.Contains("Chrome", StringComparison.OrdinalIgnoreCase);

            if (matchesText || (matchesProcess && (info.IsVisible || !string.IsNullOrWhiteSpace(info.Title))) || (matchesProcess && classLooksLikeWeChat))
            {
                var description = $"顶层窗口：标题：{EmptyToUnknown(info.Title)}；类名：{EmptyToUnknown(info.ClassName)}；进程ID：{info.ProcessId}；可见：{(info.IsVisible ? "是" : "否")}；句柄：0x{info.Handle.ToInt64():X}";
                result.Add(new WindowCandidate(info.Handle, null, description));
            }
        }

        // UIA 根节点补充扫描，防止 Win32 枚举漏掉特殊窗口。
        if (!includeUiaRootFallback)
        {
            return result;
        }

        try
        {
            var root = AutomationElement.RootElement;
            var children = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement element in children)
            {
                var name = SafeGet(() => element.Current.Name) ?? string.Empty;
                var className = SafeGet(() => element.Current.ClassName) ?? string.Empty;
                var processId = SafeGet(() => element.Current.ProcessId);
                if (processId == currentProcessId)
                {
                    continue;
                }

                var nativeHandle = new IntPtr(SafeGet(() => element.Current.NativeWindowHandle));
                var text = $"{name} {className}";

                var matchesText = WeChatTextKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
                var matchesProcess = processIds.Contains(processId);

                if ((matchesText || matchesProcess) && result.All(r => r.Handle != nativeHandle))
                {
                    result.Add(new WindowCandidate(nativeHandle, element, DescribeElement(element)));
                }
            }
        }
        catch
        {
            // 忽略 UIA 根节点枚举失败。
        }

        return result;
    }

    private static List<string> DescribeWeChatProcesses()
    {
        var descriptions = new List<string>();
        foreach (var process in Process.GetProcesses().OrderBy(p => p.ProcessName))
        {
            try
            {
                var name = process.ProcessName;
                if (name.Contains("WeChatMessageReaderAssistant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!WeChatProcessKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var title = process.MainWindowTitle;
                string path;
                try { path = process.MainModule?.FileName ?? "未知"; }
                catch { path = "无权限读取"; }

                descriptions.Add($"微信相关进程：进程名：{name}；进程ID：{process.Id}；主窗口标题：{EmptyToUnknown(title)}；路径：{path}");
            }
            catch
            {
                // 忽略无法读取的进程。
            }
            finally
            {
                process.Dispose();
            }
        }

        if (descriptions.Count == 0)
        {
            descriptions.Add("没有发现进程名包含 WeChat、Weixin、WeChatAppEx 的微信相关进程。");
        }

        return descriptions;
    }

    private static HashSet<int> GetWeChatProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (name.Contains("WeChatMessageReaderAssistant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (WeChatProcessKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    ids.Add(process.Id);
                }
            }
            catch
            {
                // 忽略无法读取的进程。
            }
            finally
            {
                process.Dispose();
            }
        }

        return ids;
    }

    private static void CollectTexts(AutomationElement root, List<string> texts, int maxTextCount, TreeWalker walker, string viewName)
    {
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));

        var visited = 0;
        const int maxVisited = 6000;
        const int maxDepth = 20;

        while (queue.Count > 0 && texts.Count < maxTextCount && visited < maxVisited)
        {
            visited++;
            var (current, depth) = queue.Dequeue();
            AddElementText(current, texts, maxTextCount, viewName, depth);

            if (depth >= maxDepth)
            {
                continue;
            }

            AutomationElement? child = null;
            try
            {
                child = walker.GetFirstChild(current);
            }
            catch
            {
                child = null;
            }

            while (child != null && texts.Count < maxTextCount && visited < maxVisited)
            {
                queue.Enqueue((child, depth + 1));
                try
                {
                    child = walker.GetNextSibling(child);
                }
                catch
                {
                    child = null;
                }
            }
        }
    }

    private static void AddElementText(AutomationElement element, List<string> texts, int maxTextCount, string viewName, int depth)
    {
        if (texts.Count >= maxTextCount)
        {
            return;
        }

        var name = SafeGet(() => element.Current.Name)?.Trim() ?? string.Empty;
        var automationId = SafeGet(() => element.Current.AutomationId)?.Trim() ?? string.Empty;
        var className = SafeGet(() => element.Current.ClassName)?.Trim() ?? string.Empty;
        var controlType = SafeGet(() => element.Current.ControlType.ProgrammaticName)?.Replace("ControlType.", string.Empty) ?? string.Empty;

        var value = string.Empty;
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
            {
                value = valuePattern.Current.Value?.Trim() ?? string.Empty;
            }
        }
        catch
        {
            value = string.Empty;
        }

        var candidates = new[] { name, value }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeText)
            .Where(s => s.Length >= 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var candidate in candidates)
        {
            var line = $"{viewName} 深度{depth} 类型{EmptyToUnknown(controlType)} 类名{EmptyToUnknown(className)} 自动化ID{EmptyToUnknown(automationId)} 文本：{candidate}";
            if (texts.Any(t => string.Equals(t, line, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            texts.Add(line);
            if (texts.Count >= maxTextCount)
            {
                return;
            }
        }
    }

    private static string DescribeElement(AutomationElement element)
    {
        var name = SafeGet(() => element.Current.Name) ?? string.Empty;
        var className = SafeGet(() => element.Current.ClassName) ?? string.Empty;
        var processId = SafeGet(() => element.Current.ProcessId);
        var controlType = SafeGet(() => element.Current.ControlType.ProgrammaticName) ?? string.Empty;
        var handle = SafeGet(() => element.Current.NativeWindowHandle);
        return $"UIA窗口：名称：{EmptyToUnknown(name)}；类名：{EmptyToUnknown(className)}；进程ID：{processId}；控件类型：{EmptyToUnknown(controlType)}；句柄：0x{handle:X}";
    }

    private static IReadOnlyList<WindowInfo> EnumerateTopLevelWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var processId);
            var title = GetWindowText(hWnd);
            var className = GetClassName(hWnd);
            var isVisible = IsWindowVisible(hWnd);
            windows.Add(new WindowInfo(hWnd, (int)processId, title, className, isVisible));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static string GetWindowText(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var builder = new StringBuilder(256);
        _ = GetClassName(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string NormalizeText(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasWhiteSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhiteSpace)
                {
                    builder.Append(' ');
                    lastWasWhiteSpace = true;
                }
            }
            else
            {
                builder.Append(ch);
                lastWasWhiteSpace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static string EmptyToUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "未知" : value;

    private static T? SafeGet<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return default;
        }
    }

    private sealed record WindowCandidate(IntPtr Handle, AutomationElement? Element, string Description);
    private sealed record WindowInfo(IntPtr Handle, int ProcessId, string Title, string ClassName, bool IsVisible);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}







