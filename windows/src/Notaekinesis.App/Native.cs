using System.Runtime.InteropServices;

namespace Notaekinesis.App;

internal static partial class Native
{
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    /// <summary>Back to the app after finishing the sign-in in the browser.</summary>
    internal static void BringToFront(Microsoft.UI.Xaml.Window window)
    {
        window.Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
    }
}
