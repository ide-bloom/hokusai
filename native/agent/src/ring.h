// Shared-memory event ring between the agent (writer, inside the target process) and Hokusai (reader).
// The layout is mirrored by Hokusai.Capture/Odbc/EventRing.cs; change both together.
#pragma once

#include <cstdint>
#include <string_view>

namespace hokusai
{

constexpr std::uint32_t RingMagic = 0x47524B48; // "HKRG"
constexpr std::uint32_t RingVersion = 1;
constexpr std::uint32_t RingSlotCount = 4096;
constexpr std::uint32_t RingSlotSize = 8192;
constexpr std::uint32_t SlotFlagTruncated = 1;

#pragma pack(push, 8)
struct RingHeader
{
    std::uint32_t magic;
    std::uint32_t version;
    std::uint32_t slotCount;
    std::uint32_t slotSize;
    volatile std::int64_t writeSequence; // next sequence number to reserve
    volatile std::int64_t dropped;       // events not written because they did not fit
    std::uint32_t processId;
    std::uint32_t bits;
    std::uint8_t reserved[24];
};

struct SlotHeader
{
    volatile std::int64_t committed; // sequence + 1 once the payload is complete
    std::uint32_t length;
    std::uint32_t flags;
};
#pragma pack(pop)

static_assert(sizeof(RingHeader) == 64, "RingHeader layout is shared with C#");
static_assert(sizeof(SlotHeader) == 16, "SlotHeader layout is shared with C#");

// Creates (or opens) the ring named Local\hokusai_ring_<pid>. Returns false when shared memory is
// unavailable.
bool RingOpen();

// Appends one UTF-8 JSON event. Safe to call from any thread.
void RingWrite(std::string_view payload);

void RingClose();

} // namespace hokusai
