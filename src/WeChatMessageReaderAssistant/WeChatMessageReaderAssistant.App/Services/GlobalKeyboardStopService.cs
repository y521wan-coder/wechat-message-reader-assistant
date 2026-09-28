using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WeChatMessageReaderAssistant.App.Services;

public sealed class GlobalKeyboardStopService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_UP = 0x26;
    private const int VK_DOWN = 0x28;
    private const int VK_PRIOR = 0x21;
    private const int VK_NEXT = 0x22;
    private const int VK_HOME = 0x24;
    private const int VK_END = 0x23;
    private const int VK_MENU = 0x12;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_SHIFT = 0x10;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_M = 0x4D;
    private const int VK_W = 0x57;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _controlIsDown;
    private string _globalMonitorHotkey = "Ctrl+Alt+M";
    private int _activeHotkeyMainKey;

    public event EventHandler? ControlKeyPressed;
    public event EventHandler? NavigationKeyPressed;
    public event EventHandler? GlobalMonitorHotkeyPressed;

    public GlobalKeyboardStopService()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
        {
            return;
        }

        _hookId = SetHook(_proc);
    }

    public void ConfigureGlobalMonitorHotkey(string? hotkey)
    {
        _globalMonitorHotkey = NormalizeGlobalMonitorHotkey(hotkey);
        _activeHotkeyMainKey = 0;
    }

    public void Stop()
    {
        if (_hookId == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _controlIsDown = false;
    }

    public void Dispose()
    {
        Stop();
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        var moduleHandle = currentModule == null ? IntPtr.Zero : GetModuleHandle(currentModule.ModuleName);
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, moduleHandle, 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vkCode = Marshal.ReadInt32(lParam);
            var isControl = vkCode == VK_CONTROL || vkCode == VK_LCONTROL || vkCode == VK_RCONTROL;
            var message = wParam.ToInt32();

            if (isControl && (message == WM_KEYDOWN || message == WM_SYSKEYDOWN))
            {
                if (!_controlIsDown)
                {
                    _controlIsDown = true;
                    ControlKeyPressed?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (isControl && (message == WM_KEYUP || message == WM_SYSKEYUP))
            {
                _controlIsDown = false;
            }
            else if ((message == WM_KEYDOWN || message == WM_SYSKEYDOWN) && IsNavigationKey(vkCode))
            {
                NavigationKeyPressed?.Invoke(this, EventArgs.Empty);
            }
            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
            {
                TryRaiseGlobalMonitorHotkey(vkCode);
            }
            else if (message == WM_KEYUP || message == WM_SYSKEYUP)
            {
                if (vkCode == _activeHotkeyMainKey)
                {
                    _activeHotkeyMainKey = 0;
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void TryRaiseGlobalMonitorHotkey(int vkCode)
    {
        if (_globalMonitorHotkey.Equals("Off", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TryGetHotkeyDefinition(_globalMonitorHotkey, out var requiredControl, out var requiredAlt, out var requiredShift, out var mainKey))
        {
            return;
        }

        if (vkCode != mainKey || _activeHotkeyMainKey == mainKey)
        {
            return;
        }

        if (IsControlDown() == requiredControl && IsAltDown() == requiredAlt && IsShiftDown() == requiredShift)
        {
            _activeHotkeyMainKey = mainKey;
            GlobalMonitorHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool TryGetHotkeyDefinition(string hotkey, out bool control, out bool alt, out bool shift, out int mainKey)
    {
        control = false;
        alt = false;
        shift = false;
        mainKey = 0;

        if (hotkey.Equals("Ctrl+Alt+M", StringComparison.OrdinalIgnoreCase))
        {
            control = true;
            alt = true;
            mainKey = VK_M;
            return true;
        }

        if (hotkey.Equals("Ctrl+Shift+M", StringComparison.OrdinalIgnoreCase))
        {
            control = true;
            shift = true;
            mainKey = VK_M;
            return true;
        }

        if (hotkey.Equals("Alt+Shift+M", StringComparison.OrdinalIgnoreCase))
        {
            alt = true;
            shift = true;
            mainKey = VK_M;
            return true;
        }

        if (hotkey.Equals("Ctrl+Alt+W", StringComparison.OrdinalIgnoreCase))
        {
            control = true;
            alt = true;
            mainKey = VK_W;
            return true;
        }

        return false;
    }

    private static string NormalizeGlobalMonitorHotkey(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return "Ctrl+Alt+M";
        }

        var normalized = hotkey.Trim().Replace("Control", "Ctrl", StringComparison.OrdinalIgnoreCase);
        if (normalized.Equals("Ctrl+Alt+M", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Ctrl+Shift+M", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Alt+Shift+M", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Ctrl+Alt+W", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
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

    private static bool IsNavigationKey(int vkCode)
    {
        return vkCode == VK_UP
            || vkCode == VK_DOWN
            || vkCode == VK_PRIOR
            || vkCode == VK_NEXT
            || vkCode == VK_HOME
            || vkCode == VK_END;
    }

    private static bool IsControlDown()
    {
        return IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);
    }

    private static bool IsAltDown()
    {
        return IsKeyDown(VK_MENU) || IsKeyDown(VK_LMENU) || IsKeyDown(VK_RMENU);
    }

    private static bool IsShiftDown()
    {
        return IsKeyDown(VK_SHIFT) || IsKeyDown(VK_LSHIFT) || IsKeyDown(VK_RSHIFT);
    }

    private static bool IsKeyDown(int vkCode)
    {
        return (GetAsyncKeyState(vkCode) & 0x8000) != 0;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
