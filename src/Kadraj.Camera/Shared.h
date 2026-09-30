#pragma once
#include <cstdint>

// Frame hand-off between the Kadraj app and this camera, which runs inside the Windows
// Camera Frame Server service. Mirrored in src/Kadraj.App/Camera/SharedFrameWriter.cs.
// The app creates the section (in its session, user-only ACL); the camera only opens it.
namespace kadraj
{
    constexpr wchar_t SharedMemoryName[] = L"KadrajCameraFrames";
    constexpr uint32_t SharedMagic = 0x4A52444B; // "KDRJ"
    constexpr uint32_t SharedVersion = 2;
    constexpr int32_t MaxWidth = 1920;
    constexpr int32_t MaxHeight = 1080;
    constexpr uint32_t HeaderSize = 4096;
    constexpr uint64_t SharedSize = HeaderSize + uint64_t(MaxWidth) * MaxHeight * 4;
    constexpr uint64_t StaleAfterMs = 1000; // no frame for this long → show the "waiting" card

#pragma pack(push, 8)
    struct SharedHeader
    {
        uint32_t magic;             // 0
        uint32_t version;           // 4
        // Written by the camera: the size the consuming app (Zoom, Discord…) picked; 0 while unused.
        volatile int32_t requestedWidth;   // 8
        volatile int32_t requestedHeight;  // 12
        volatile int64_t consumerTick;     // 16  GetTickCount64 of the last frame delivered
        // Written by Kadraj: the newest BGRA frame (top-down, stride = width * 4), behind a seqlock.
        volatile int64_t producerTick;     // 24
        volatile int32_t sequence;         // 32  odd while Kadraj is writing
        int32_t width;                     // 36
        int32_t height;                    // 40
        int32_t reserved;                  // 44
    };
#pragma pack(pop)

    static_assert(sizeof(SharedHeader) == 48);
}
