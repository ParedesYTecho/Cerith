#nullable enable

using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Cerith.WidgetHost;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 1;
    private const uint NimAdd = 0, NimDelete = 2;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const uint WmLButtonUp = 0x202, WmRButtonUp = 0x205, WmNull = 0;
    private const uint MfString = 0, MfSeparator = 0x800;
    private const uint TpmRightButton = 2, TpmReturnCmd = 0x100;
    private const uint IdToggleVisible = 1, IdToggleCompact = 2, IdQuit = 3;
    private static readonly IntPtr IdiApplication = 32512;
    private static readonly UIntPtr SubclassId = 1;

    private readonly Window _window;
    private readonly Func<bool> _isCompact;
    private readonly Action _toggleCompact;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly SubclassProc _callback;
    private bool _disposed;

    internal TrayIcon(Window window, Func<bool> isCompact, Action toggleCompact)
    {
        _window = window;
        _isCompact = isCompact;
        _toggleCompact = toggleCompact;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _ownsIcon = ExtractIconExW(Environment.ProcessPath, 0, IntPtr.Zero, out _icon, 1) > 0 && _icon != IntPtr.Zero;
        if (!_ownsIcon) _icon = LoadIconW(IntPtr.Zero, IdiApplication);
        _callback = OnMessage;
        SetWindowSubclass(_hwnd, _callback, SubclassId, UIntPtr.Zero);
        AddIcon();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = NewData();
        Shell_NotifyIconW(NimDelete, ref data);
        RemoveWindowSubclass(_hwnd, _callback, SubclassId);
        if (_ownsIcon) DestroyIcon(_icon);
    }

    private NotifyIconData NewData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Hwnd = _hwnd,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = "Cerith",
    };

    private void AddIcon()
    {
        var data = NewData();
        Shell_NotifyIconW(NimAdd, ref data);
    }

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        if (message == CallbackMessage)
        {
            var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouse == WmLButtonUp) ToggleVisible();
            else if (mouse == WmRButtonUp) ShowMenu();
        }
        else if (message == _taskbarCreated) AddIcon();
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ToggleVisible()
    {
        if (_window.AppWindow.IsVisible) _window.AppWindow.Hide();
        else Show();
    }

    internal void Show()
    {
        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        _window.AppWindow.Show();
        SetForegroundWindow(_hwnd);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MfString, IdToggleVisible, _window.AppWindow.IsVisible ? "Hide" : "Show");
            AppendMenuW(menu, MfString, IdToggleCompact, _isCompact() ? "Expand" : "Compact");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, IdQuit, "Quit");
            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
            if (command == IdToggleVisible) ToggleVisible();
            else if (command == IdToggleCompact) _toggleCompact();
            else if (command == IdQuit) _window.Close();
        }
        finally { DestroyMenu(menu); }
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string? file, int index, IntPtr large, out IntPtr small, uint count);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id, UIntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
