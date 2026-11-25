using System.Runtime.InteropServices;

namespace Hokusai.Capture.Ui;

/// <summary>
/// Low-level mouse and keyboard hooks on a dedicated message-loop thread. Legacy Win32 buttons do not always raise
/// the UI Automation Invoked event, so clicks and Enter/Space presses are observed here as a fallback.
/// </summary>
internal sealed partial class NativeInput : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmLButtonUp = 0x0202;
    private const int WmKeyUp = 0x0101;
    private const int WmQuit = 0x0012;
    private const int VkReturn = 0x0D;
    private const int VkSpace = 0x20;

    private readonly Action<int, int, DateTimeOffset> _onClick;
    private readonly Action<DateTimeOffset> _onKey;
    private readonly Thread _thread;
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private uint _threadId;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public Point Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
    }

    public NativeInput(Action<int, int, DateTimeOffset> onClick, Action<DateTimeOffset> onKey)
    {
        _onClick = onClick;
        _onKey = onKey;
        _mouseProc = MouseHook;
        _keyboardProc = KeyboardHook;
        _thread = new Thread(Run) { IsBackground = true, Name = "hokusai-input-hooks" };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var module = GetModuleHandleW(null);
        var mouse = SetWindowsHookExW(WhMouseLl, Marshal.GetFunctionPointerForDelegate(_mouseProc), module, 0);
        var keyboard = SetWindowsHookExW(WhKeyboardLl, Marshal.GetFunctionPointerForDelegate(_keyboardProc), module, 0);
        try
        {
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(in msg);
                DispatchMessageW(in msg);
            }
        }
        finally
        {
            if (mouse != IntPtr.Zero)
            {
                UnhookWindowsHookEx(mouse);
            }

            if (keyboard != IntPtr.Zero)
            {
                UnhookWindowsHookEx(keyboard);
            }
        }
    }

    // Hook callbacks must return quickly or Windows removes the hook; the work happens on another thread.
    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == WmLButtonUp)
        {
            // The time is taken here, before the app handles the click; resolving the element later takes long
            // enough for the app to have saved and cleared its form already.
            var data = Marshal.PtrToStructure<MouseHookData>(lParam);
            var at = DateTimeOffset.Now;
            ThreadPool.QueueUserWorkItem(_ => _onClick(data.Pt.X, data.Pt.Y, at));
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == WmKeyUp)
        {
            var vk = Marshal.ReadInt32(lParam);
            if (vk is VkReturn or VkSpace)
            {
                var at = DateTimeOffset.Now;
                ThreadPool.QueueUserWorkItem(_ => _onKey(at));
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetWindowsHookExW(int idHook, IntPtr lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Msg lpMsg);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(in Msg lpMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? lpModuleName);
}
