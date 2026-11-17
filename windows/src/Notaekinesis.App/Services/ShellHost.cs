using System.Runtime.InteropServices;
using static Notaekinesis.App.Native;

namespace Notaekinesis.App.Services;

/// <summary>
/// A hidden Win32 window that owns what lives outside the app's windows: the tray icon,
/// the global hotkey, and the "show yourself" message a second launch sends. Its messages
/// arrive on the UI thread (WinUI pumps them), so handlers can touch XAML directly.
/// </summary>
internal sealed unsafe partial class ShellHost : IDisposable
{
    public const string ClassName = "Notaekinesis.Host";

    /// <summary>Posted by a second instance to ask this one to show its window.</summary>
    public const uint WM_APP_SHOW = WM_APP + 1;

    private const uint WM_TRAY = WM_APP + 2;
    private const int HotkeyId = 1;
    private const int CmdOpen = 1;
    private const int CmdQuickAdd = 2;
    private const int CmdQuit = 3;

    private static ShellHost? s_current;
    private static readonly uint WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

    private readonly nint _hwnd;
    private readonly nint _icon;
    private readonly string _tooltip;
    private bool _trayAdded;
    private bool _hotkey;

    public event EventHandler? OpenRequested;
    public event EventHandler? QuickAddRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler<Exception>? Error;

    /// <param name="instanceName">Window title, so a second launch can find exactly this instance.</param>
    public ShellHost(string instanceName, string iconPath, string tooltip)
    {
        s_current = this;
        _tooltip = tooltip;
        var module = GetModuleHandle(null);
        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = module,
                lpszClassName = className,
            };
            RegisterClassEx(&wc); // fails harmlessly if already registered
        }
        // A hidden top-level window rather than a message-only one: it must receive the
        // TaskbarCreated broadcast to put the icon back after Explorer restarts.
        _hwnd = CreateWindowEx(0, ClassName, instanceName, 0, 0, 0, 0, 0, 0, 0, module, 0);
        if (_hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());

        var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem());
        _icon = LoadImage(0, iconPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        AddTrayIcon();
    }

    /// <summary>Finds an already running instance and asks it to show itself.</summary>
    public static bool SignalExisting(string instanceName)
    {
        var hwnd = FindWindow(ClassName, instanceName);
        return hwnd != 0 && PostMessage(hwnd, WM_APP_SHOW, 0, 0);
    }

    /// <summary>Registers the global hotkey; false if another app already has it.</summary>
    public bool RegisterHotkey(uint modifiers, uint key)
    {
        if (_hotkey) UnregisterHotKey(_hwnd, HotkeyId);
        _hotkey = RegisterHotKey(_hwnd, HotkeyId, modifiers | MOD_NOREPEAT, key);
        return _hotkey;
    }

    private void AddTrayIcon()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = 0x1 | 0x2 | 0x4 | 0x80, // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP
            uCallbackMessage = WM_TRAY,
            hIcon = _icon,
        };
        var tip = _tooltip.AsSpan(0, Math.Min(_tooltip.Length, 127));
        tip.CopyTo(new Span<char>(data.szTip, 128));
        _trayAdded = ShellNotifyIcon(0 /* NIM_ADD */, &data);
        data.uVersion = 4; // NOTIFYICON_VERSION_4: clean click and keyboard events
        ShellNotifyIcon(4 /* NIM_SETVERSION */, &data);
    }

    private void RemoveTrayIcon()
    {
        if (!_trayAdded) return;
        var data = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _hwnd, uID = 1 };
        ShellNotifyIcon(2 /* NIM_DELETE */, &data);
        _trayAdded = false;
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, CmdOpen, "Open notaekinesis");
        AppendMenu(menu, 0, CmdQuickAdd, "Quick add\tWin+Shift+N");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, CmdQuit, "Quit");
        SetMenuDefaultItem(menu, CmdOpen, 0);
        // Without this the menu doesn't close when clicking elsewhere.
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenuEx(menu, 0x100 /* TPM_RETURNCMD */ | 0x2 /* TPM_RIGHTBUTTON */, x, y, _hwnd, 0);
        PostMessage(_hwnd, WM_NULL, 0, 0);
        DestroyMenu(menu);
        switch (cmd)
        {
            case CmdOpen:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case CmdQuickAdd:
                QuickAddRequested?.Invoke(this, EventArgs.Empty);
                break;
            case CmdQuit:
                QuitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private nint Handle(uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_HOTKEY && wParam == HotkeyId)
        {
            QuickAddRequested?.Invoke(this, EventArgs.Empty);
            return 0;
        }
        if (msg == WM_APP_SHOW)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
            return 0;
        }
        if (msg == WM_TRAY)
        {
            // Version 4: the event is in the low word of lParam, the anchor point in wParam.
            var ev = (uint)(lParam & 0xFFFF);
            if (ev is NIN_SELECT or NIN_KEYSELECT) OpenRequested?.Invoke(this, EventArgs.Empty);
            else if (ev == WM_CONTEXTMENU) ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
            return 0;
        }
        if (msg == WM_TASKBARCREATED)
        {
            _trayAdded = false;
            AddTrayIcon();
            return 0;
        }
        return DefWindowProc(_hwnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var host = s_current;
        if (host is null || hwnd != host._hwnd) return DefWindowProc(hwnd, msg, wParam, lParam);
        try
        {
            return host.Handle(msg, wParam, lParam);
        }
        catch (Exception e)
        {
            // An exception must never cross back into Win32.
            host.Error?.Invoke(host, e);
            return 0;
        }
    }

    public void Dispose()
    {
        if (_hotkey) UnregisterHotKey(_hwnd, HotkeyId);
        RemoveTrayIcon();
        if (_icon != 0) DestroyIcon(_icon);
        DestroyWindow(_hwnd);
        s_current = null;
    }
}
