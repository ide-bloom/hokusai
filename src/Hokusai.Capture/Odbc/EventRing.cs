using System.IO.MemoryMappedFiles;
using System.Text;

namespace Hokusai.Capture.Odbc;

/// <summary>
/// Reader side of the agent's shared-memory ring (layout in native/agent/src/ring.h). The writer reserves a
/// sequence number, fills the slot, then publishes <c>committed = sequence + 1</c>; the reader consumes slots in
/// sequence order and counts slots overwritten before it could read them.
/// </summary>
public sealed class EventRing : IDisposable
{
    public const uint Magic = 0x47524B48;
    public const int HeaderSize = 64;
    public const int SlotHeaderSize = 16;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly uint _slotCount;
    private readonly uint _slotSize;
    private long _next;

    private EventRing(MemoryMappedFile file, MemoryMappedViewAccessor view)
    {
        _file = file;
        _view = view;
        _slotCount = view.ReadUInt32(8);
        _slotSize = view.ReadUInt32(12);
    }

    public static string NameFor(int processId) => $@"Local\hokusai_ring_{processId}";

    public long Lost { get; private set; }

    /// <summary>Opens the ring of <paramref name="processId"/>, or returns null if the agent has not created it yet.</summary>
    public static EventRing? TryOpen(int processId) => TryOpen(NameFor(processId));

    public static EventRing? TryOpen(string name)
    {
        MemoryMappedFile file;
        try
        {
            file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var view = file.CreateViewAccessor();
        if (view.ReadUInt32(0) != Magic)
        {
            view.Dispose();
            file.Dispose();
            return null;
        }

        return new EventRing(file, view);
    }

    /// <summary>Reads every committed event not read yet.</summary>
    public List<string> ReadAvailable()
    {
        var events = new List<string>();
        var written = _view.ReadInt64(16);
        if (written - _next > _slotCount)
        {
            Lost += written - _next - _slotCount;
            _next = written - _slotCount;
        }

        while (_next < written)
        {
            var offset = HeaderSize + ((_next % _slotCount) * _slotSize);
            // An aligned 8-byte read is atomic on x86/x64; the barrier keeps the payload read after it.
            var committed = _view.ReadInt64(offset);
            Interlocked.MemoryBarrier();
            if (committed < _next + 1)
            {
                break; // reserved but still being written; picked up on the next read
            }

            if (committed > _next + 1)
            {
                Lost++; // overwritten by a later lap
                _next++;
                continue;
            }

            var length = _view.ReadUInt32(offset + 8);
            var bytes = new byte[Math.Min(length, _slotSize - SlotHeaderSize)];
            _view.ReadArray(offset + SlotHeaderSize, bytes, 0, bytes.Length);
            events.Add(Encoding.UTF8.GetString(bytes));
            _next++;
        }

        return events;
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }
}
