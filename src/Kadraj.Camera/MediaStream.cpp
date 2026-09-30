#include "MediaSource.h"

// KSCATEGORY_VIDEO_CAMERA pin: PINNAME_VIDEO_CAPTURE
static const GUID PinNameVideoCapture = { 0xfb6c4281, 0x0353, 0x11d1, { 0x90, 0x5f, 0x00, 0x00, 0xc0, 0xcc, 0x16, 0xba } };

namespace
{
    struct Format { int width; int height; bool nv12; };

    // First entry is the default. NV12 first: it is what most apps expect from a webcam.
    constexpr Format Formats[] = {
        { 1280, 720, true }, { 1920, 1080, true }, { 640, 360, true },
        { 1280, 720, false }, { 1920, 1080, false }, { 640, 360, false },
    };

    HRESULT CreateMediaType(const Format& format, IMFMediaType** type)
    {
        ComPtr<IMFMediaType> t;
        HRESULT hr = MFCreateMediaType(&t);
        if (FAILED(hr)) return hr;

        UINT32 stride = format.nv12 ? format.width : format.width * 4;
        UINT32 size = format.nv12 ? format.width * format.height * 3 / 2 : format.width * format.height * 4;

        t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        t->SetGUID(MF_MT_SUBTYPE, format.nv12 ? MFVideoFormat_NV12 : MFVideoFormat_RGB32);
        MFSetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, format.width, format.height);
        MFSetAttributeRatio(t.Get(), MF_MT_FRAME_RATE, 30, 1);
        MFSetAttributeRatio(t.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        t->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
        t->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
        t->SetUINT32(MF_MT_DEFAULT_STRIDE, stride); // positive: top-down, also for RGB32
        t->SetUINT32(MF_MT_SAMPLE_SIZE, size);
        t->SetUINT32(MF_MT_AVG_BITRATE, size * 8 * 30);
        if (format.nv12)
        {
            t->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
            t->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
        }
        *type = t.Detach();
        return S_OK;
    }
}

MediaStream::~MediaStream()
{
    StopWorker();
}

HRESULT MediaStream::RuntimeClassInitialize(IMFMediaSource* source, DWORD id)
{
    _source = source;

    HRESULT hr = MFCreateEventQueue(&_queue);
    if (FAILED(hr)) return hr;

    hr = MFCreateAttributes(&_attributes, 4);
    if (FAILED(hr)) return hr;
    _attributes->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PinNameVideoCapture);
    _attributes->SetUINT32(MF_DEVICESTREAM_STREAM_ID, id);
    _attributes->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
    _attributes->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);

    constexpr DWORD count = ARRAYSIZE(Formats);
    ComPtr<IMFMediaType> types[count];
    IMFMediaType* raw[count];
    for (DWORD i = 0; i < count; i++)
    {
        hr = CreateMediaType(Formats[i], &types[i]);
        if (FAILED(hr)) return hr;
        raw[i] = types[i].Get();
    }

    hr = MFCreateStreamDescriptor(id, count, raw, &_descriptor);
    if (FAILED(hr)) return hr;

    ComPtr<IMFMediaTypeHandler> handler;
    hr = _descriptor->GetMediaTypeHandler(&handler);
    if (FAILED(hr)) return hr;
    hr = handler->SetCurrentMediaType(raw[0]);
    if (FAILED(hr)) return hr;

    return _attributes->CopyAllItems(_descriptor.Get());
}

// IMFMediaEventGenerator ------------------------------------------------------

IFACEMETHODIMP MediaStream::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard guard(_lock);
        if (_shutdown) return MF_E_SHUTDOWN;
        queue = _queue;
    }
    return queue->GetEvent(flags, event); // may block: must not hold the lock
}

IFACEMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
{
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    return _queue->BeginGetEvent(callback, state);
}

IFACEMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    return _queue->EndGetEvent(result, event);
}

IFACEMETHODIMP MediaStream::QueueEvent(MediaEventType type, REFGUID extended, HRESULT status, const PROPVARIANT* value)
{
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    return _queue->QueueEventParamVar(type, extended, status, value);
}

// IMFMediaStream --------------------------------------------------------------

IFACEMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** source)
{
    if (!source) return E_POINTER;
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    return _source.CopyTo(source);
}

IFACEMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor)
{
    if (!descriptor) return E_POINTER;
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    return _descriptor.CopyTo(descriptor);
}

IFACEMETHODIMP MediaStream::RequestSample(IUnknown* token)
{
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    if (_state != MF_STREAM_STATE_RUNNING) return MF_E_INVALIDREQUEST;

    if (_tokens.size() >= 16) _tokens.pop_front();
    _tokens.emplace_back(token);
    return S_OK;
}

// IMFMediaStream2 -------------------------------------------------------------

IFACEMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE state)
{
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;

    switch (state)
    {
    case MF_STREAM_STATE_RUNNING:
        _state = state;
        EnsureWorker();
        return S_OK;
    case MF_STREAM_STATE_PAUSED:
        _state = state;
        return S_OK;
    case MF_STREAM_STATE_STOPPED:
        _state = state;
        _tokens.clear();
        return S_OK;
    default:
        return MF_E_INVALID_STATE_TRANSITION;
    }
}

IFACEMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* state)
{
    if (!state) return E_POINTER;
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    *state = _state;
    return S_OK;
}

// Driven by MediaSource -------------------------------------------------------

HRESULT MediaStream::Start(IMFMediaType* type, const PROPVARIANT* startPosition)
{
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;

    if (type)
    {
        GUID subtype{};
        UINT32 width = 0, height = 0;
        if (SUCCEEDED(type->GetGUID(MF_MT_SUBTYPE, &subtype)) &&
            SUCCEEDED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height)) &&
            width > 0 && height > 0 && width <= kadraj::MaxWidth && height <= kadraj::MaxHeight)
        {
            _width = static_cast<int>(width);
            _height = static_cast<int>(height);
            _nv12 = subtype == MFVideoFormat_NV12;
        }
    }

    _state = MF_STREAM_STATE_RUNNING;
    EnsureWorker();
    return _queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, startPosition);
}

HRESULT MediaStream::Stop()
{
    HRESULT hr;
    {
        std::lock_guard guard(_lock);
        if (_shutdown) return MF_E_SHUTDOWN;
        _state = MF_STREAM_STATE_STOPPED;
        _tokens.clear();
        hr = _queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    }
    StopWorker();
    return hr;
}

void MediaStream::Shutdown()
{
    {
        std::lock_guard guard(_lock);
        if (_shutdown) return;
        _shutdown = true;
        _state = MF_STREAM_STATE_STOPPED;
        _tokens.clear();
        _queue->Shutdown();
    }
    StopWorker();
    std::lock_guard guard(_lock);
    _source.Reset(); // breaks the source <-> stream reference cycle
}

HRESULT MediaStream::GetAttributes(IMFAttributes** attributes)
{
    return _attributes.CopyTo(attributes);
}

// Worker ----------------------------------------------------------------------

void MediaStream::EnsureWorker()
{
    // Caller holds _lock.
    if (_worker.joinable()) return;
    HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!stop) return;
    _workerStop = stop;
    _worker = std::thread([this, stop] { Worker(stop); });
}

void MediaStream::StopWorker()
{
    std::thread worker;
    HANDLE stop;
    {
        std::lock_guard guard(_lock);
        worker = std::move(_worker);
        stop = std::exchange(_workerStop, nullptr);
    }
    if (!stop) return;
    SetEvent(stop);
    if (worker.joinable()) worker.join();
    CloseHandle(stop);
}

void MediaStream::Worker(HANDLE stop)
{
    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (!timer) timer = CreateWaitableTimerW(nullptr, FALSE, nullptr);
    if (!timer) return;

    LARGE_INTEGER due{};
    due.QuadPart = -333333; // 33.3 ms, relative
    SetWaitableTimer(timer, &due, 33, nullptr, nullptr, FALSE);
    HANDLE handles[] = { stop, timer };

    while (WaitForMultipleObjects(2, handles, FALSE, INFINITE) == WAIT_OBJECT_0 + 1)
    {
        ComPtr<IUnknown> token;
        int width, height;
        bool nv12;
        {
            std::lock_guard guard(_lock);
            if (_shutdown || _state != MF_STREAM_STATE_RUNNING || _tokens.empty()) continue;
            token = std::move(_tokens.front());
            _tokens.pop_front();
            width = _width;
            height = _height;
            nv12 = _nv12;
        }

        ComPtr<IMFSample> sample;
        if (FAILED(CreateSample(width, height, nv12, &sample))) continue;
        if (token) sample->SetUnknown(MFSampleExtension_Token, token.Get());

        std::lock_guard guard(_lock);
        if (!_shutdown && _state == MF_STREAM_STATE_RUNNING)
            _queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
    }

    CancelWaitableTimer(timer);
    CloseHandle(timer);
    _frames.MarkIdle();
}

HRESULT MediaStream::CreateSample(int width, int height, bool nv12, IMFSample** sample)
{
    DWORD size = nv12 ? width * height * 3 / 2 : width * height * 4;

    ComPtr<IMFMediaBuffer> buffer;
    HRESULT hr = MFCreateMemoryBuffer(size, &buffer);
    if (FAILED(hr)) return hr;

    BYTE* data = nullptr;
    hr = buffer->Lock(&data, nullptr, nullptr);
    if (FAILED(hr)) return hr;
    _frames.Render(data, width, height, nv12);
    buffer->Unlock();
    buffer->SetCurrentLength(size);

    ComPtr<IMFSample> s;
    hr = MFCreateSample(&s);
    if (FAILED(hr)) return hr;
    s->AddBuffer(buffer.Get());
    s->SetSampleTime(MFGetSystemTime());
    s->SetSampleDuration(333333);
    *sample = s.Detach();
    return S_OK;
}
