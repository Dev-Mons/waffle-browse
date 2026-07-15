using System.Runtime.InteropServices;

namespace Waffle.Browse.App.Diagnostics;

internal sealed class NativeFocusEventTracer : IDisposable
{
    private const uint EventObjectFocus = 0x8005;
    private const uint WinEventOutOfContext = 0x0000;

    private WinEventDelegate? callback;
    private IntPtr hook;

    public void Start(Action<IntPtr, int, int> focusChanged)
    {
        if (!FocusTraceLogger.IsEnabled || hook != IntPtr.Zero)
        {
            return;
        }

        callback = (_, _, hwnd, idObject, idChild, _, _) => focusChanged(hwnd, idObject, idChild);
        hook = SetWinEventHook(
            EventObjectFocus,
            EventObjectFocus,
            IntPtr.Zero,
            callback,
            (uint)Environment.ProcessId,
            0,
            WinEventOutOfContext);
    }

    public void Dispose()
    {
        if (hook == IntPtr.Zero)
        {
            return;
        }

        UnhookWinEvent(hook);
        hook = IntPtr.Zero;
        callback = null;
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
