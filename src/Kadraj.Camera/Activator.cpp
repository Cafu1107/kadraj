#include "MediaSource.h"
#include <wrl/module.h>

using Microsoft::WRL::MakeAndInitialize;

// The COM class registered for the virtual camera. Frame Server creates it and calls
// ActivateObject to get the media source. Its IMFAttributes part is a plain attribute store.
class __declspec(uuid("69570d99-37dc-4406-9a8c-cb2d1438ea3a")) Activator
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, ChainInterfaces<IMFActivate, IMFAttributes>>
{
public:
    Activator() { MFCreateAttributes(&_attributes, 1); }

    // IMFActivate
    IFACEMETHODIMP ActivateObject(REFIID riid, void** object) override
    {
        if (!object) return E_POINTER;
        *object = nullptr;
        std::lock_guard guard(_lock);
        if (!_source)
        {
            HRESULT hr = MakeAndInitialize<MediaSource>(&_source);
            if (FAILED(hr)) return hr;
        }
        return _source.CopyTo(riid, object);
    }

    IFACEMETHODIMP ShutdownObject() override
    {
        std::lock_guard guard(_lock);
        if (_source) _source->Shutdown();
        _source.Reset();
        return S_OK;
    }

    IFACEMETHODIMP DetachObject() override
    {
        std::lock_guard guard(_lock);
        _source.Reset();
        return S_OK;
    }

    // IMFAttributes
#define KADRAJ_CHECK if (!_attributes) return E_OUTOFMEMORY
    IFACEMETHODIMP GetItem(REFGUID key, PROPVARIANT* value) override { KADRAJ_CHECK; return _attributes->GetItem(key, value); }
    IFACEMETHODIMP GetItemType(REFGUID key, MF_ATTRIBUTE_TYPE* type) override { KADRAJ_CHECK; return _attributes->GetItemType(key, type); }
    IFACEMETHODIMP CompareItem(REFGUID key, REFPROPVARIANT value, BOOL* result) override { KADRAJ_CHECK; return _attributes->CompareItem(key, value, result); }
    IFACEMETHODIMP Compare(IMFAttributes* other, MF_ATTRIBUTES_MATCH_TYPE type, BOOL* result) override { KADRAJ_CHECK; return _attributes->Compare(other, type, result); }
    IFACEMETHODIMP GetUINT32(REFGUID key, UINT32* value) override { KADRAJ_CHECK; return _attributes->GetUINT32(key, value); }
    IFACEMETHODIMP GetUINT64(REFGUID key, UINT64* value) override { KADRAJ_CHECK; return _attributes->GetUINT64(key, value); }
    IFACEMETHODIMP GetDouble(REFGUID key, double* value) override { KADRAJ_CHECK; return _attributes->GetDouble(key, value); }
    IFACEMETHODIMP GetGUID(REFGUID key, GUID* value) override { KADRAJ_CHECK; return _attributes->GetGUID(key, value); }
    IFACEMETHODIMP GetStringLength(REFGUID key, UINT32* length) override { KADRAJ_CHECK; return _attributes->GetStringLength(key, length); }
    IFACEMETHODIMP GetString(REFGUID key, LPWSTR value, UINT32 size, UINT32* length) override { KADRAJ_CHECK; return _attributes->GetString(key, value, size, length); }
    IFACEMETHODIMP GetAllocatedString(REFGUID key, LPWSTR* value, UINT32* length) override { KADRAJ_CHECK; return _attributes->GetAllocatedString(key, value, length); }
    IFACEMETHODIMP GetBlobSize(REFGUID key, UINT32* size) override { KADRAJ_CHECK; return _attributes->GetBlobSize(key, size); }
    IFACEMETHODIMP GetBlob(REFGUID key, UINT8* buffer, UINT32 size, UINT32* written) override { KADRAJ_CHECK; return _attributes->GetBlob(key, buffer, size, written); }
    IFACEMETHODIMP GetAllocatedBlob(REFGUID key, UINT8** buffer, UINT32* size) override { KADRAJ_CHECK; return _attributes->GetAllocatedBlob(key, buffer, size); }
    IFACEMETHODIMP GetUnknown(REFGUID key, REFIID riid, LPVOID* object) override { KADRAJ_CHECK; return _attributes->GetUnknown(key, riid, object); }
    IFACEMETHODIMP SetItem(REFGUID key, REFPROPVARIANT value) override { KADRAJ_CHECK; return _attributes->SetItem(key, value); }
    IFACEMETHODIMP DeleteItem(REFGUID key) override { KADRAJ_CHECK; return _attributes->DeleteItem(key); }
    IFACEMETHODIMP DeleteAllItems() override { KADRAJ_CHECK; return _attributes->DeleteAllItems(); }
    IFACEMETHODIMP SetUINT32(REFGUID key, UINT32 value) override { KADRAJ_CHECK; return _attributes->SetUINT32(key, value); }
    IFACEMETHODIMP SetUINT64(REFGUID key, UINT64 value) override { KADRAJ_CHECK; return _attributes->SetUINT64(key, value); }
    IFACEMETHODIMP SetDouble(REFGUID key, double value) override { KADRAJ_CHECK; return _attributes->SetDouble(key, value); }
    IFACEMETHODIMP SetGUID(REFGUID key, REFGUID value) override { KADRAJ_CHECK; return _attributes->SetGUID(key, value); }
    IFACEMETHODIMP SetString(REFGUID key, LPCWSTR value) override { KADRAJ_CHECK; return _attributes->SetString(key, value); }
    IFACEMETHODIMP SetBlob(REFGUID key, const UINT8* buffer, UINT32 size) override { KADRAJ_CHECK; return _attributes->SetBlob(key, buffer, size); }
    IFACEMETHODIMP SetUnknown(REFGUID key, IUnknown* value) override { KADRAJ_CHECK; return _attributes->SetUnknown(key, value); }
    IFACEMETHODIMP LockStore() override { KADRAJ_CHECK; return _attributes->LockStore(); }
    IFACEMETHODIMP UnlockStore() override { KADRAJ_CHECK; return _attributes->UnlockStore(); }
    IFACEMETHODIMP GetCount(UINT32* count) override { KADRAJ_CHECK; return _attributes->GetCount(count); }
    IFACEMETHODIMP GetItemByIndex(UINT32 index, GUID* key, PROPVARIANT* value) override { KADRAJ_CHECK; return _attributes->GetItemByIndex(index, key, value); }
    IFACEMETHODIMP CopyAllItems(IMFAttributes* destination) override { KADRAJ_CHECK; return _attributes->CopyAllItems(destination); }
#undef KADRAJ_CHECK

private:
    std::mutex _lock;
    ComPtr<IMFAttributes> _attributes;
    ComPtr<MediaSource> _source;
};

CoCreatableClass(Activator);
