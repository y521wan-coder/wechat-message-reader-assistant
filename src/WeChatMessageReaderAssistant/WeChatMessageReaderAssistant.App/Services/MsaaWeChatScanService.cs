using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;

namespace WeChatMessageReaderAssistant.App.Services;

public sealed record MsaaScanResult(string Summary, IReadOnlyList<string> Lines, IReadOnlyList<string> Highlights);

public sealed class MsaaWeChatScanService
{
    private const uint OBJID_CLIENT = 0xFFFFFFFC;
    private static readonly Guid IidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    private static readonly string[] WeChatProcessKeywords =
    {
        "wechat",
        "weixin",
        "wechatappex"
    };

    public MsaaScanResult ScanWeChatWindows(int maxLines = 2000, int maxDepth = 25)
    {
        var lines = new List<string>();
        var windows = EnumerateWeChatWindowsIncludingChildren()
            .Where(w => w.ProcessId != Process.GetCurrentProcess().Id)
            .OrderByDescending(IsLikelyChatWindow)
            .ThenByDescending(w => w.IsVisible && !string.IsNullOrWhiteSpace(w.Title) && !w.Title.Equals("Weixin", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.IsVisible)
            .ThenByDescending(w => w.ClassName.Contains("Chat", StringComparison.OrdinalIgnoreCase) || w.Title.Contains("Chat", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.ClassName.Contains("mmui", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.ClassName.Contains("Qt", StringComparison.OrdinalIgnoreCase))
            .ThenBy(w => w.Title)
            .ThenBy(w => w.Handle.ToInt64())
            .ToList();

        if (windows.Count == 0)
        {
            return new MsaaScanResult("MSAA did not find WeChat related windows. Please open PC WeChat chat window first.", lines, Array.Empty<string>());
        }

        foreach (var window in windows)
        {
            if (lines.Count >= maxLines)
            {
                break;
            }

            lines.Add($"Window: Kind={window.Kind}; Depth={window.WindowDepth}; Title={EmptyToUnknown(window.Title)}; Class={EmptyToUnknown(window.ClassName)}; ProcessId={window.ProcessId}; Visible={(window.IsVisible ? "Yes" : "No")}; Hwnd=0x{window.Handle.ToInt64():X}; Parent=0x{window.ParentHandle.ToInt64():X}");
            try
            {
                var accessibleGuid = IidIAccessible;
                var hr = AccessibleObjectFromWindow(window.Handle, OBJID_CLIENT, ref accessibleGuid, out var accessibleObject);
                if (hr != 0 || accessibleObject is not IAccessible accessible)
                {
                    lines.Add($"  MSAA read failed: HRESULT=0x{hr:X8}");
                    continue;
                }

                CollectAccessible(accessible, 0, maxDepth, lines, maxLines, new HashSet<int>());
            }
            catch (Exception ex)
            {
                lines.Add("  MSAA read exception: " + ex.Message);
            }
        }

        var usefulCount = lines.Count(l =>
            l.Contains("Name=", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("Value=", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("Description=", StringComparison.OrdinalIgnoreCase));
        var highlights = BuildHighlights(lines);
        var summary = $"MSAA found {windows.Count} WeChat related windows, read {lines.Count} MSAA lines, {usefulCount} lines contain Name, Value or Description, {highlights.Count} highlighted candidate lines.";
        return new MsaaScanResult(summary, lines, highlights);
    }

    private static void CollectAccessible(IAccessible accessible, int depth, int maxDepth, List<string> lines, int maxLines, HashSet<int> visited)
    {
        if (lines.Count >= maxLines || depth > maxDepth)
        {
            return;
        }

        var id = RuntimeHelpersGetHashCode(accessible);
        if (!visited.Add(id))
        {
            return;
        }

        AddAccessibleLine(accessible, 0, depth, lines, maxLines);

        int childCount;
        try
        {
            childCount = accessible.accChildCount;
        }
        catch
        {
            return;
        }

        if (childCount <= 0)
        {
            return;
        }

        var children = new object[childCount];
        try
        {
            AccessibleChildren(accessible, 0, childCount, children, out var obtained);
            childCount = obtained;
        }
        catch
        {
            return;
        }

        for (var i = 0; i < childCount && lines.Count < maxLines; i++)
        {
            var child = children[i];
            if (child is IAccessible childAccessible)
            {
                CollectAccessible(childAccessible, depth + 1, maxDepth, lines, maxLines, visited);
            }
            else if (child is int childId)
            {
                AddAccessibleLine(accessible, childId, depth + 1, lines, maxLines);
            }
        }
    }

    private static void AddAccessibleLine(IAccessible accessible, int childId, int depth, List<string> lines, int maxLines)
    {
        if (lines.Count >= maxLines)
        {
            return;
        }

        var child = childId == 0 ? (object)0 : childId;
        var name = SafeGet(() => accessible.get_accName(child));
        var value = SafeGet(() => accessible.get_accValue(child));
        var description = SafeGet(() => accessible.get_accDescription(child));
        var roleObject = SafeGet(() => accessible.get_accRole(child));
        var role = roleObject?.ToString();
        var roleText = GetRoleTextSafe(roleObject);
        var state = SafeGet(() => accessible.get_accState(child)?.ToString());
        var defaultAction = SafeGet(() => accessible.get_accDefaultAction(child));
        var keyboardShortcut = SafeGet(() => accessible.get_accKeyboardShortcut(child));
        var location = GetLocationText(accessible, child);

        if (string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(value)
            && string.IsNullOrWhiteSpace(description)
            && string.IsNullOrWhiteSpace(defaultAction)
            && string.IsNullOrWhiteSpace(keyboardShortcut))
        {
            return;
        }

        lines.Add($"  Depth={depth}; ChildId={childId}; Role={EmptyToUnknown(role)}; RoleText={EmptyToUnknown(roleText)}; State={EmptyToUnknown(state)}; Location={location}; Name={EmptyToUnknown(NormalizeText(name))}; Value={EmptyToUnknown(NormalizeText(value))}; Description={EmptyToUnknown(NormalizeText(description))}; DefaultAction={EmptyToUnknown(NormalizeText(defaultAction))}; Shortcut={EmptyToUnknown(NormalizeText(keyboardShortcut))}");
    }


    private static IReadOnlyList<string> BuildHighlights(IReadOnlyList<string> lines)
    {
        var highlights = new List<string>();
        foreach (var line in lines)
        {
            if (highlights.Count >= 120)
            {
                break;
            }

            if (IsLikelyUsefulAccessibleLine(line))
            {
                highlights.Add(line);
            }
        }

        return highlights;
    }

    private static bool IsLikelyUsefulAccessibleLine(string line)
    {
        if (line.StartsWith("Window:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("TopWindow:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hasText = !line.Contains("Name=Unknown; Value=Unknown; Description=Unknown", StringComparison.OrdinalIgnoreCase);
        if (!hasText)
        {
            return false;
        }

        var isLikelyMessageOrSpeaker =
            line.Contains("RoleText=push button", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("RoleText=text", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("RoleText=static text", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("RoleText=editable text", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("RoleText=list item", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("RoleText=client", StringComparison.OrdinalIgnoreCase);

        var hasLocation = !line.Contains("Location=Unknown", StringComparison.OrdinalIgnoreCase);
        var hasNameOrValue = !line.Contains("Name=Unknown; Value=Unknown", StringComparison.OrdinalIgnoreCase);

        return isLikelyMessageOrSpeaker || (hasLocation && hasNameOrValue);
    }

    private static bool IsWeChatProcess(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            if (name.Contains("WeChatMessageReaderAssistant", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return WeChatProcessKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<WindowInfo> EnumerateWeChatWindowsIncludingChildren()
    {
        var result = new List<WindowInfo>();
        var seen = new HashSet<IntPtr>();
        foreach (var top in EnumerateTopLevelWindows().Where(w => IsWeChatProcess(w.ProcessId)))
        {
            AddWindowIfNew(result, seen, top);
            EnumerateChildWindowsRecursive(top.Handle, top.ProcessId, 1, result, seen);
        }

        return result;
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
            windows.Add(new WindowInfo(hWnd, IntPtr.Zero, 0, "Top", (int)processId, title, className, isVisible));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static void EnumerateChildWindowsRecursive(IntPtr parent, int expectedProcessId, int depth, List<WindowInfo> result, HashSet<IntPtr> seen)
    {
        if (depth > 8)
        {
            return;
        }

        EnumChildWindows(parent, (hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var processId);
            if ((int)processId != expectedProcessId)
            {
                return true;
            }

            var title = GetWindowText(hWnd);
            var className = GetClassName(hWnd);
            var isVisible = IsWindowVisible(hWnd);
            var window = new WindowInfo(hWnd, parent, depth, "Child", (int)processId, title, className, isVisible);
            AddWindowIfNew(result, seen, window);
            EnumerateChildWindowsRecursive(hWnd, expectedProcessId, depth + 1, result, seen);
            return true;
        }, IntPtr.Zero);
    }

    private static void AddWindowIfNew(List<WindowInfo> result, HashSet<IntPtr> seen, WindowInfo window)
    {
        if (window.Handle == IntPtr.Zero || !seen.Add(window.Handle))
        {
            return;
        }

        result.Add(window);
    }

    private static bool IsLikelyChatWindow(WindowInfo window)
    {
        return window.ClassName.Contains("ChatSingleWindow", StringComparison.OrdinalIgnoreCase)
            || window.ClassName.Contains("Chat", StringComparison.OrdinalIgnoreCase)
            || window.Title.Contains("ChatSingleWindow", StringComparison.OrdinalIgnoreCase)
            || window.Title.Contains("chat_message", StringComparison.OrdinalIgnoreCase)
            || window.Title.Contains("??", StringComparison.OrdinalIgnoreCase);
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


    private static string GetRoleTextSafe(object? roleObject)
    {
        if (roleObject == null)
        {
            return string.Empty;
        }

        if (!uint.TryParse(roleObject.ToString(), out var role))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(128);
        var length = GetRoleText(role, builder, (uint)builder.Capacity);
        return length == 0 ? string.Empty : builder.ToString();
    }

    private static string GetLocationText(IAccessible accessible, object child)
    {
        try
        {
            accessible.accLocation(out var left, out var top, out var width, out var height, child);
            return $"{left},{top},{width},{height}";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

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

    private static string EmptyToUnknown(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

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

    private static int RuntimeHelpersGetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);

    private sealed record WindowInfo(IntPtr Handle, IntPtr ParentHandle, int WindowDepth, string Kind, int ProcessId, string Title, string ClassName, bool IsVisible);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

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

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint dwObjectID, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppvObject);


    [DllImport("oleacc.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRoleText(uint lRole, StringBuilder lpszRole, uint cchRoleMax);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(IAccessible paccContainer, int iChildStart, int cChildren, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] rgvarChildren, out int pcObtained);
}
