#include "MediaSource.h"
#include <wrl/module.h>

using Microsoft::WRL::MakeAndInitialize;

MediaSource::~MediaSource()
{
    if (_mfStarted) MFShutdown();
}

HRESULT MediaSource::RuntimeClassInitialize()
{
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;
    _mfStarted = true;

    hr = MFCreateEventQueue(&_queue);
    if (FAILED(hr)) return hr;
    hr = MFCreateAttributes(&_attributes, 1);
    if (FAILED(hr)) return hr;

    hr = MakeAndInitialize<MediaStream>(&_stream, static_cast<IMFMediaSource*>(this), 0);
    if (FAILED(hr)) return hr;

    ComPtr<IMFStreamDescriptor> stream;
    hr = _stream->GetStreamDescriptor(&stream);
    if (FAILED(hr)) return hr;

    IMFStreamDescriptor* streams[] = { stream.Get() };
    hr = MFCreatePresentationDescriptor(1, streams, &_descriptor);
    if (FAILED(hr)) return hr;
    return _descriptor->SelectStream(0);
}

// IMFMediaEventGenerator ------------------------------------------------------

IFACEMETHODIMP MediaSource::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard guard(_lock);
        if (_shutdown) return MF_E_SHUTDOWN;
        queue = _queue;
    }
    return queue->GetEvent(flags, event);
}

IFACEMETHODIMP MediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
{
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    return _queue->BeginGetEvent(callback, state);
}

IFACEMETHODIMP MediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    return _queue->EndGetEvent(result, event);
}

IFACEMETHODIMP MediaSource::QueueEvent(MediaEventType type, REFGUID extended, HRESULT status, const PROPVARIANT* value)
{
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    return _queue->QueueEventParamVar(type, extended, status, value);
}

// IMFMediaSource --------------------------------------------------------------

IFACEMETHODIMP MediaSource::GetCharacteristics(DWORD* characteristics)
{
    if (!characteristics) return E_POINTER;
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    *characteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

IFACEMETHODIMP MediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor)
{
    if (!descriptor) return E_POINTER;
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    return _descriptor->Clone(descriptor);
}

IFACEMETHODIMP MediaSource::Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition)
{
    if (!descriptor || !startPosition) return E_INVALIDARG;
    if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;

    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;

    DWORD count = 0;
    HRESULT hr = descriptor->GetStreamDescriptorCount(&count);
    if (FAILED(hr)) return hr;

    for (DWORD i = 0; i < count; i++)
    {
        BOOL selected = FALSE;
        ComPtr<IMFStreamDescriptor> stream;
        hr = descriptor->GetStreamDescriptorByIndex(i, &selected, &stream);
        if (FAILED(hr)) return hr;

        DWORD id = 0;
        stream->GetStreamIdentifier(&id);
        if (id != 0) continue;

        if (selected)
        {
            ComPtr<IMFMediaTypeHandler> handler;
            ComPtr<IMFMediaType> type;
            if (SUCCEEDED(stream->GetMediaTypeHandler(&handler))) handler->GetCurrentMediaType(&type);

            _descriptor->SelectStream(0);
            hr = _queue->QueueEventParamUnk(_streamAnnounced ? MEUpdatedStream : MENewStream,
                GUID_NULL, S_OK, static_cast<IMFMediaStream*>(_stream.Get()));
            if (FAILED(hr)) return hr;
            _streamAnnounced = true;

            hr = _stream->Start(type.Get(), startPosition);
            if (FAILED(hr)) return hr;
            _streamActive = true;
        }
        else
        {
            _descriptor->DeselectStream(0);
            if (_streamActive) _stream->Stop();
            _streamActive = false;
        }
    }

    return _queue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, startPosition);
}

IFACEMETHODIMP MediaSource::Stop()
{
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    if (_streamActive) _stream->Stop();
    _streamActive = false;
    return _queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
}

IFACEMETHODIMP MediaSource::Pause()
{
    return MF_E_INVALID_STATE_TRANSITION; // live camera: no pause
}

IFACEMETHODIMP MediaSource::Shutdown()
{
    ComPtr<MediaStream> stream;
    {
        std::lock_guard guard(_lock);
        if (_shutdown) return MF_E_SHUTDOWN;
        _shutdown = true;
        _queue->Shutdown();
        stream = std::move(_stream);
    }
    if (stream) stream->Shutdown();
    return S_OK;
}

// IMFMediaSourceEx ------------------------------------------------------------

IFACEMETHODIMP MediaSource::GetSourceAttributes(IMFAttributes** attributes)
{
    if (!attributes) return E_POINTER;
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    return _attributes.CopyTo(attributes);
}

IFACEMETHODIMP MediaSource::GetStreamAttributes(DWORD streamId, IMFAttributes** attributes)
{
    if (!attributes) return E_POINTER;
    std::lock_guard guard(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    if (streamId != 0) return MF_E_INVALIDSTREAMNUMBER;
    return _stream->GetAttributes(attributes);
}

IFACEMETHODIMP MediaSource::SetD3DManager(IUnknown*)
{
    return S_OK; // frames are produced in system memory
}

// IMFGetService / IKsControl --------------------------------------------------

IFACEMETHODIMP MediaSource::GetService(REFGUID, REFIID, LPVOID* object)
{
    if (object) *object = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

IFACEMETHODIMP MediaSource::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*)
{
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP MediaSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*)
{
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP MediaSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*)
{
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}
