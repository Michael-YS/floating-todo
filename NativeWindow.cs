using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace FloatingTodo;

internal static class NativeWindow
{
    private static readonly nint HwndTop = nint.Zero;
    private static readonly nint HwndTopMost = new(-1);
    private static readonly nint HwndNoTopMost = new(-2);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;

    public static void Configure(System.Windows.Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExToolWindow);

        var enabled = 1;
        var rounded = 2;
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
        DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref rounded, sizeof(int));
    }

    public static void SendToDesktop(System.Windows.Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        window.Topmost = false;
        SetWindowPos(handle, HwndNoTopMost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
        SetWindowPos(handle, HwndTop, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
    }

    public static void ShowTemporarily(System.Windows.Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!window.IsVisible)
        {
            window.Show();
        }

        SetWindowPos(handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
        window.Topmost = true;
        window.Activate();
    }

    public static void ResetToPrimaryTopRight(System.Windows.Window window, double margin = 6)
    {
        var screen = Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            return;
        }

        PositionInWorkingArea(window, screen.WorkingArea, margin);
    }

    public static void KeepInsideCurrentWorkingArea(System.Windows.Window window, double margin = 6)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var screen = Forms.Screen.FromHandle(handle);
        var dpi = GetDpiForWindow(handle);
        var scale = dpi <= 0 ? 1d : dpi / 96d;
        var area = screen.WorkingArea;
        var left = area.Left / scale;
        var top = area.Top / scale;
        var right = area.Right / scale;
        var bottom = area.Bottom / scale;

        window.MaxHeight = Math.Max(240, bottom - top - (margin * 2));
        window.Left = Math.Clamp(window.Left, left + margin, Math.Max(left + margin, right - window.ActualWidth - margin));
        window.Top = Math.Clamp(window.Top, top + margin, Math.Max(top + margin, bottom - window.ActualHeight - margin));
    }

    private static void PositionInWorkingArea(System.Windows.Window window, System.Drawing.Rectangle area, double margin)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var dpi = GetDpiForWindow(handle);
        var scale = dpi <= 0 ? 1d : dpi / 96d;
        var left = area.Left / scale;
        var top = area.Top / scale;
        var right = area.Right / scale;
        var bottom = area.Bottom / scale;
        window.MaxHeight = Math.Max(240, bottom - top - (margin * 2));
        window.Left = right - window.ActualWidth - margin;
        window.Top = top + margin;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int valueSize);
}
