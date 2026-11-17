using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using static Notaekinesis.App.Native;

namespace Notaekinesis.App.Services;

public enum Edge
{
    Off,
    Left,
    Right,
}

/// <summary>
/// A 2-pixel, practically invisible window along part of the outer screen edge that only reacts
/// to drag and drop: when something is dragged onto it, it asks for the basket. It's an OLE drop
/// target rather than a mouse hook, so ordinary mouse moves and clicks never wake the app.
/// </summary>
internal sealed unsafe partial class EdgeStrip : IDisposable
{
    private const string ClassName = "Notaekinesis.Edge";
    private const int ThicknessPx = 2;

    /// <summary>The middle part of the edge, so screen corners (hot corners, scroll bars of maximized windows) stay untouched.</summary>
    private const double Coverage = 0.5;

    private static EdgeStrip? s_current;
    private readonly nint _hwnd;
    private readonly DropTarget _target;
    private bool _registered;

    public Edge Edge { get; private set; } = Edge.Off;

    /// <summary>A drag reached the strip. The point is the pointer position in screen pixels.</summary>
    public event EventHandler<POINT>? DragArrived;

    public EdgeStrip()
    {
        s_current = this;
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
            RegisterClassEx(&wc);
        }
        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            ClassName,
            "notaekinesis drop edge",
            WS_POPUP,
            0, 0, 0, 0, 0, 0, module, 0
        );
        if (_hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        // Alpha 1 of 255: invisible, but unlike alpha 0 it is still hit-tested, so drags find it.
        SetLayeredWindowAttributes(_hwnd, 0, 1, LWA_ALPHA);

        _target = new DropTarget(this);
        // Drag and drop needs OLE on this (the UI, single-threaded) thread; S_FALSE means it was already on.
        var ole = OleInitialize(0);
        var hr = ole < 0 ? ole : RegisterDragDrop(_hwnd, _target);
        _registered = hr == 0;
        if (!_registered) Problem = $"The drop edge is unavailable (0x{hr:X8}).";
    }

    /// <summary>Why the strip can't work, if it can't.</summary>
    public string? Problem { get; }

    /// <summary>Puts the strip on the given edge of the outermost display, or hides it.</summary>
    public void Place(Edge edge)
    {
        Edge = edge;
        if (edge == Edge.Off || !_registered)
        {
            ShowWindow(_hwnd, SW_HIDE);
            return;
        }
        // The edge of the whole desktop: with two monitors side by side, the seam between them isn't an edge.
        // (Win32 rather than DisplayArea.FindAll(), whose list can't be enumerated under NativeAOT.)
        var monitors = new List<RECT>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            EnumDisplayMonitors(0, 0, &CollectMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        if (monitors.Count == 0) return;
        var outer = edge == Edge.Right ? monitors.MaxBy(m => m.Right) : monitors.MinBy(m => m.Left);
        var height = outer.Bottom - outer.Top;
        var length = (int)(height * Coverage);
        var x = edge == Edge.Right ? outer.Right - ThicknessPx : outer.Left;
        var y = outer.Top + (height - length) / 2;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, ThicknessPx, length, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Re-places the strip after monitors, resolution or scaling change.</summary>
    public void Refresh() => Place(Edge);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [UnmanagedCallersOnly]
    private static int CollectMonitor(nint monitor, nint dc, RECT* rect, nint data)
    {
        ((List<RECT>)GCHandle.FromIntPtr(data).Target!).Add(*rect);
        return 1;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // Never take focus, even when clicked.
        if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED)
        {
            try
            {
                s_current?.Refresh();
            }
            catch (Exception) { }
            return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_registered) _ = RevokeDragDrop(_hwnd);
        _registered = false;
        DestroyWindow(_hwnd);
        s_current = null;
    }

    private void OnDragEnter(POINTL pt) => DragArrived?.Invoke(this, new POINT { X = pt.X, Y = pt.Y });

    // --- OLE drop target ---------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTL
    {
        public int X;
        public int Y;
    }

    /// <summary>IDropTarget. The data object is never read here; the basket takes the drop.</summary>
    [GeneratedComInterface]
    [Guid("00000122-0000-0000-C000-000000000046")]
    internal partial interface IDropTarget
    {
        [PreserveSig]
        int DragEnter(nint dataObject, uint keyState, POINTL pt, uint* effect);

        [PreserveSig]
        int DragOver(uint keyState, POINTL pt, uint* effect);

        [PreserveSig]
        int DragLeave();

        [PreserveSig]
        int Drop(nint dataObject, uint keyState, POINTL pt, uint* effect);
    }

    [GeneratedComClass]
    internal sealed partial class DropTarget(EdgeStrip strip) : IDropTarget
    {
        private const uint DROPEFFECT_NONE = 0;
        private const uint DROPEFFECT_COPY = 1;

        public int DragEnter(nint dataObject, uint keyState, POINTL pt, uint* effect)
        {
            *effect = DROPEFFECT_COPY;
            try
            {
                strip.OnDragEnter(pt);
            }
            catch (Exception) { }
            return 0;
        }

        public int DragOver(uint keyState, POINTL pt, uint* effect)
        {
            *effect = DROPEFFECT_COPY;
            return 0;
        }

        public int DragLeave() => 0;

        // Released right on the 2-pixel strip: nothing is taken, the basket is open to try again.
        public int Drop(nint dataObject, uint keyState, POINTL pt, uint* effect)
        {
            *effect = DROPEFFECT_NONE;
            return 0;
        }
    }

    // --- Win32 -------------------------------------------------------------------------

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_HIDE = 0;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_DPICHANGED = 0x02E0;
    private const nint MA_NOACTIVATE = 3;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint dc, nint clip, delegate* unmanaged<nint, nint, RECT*, nint, int> callback, nint data);

    [LibraryImport("ole32.dll")]
    private static partial int OleInitialize(nint reserved);

    [LibraryImport("ole32.dll")]
    private static partial int RegisterDragDrop(nint hwnd, IDropTarget target);

    [LibraryImport("ole32.dll")]
    private static partial int RevokeDragDrop(nint hwnd);
}
