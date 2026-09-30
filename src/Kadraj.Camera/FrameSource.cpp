#include "FrameSource.h"
#include <wtsapi32.h>
#include <cwchar>
#include <algorithm>
#include <cstring>

using namespace kadraj;

FrameSource::~FrameSource()
{
    MarkIdle();
    if (_header) UnmapViewOfFile(_header);
    if (_mapping) CloseHandle(_mapping);
}

// The Kadraj app creates the section in its own session namespace and grants access only to its
// user, SYSTEM and LocalService (Frame Server's account). This DLL runs in session 0, so it reaches
// the user's session through the "Session\<id>\" prefix: the console session first, then any
// other active session (e.g. a remote desktop).
static HANDLE OpenAppMapping()
{
    auto tryOpen = [](const wchar_t* name) { return OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name); };

    wchar_t name[96];
    DWORD console = WTSGetActiveConsoleSessionId();
    if (console != 0xFFFFFFFF)
    {
        swprintf_s(name, L"Session\\%lu\\%s", console, SharedMemoryName);
        if (HANDLE h = tryOpen(name)) return h;
    }

    PWTS_SESSION_INFOW sessions = nullptr;
    DWORD count = 0;
    if (WTSEnumerateSessionsW(WTS_CURRENT_SERVER_HANDLE, 0, 1, &sessions, &count))
    {
        HANDLE found = nullptr;
        for (DWORD i = 0; i < count && !found; i++)
        {
            if (sessions[i].State != WTSActive || sessions[i].SessionId == console) continue;
            swprintf_s(name, L"Session\\%lu\\%s", sessions[i].SessionId, SharedMemoryName);
            found = tryOpen(name);
        }
        WTSFreeMemory(sessions);
        if (found) return found;
    }

    // Loaded inside the user's own session (not via Frame Server): plain local name.
    swprintf_s(name, L"Local\\%s", SharedMemoryName);
    return tryOpen(name);
}

bool FrameSource::EnsureMapping()
{
    if (_header) return true;

    ULONGLONG now = GetTickCount64();
    if (_lastMappingAttempt != 0 && now - _lastMappingAttempt < 1000) return false;
    _lastMappingAttempt = now;

    HANDLE mapping = OpenAppMapping();
    if (!mapping) return false; // Kadraj isn't running (yet)

    void* view = MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, SharedSize);
    if (!view)
    {
        CloseHandle(mapping);
        return false;
    }

    _mapping = mapping;
    _header = static_cast<SharedHeader*>(view);
    _pixels = static_cast<const BYTE*>(view) + HeaderSize;
    return true;
}

void FrameSource::MarkIdle()
{
    if (!_header) return;
    _header->requestedWidth = 0;
    _header->requestedHeight = 0;
}

bool FrameSource::CopyLatest()
{
    if (!EnsureMapping() || _header->magic != SharedMagic || _header->version != SharedVersion) return false;

    int64_t produced = _header->producerTick;
    if (produced <= 0 || GetTickCount64() - static_cast<ULONGLONG>(produced) > StaleAfterMs) return false;

    for (int attempt = 0; attempt < 3; attempt++)
    {
        LONG before = _header->sequence;
        if (before & 1)
        {
            YieldProcessor();
            continue;
        }

        int width = _header->width, height = _header->height;
        if (width <= 0 || height <= 0 || width > MaxWidth || height > MaxHeight) return false;

        size_t size = size_t(width) * height * 4;
        _frame.resize(size);
        memcpy(_frame.data(), _pixels, size);
        MemoryBarrier();

        if (_header->sequence == before)
        {
            _frameWidth = width;
            _frameHeight = height;
            return true;
        }
    }
    // Kept colliding with a write: reuse the previous frame rather than show a torn one.
    return _frameWidth > 0;
}

void FrameSource::EnsurePlaceholder(int width, int height)
{
    if (_placeholderWidth == width && _placeholderHeight == height) return;

    _placeholder.assign(size_t(width) * height * 4, 0);
    _placeholderWidth = width;
    _placeholderHeight = height;

    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = width;
    bi.bmiHeader.biHeight = -height; // top-down
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    HDC dc = CreateCompatibleDC(nullptr);
    void* bits = nullptr;
    HBITMAP bitmap = dc ? CreateDIBSection(dc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0) : nullptr;
    if (!bitmap)
    {
        if (dc) DeleteDC(dc);
        return; // black frame is still a valid frame
    }
    HGDIOBJ oldBitmap = SelectObject(dc, bitmap);

    RECT all{ 0, 0, width, height };
    HBRUSH background = CreateSolidBrush(RGB(15, 16, 18));
    FillRect(dc, &all, background);
    DeleteObject(background);

    // Accent mark above the title
    int mark = std::max(4, height / 90);
    RECT accent{ width / 2 - mark * 4, height / 2 - height / 7 - mark, width / 2 + mark * 4, height / 2 - height / 7 };
    HBRUSH accentBrush = CreateSolidBrush(RGB(255, 90, 54));
    FillRect(dc, &accent, accentBrush);
    DeleteObject(accentBrush);

    SetBkMode(dc, TRANSPARENT);
    HFONT title = CreateFontW(-height / 10, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, DEFAULT_PITCH, L"Segoe UI");
    HGDIOBJ oldFont = SelectObject(dc, title);
    SetTextColor(dc, RGB(236, 237, 239));
    RECT titleRect{ 0, 0, width, height / 2 + height / 30 };
    DrawTextW(dc, L"Kadraj", -1, &titleRect, DT_CENTER | DT_BOTTOM | DT_SINGLELINE);

    bool turkish = PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_TURKISH;
    HFONT subtitle = CreateFontW(-height / 26, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, DEFAULT_PITCH, L"Segoe UI");
    SelectObject(dc, subtitle);
    SetTextColor(dc, RGB(139, 143, 152));
    RECT subtitleRect{ 0, height / 2 + height / 16, width, height };
    DrawTextW(dc, turkish ? L"Telefon bekleniyor" : L"Waiting for a phone", -1, &subtitleRect,
        DT_CENTER | DT_TOP | DT_SINGLELINE);

    GdiFlush();
    memcpy(_placeholder.data(), bits, _placeholder.size());

    SelectObject(dc, oldFont);
    DeleteObject(title);
    DeleteObject(subtitle);
    SelectObject(dc, oldBitmap);
    DeleteObject(bitmap);
    DeleteDC(dc);
}

const BYTE* FrameSource::Sample(int width, int height, int& srcWidth, int& srcHeight)
{
    if (CopyLatest())
    {
        srcWidth = _frameWidth;
        srcHeight = _frameHeight;
        return _frame.data();
    }
    EnsurePlaceholder(width, height);
    srcWidth = width;
    srcHeight = height;
    return _placeholder.data();
}

void FrameSource::Render(BYTE* dst, int width, int height, bool nv12)
{
    if (EnsureMapping())
    {
        _header->requestedWidth = width;
        _header->requestedHeight = height;
        _header->consumerTick = static_cast<int64_t>(GetTickCount64());
    }

    int sw = 0, sh = 0;
    const BYTE* src = Sample(width, height, sw, sh);

    // Kadraj renders at the requested size, so this is normally 1:1. Nearest-neighbour
    // covers the moment a consumer switches resolution.
    _xMap.resize(width);
    for (int x = 0; x < width; x++) _xMap[x] = (x * sw / width) * 4;
    auto row = [&](int y) { return src + size_t(y * sh / height) * sw * 4; };

    if (!nv12)
    {
        for (int y = 0; y < height; y++)
        {
            const BYTE* s = row(y);
            BYTE* d = dst + size_t(y) * width * 4;
            if (sw == width) memcpy(d, s, size_t(width) * 4);
            else for (int x = 0; x < width; x++) memcpy(d + x * 4, s + _xMap[x], 4);
        }
        return;
    }

    // BGRA → NV12, BT.709 limited range (matches the media type attributes).
    BYTE* yPlane = dst;
    BYTE* uvPlane = dst + size_t(width) * height;
    for (int y = 0; y < height; y++)
    {
        const BYTE* s = row(y);
        BYTE* d = yPlane + size_t(y) * width;
        for (int x = 0; x < width; x++)
        {
            const BYTE* p = s + _xMap[x];
            d[x] = static_cast<BYTE>(((47 * p[2] + 157 * p[1] + 16 * p[0] + 128) >> 8) + 16);
        }
    }
    for (int y = 0; y < height / 2; y++)
    {
        const BYTE* s0 = row(y * 2);
        const BYTE* s1 = row(y * 2 + 1);
        BYTE* d = uvPlane + size_t(y) * width;
        for (int x = 0; x < width / 2; x++)
        {
            const BYTE* a = s0 + _xMap[x * 2];
            const BYTE* b = s0 + _xMap[x * 2 + 1];
            const BYTE* c = s1 + _xMap[x * 2];
            const BYTE* e = s1 + _xMap[x * 2 + 1];
            int bl = (a[0] + b[0] + c[0] + e[0]) >> 2;
            int gr = (a[1] + b[1] + c[1] + e[1]) >> 2;
            int rd = (a[2] + b[2] + c[2] + e[2]) >> 2;
            int u = ((-26 * rd - 86 * gr + 112 * bl + 128) >> 8) + 128;
            int v = ((112 * rd - 102 * gr - 10 * bl + 128) >> 8) + 128;
            d[x * 2] = static_cast<BYTE>(std::clamp(u, 0, 255));
            d[x * 2 + 1] = static_cast<BYTE>(std::clamp(v, 0, 255));
        }
    }
}
