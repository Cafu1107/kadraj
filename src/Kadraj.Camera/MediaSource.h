#pragma once
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <ks.h>
#include <ksproxy.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <deque>
#include <mutex>
#include <thread>
#include "FrameSource.h"

using Microsoft::WRL::ChainInterfaces;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;
using Microsoft::WRL::ClassicCom;

// The single video stream. Consumers pull frames with RequestSample; a 30 fps worker thread
// answers each request with the newest frame, which keeps delivery paced like a real camera.
class MediaStream : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
    ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>>
{
public:
    ~MediaStream() override;
    HRESULT RuntimeClassInitialize(IMFMediaSource* source, DWORD id);

    // IMFMediaEventGenerator
    IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extended, HRESULT status, const PROPVARIANT* value) override;

    // IMFMediaStream
    IFACEMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** descriptor) override;
    IFACEMETHODIMP RequestSample(IUnknown* token) override;

    // IMFMediaStream2
    IFACEMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
    IFACEMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

    // Called by MediaSource
    HRESULT Start(IMFMediaType* type, const PROPVARIANT* startPosition);
    HRESULT Stop();
    void Shutdown();
    HRESULT GetAttributes(IMFAttributes** attributes);

private:
    void EnsureWorker();
    void StopWorker();
    void Worker(HANDLE stop);
    HRESULT CreateSample(int width, int height, bool nv12, IMFSample** sample);

    std::mutex _lock;
    ComPtr<IMFMediaSource> _source;
    ComPtr<IMFMediaEventQueue> _queue;
    ComPtr<IMFStreamDescriptor> _descriptor;
    ComPtr<IMFAttributes> _attributes;
    MF_STREAM_STATE _state = MF_STREAM_STATE_STOPPED;
    bool _shutdown = false;
    std::deque<ComPtr<IUnknown>> _tokens;
    int _width = 1280;
    int _height = 720;
    bool _nv12 = true;

    std::thread _worker;
    HANDLE _workerStop = nullptr; // one event per worker, so a stopping worker never races a new one
    FrameSource _frames;
};

class MediaSource : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
    ChainInterfaces<IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>, IMFGetService, IKsControl>
{
public:
    ~MediaSource() override;
    HRESULT RuntimeClassInitialize();

    // IMFMediaEventGenerator
    IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extended, HRESULT status, const PROPVARIANT* value) override;

    // IMFMediaSource
    IFACEMETHODIMP GetCharacteristics(DWORD* characteristics) override;
    IFACEMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor) override;
    IFACEMETHODIMP Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition) override;
    IFACEMETHODIMP Stop() override;
    IFACEMETHODIMP Pause() override;
    IFACEMETHODIMP Shutdown() override;

    // IMFMediaSourceEx
    IFACEMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
    IFACEMETHODIMP GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) override;
    IFACEMETHODIMP SetD3DManager(IUnknown* manager) override;

    // IMFGetService
    IFACEMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* object) override;

    // IKsControl: no camera controls (zoom, focus…) exposed yet
    IFACEMETHODIMP KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
    IFACEMETHODIMP KsMethod(PKSMETHOD method, ULONG methodLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
    IFACEMETHODIMP KsEvent(PKSEVENT event, ULONG eventLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;

private:
    std::mutex _lock;
    ComPtr<IMFMediaEventQueue> _queue;
    ComPtr<IMFAttributes> _attributes;
    ComPtr<IMFPresentationDescriptor> _descriptor;
    ComPtr<MediaStream> _stream;
    bool _streamAnnounced = false;
    bool _streamActive = false;
    bool _shutdown = false;
    bool _mfStarted = false;
};
