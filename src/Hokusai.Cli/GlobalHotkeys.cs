using System.Runtime.InteropServices;

namespace Hokusai.Cli;

/// <summary>System-wide hotkeys so demonstrations can be marked while the legacy app has focus.</summary>
internal sealed partial class GlobalHotkeys : IDisposable
{
    public const int ToggleId = 1;
    public const int FinishId = 2;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF9 = 0x78;
    private const uint VkF12 = 0x7B;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;

    private readonly Thread _thread;
    private uint _threadId;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    public GlobalHotkeys(Action<int> pressed)
    {
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            RegisterHotKey(IntPtr.Zero, ToggleId, ModControl | ModAlt | ModNoRepeat, VkF9);
            RegisterHotKey(IntPtr.Zero, FinishId, ModControl | ModAlt | ModNoRepeat, VkF12);
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message == WmHotkey)
                {
                    pressed(checked((int)msg.WParam));
                }
            }

            UnregisterHotKey(IntPtr.Zero, ToggleId);
            UnregisterHotKey(IntPtr.Zero, FinishId);
        })
        { IsBackground = true, Name = "hokusai-hotkeys" };
        _thread.Start();
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread.Join(TimeSpan.FromSeconds(1));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
