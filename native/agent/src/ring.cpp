#include "ring.h"

#include <windows.h>

#include <cstring>
#include <string>

namespace hokusai
{

namespace
{
HANDLE g_mapping = nullptr;
std::uint8_t* g_view = nullptr;

RingHeader* Header()
{
    return reinterpret_cast<RingHeader*>(g_view);
}

std::uint8_t* Slot(std::int64_t sequence)
{
    auto index = static_cast<std::uint64_t>(sequence) % RingSlotCount;
    return g_view + sizeof(RingHeader) + index * RingSlotSize;
}
} // namespace

bool RingOpen()
{
    const auto pid = GetCurrentProcessId();
    const std::wstring name = L"Local\\hokusai_ring_" + std::to_wstring(pid);
    const std::uint64_t size = sizeof(RingHeader) + static_cast<std::uint64_t>(RingSlotCount) * RingSlotSize;
    g_mapping =
        CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, static_cast<DWORD>(size >> 32),
                           static_cast<DWORD>(size & 0xFFFFFFFF), name.c_str());
    if (g_mapping == nullptr)
    {
        return false;
    }

    const bool existed = GetLastError() == ERROR_ALREADY_EXISTS;
    g_view = static_cast<std::uint8_t*>(MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0));
    if (g_view == nullptr)
    {
        CloseHandle(g_mapping);
        g_mapping = nullptr;
        return false;
    }

    if (!existed || Header()->magic != RingMagic)
    {
        auto* h = Header();
        h->version = RingVersion;
        h->slotCount = RingSlotCount;
        h->slotSize = RingSlotSize;
        h->writeSequence = 0;
        h->dropped = 0;
        h->processId = pid;
        h->bits = sizeof(void*) * 8;
        MemoryBarrier();
        h->magic = RingMagic; // published last so a reader never sees a half-initialized header
    }

    return true;
}

void RingWrite(std::string_view payload)
{
    if (g_view == nullptr)
    {
        return;
    }

    auto* h = Header();
    const std::int64_t sequence = InterlockedIncrement64(&h->writeSequence) - 1;
    auto* slot = Slot(sequence);
    auto* slotHeader = reinterpret_cast<SlotHeader*>(slot);
    const std::uint32_t capacity = RingSlotSize - sizeof(SlotHeader);
    std::uint32_t length = static_cast<std::uint32_t>(payload.size());
    std::uint32_t flags = 0;
    if (length > capacity)
    {
        length = capacity;
        flags |= SlotFlagTruncated;
        InterlockedIncrement64(&h->dropped);
    }

    std::memcpy(slot + sizeof(SlotHeader), payload.data(), length);
    slotHeader->length = length;
    slotHeader->flags = flags;
    MemoryBarrier();
    InterlockedExchange64(&slotHeader->committed, sequence + 1);
}

void RingClose()
{
    if (g_view != nullptr)
    {
        UnmapViewOfFile(g_view);
        g_view = nullptr;
    }

    if (g_mapping != nullptr)
    {
        CloseHandle(g_mapping);
        g_mapping = nullptr;
    }
}

} // namespace hokusai
