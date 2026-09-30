#pragma once
#include <windows.h>
#include <vector>
#include "Shared.h"

// Produces output frames: the newest frame from the Kadraj app when there is one,
// otherwise a "waiting for a phone" card. Used from the stream's worker thread only.
class FrameSource
{
public:
    FrameSource() = default;
    ~FrameSource();
    FrameSource(const FrameSource&) = delete;
    FrameSource& operator=(const FrameSource&) = delete;

    // Fills dst (NV12 or RGB32, tightly packed) at width x height.
    void Render(BYTE* dst, int width, int height, bool nv12);

    // Tells the app nobody is watching any more.
    void MarkIdle();

private:
    bool EnsureMapping();
    bool CopyLatest();
    void EnsurePlaceholder(int width, int height);
    const BYTE* Sample(int width, int height, int& srcWidth, int& srcHeight);

    HANDLE _mapping = nullptr;
    kadraj::SharedHeader* _header = nullptr;
    const BYTE* _pixels = nullptr;
    ULONGLONG _lastMappingAttempt = 0;

    std::vector<BYTE> _frame;
    int _frameWidth = 0;
    int _frameHeight = 0;

    std::vector<BYTE> _placeholder;
    int _placeholderWidth = 0;
    int _placeholderHeight = 0;

    std::vector<int> _xMap;
};
