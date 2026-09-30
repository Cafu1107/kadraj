#include <windows.h>
#include <mfapi.h>
#include <mfvirtualcamera.h>
#include <wrl/module.h>
#include <string>

using Microsoft::WRL::InProc;
using Microsoft::WRL::Module;

static HMODULE g_module = nullptr;
static constexpr wchar_t ClsidString[] = L"{69570d99-37dc-4406-9a8c-cb2d1438ea3a}";
static constexpr wchar_t ClassKey[] = L"Software\\Classes\\CLSID\\{69570d99-37dc-4406-9a8c-cb2d1438ea3a}";

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* object)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, riid, object);
}

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().Terminate() ? S_OK : S_FALSE;
}

static HRESULT SetValue(HKEY root, const std::wstring& path, const wchar_t* name, const std::wstring& value)
{
    HKEY key;
    LSTATUS status = RegCreateKeyExW(root, path.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    status = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()),
        static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return HRESULT_FROM_WIN32(status);
}

// Machine-wide registration (needs admin): Frame Server runs as a service and does not see HKCU.
STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());

    std::wstring key = ClassKey;
    HRESULT hr = SetValue(HKEY_LOCAL_MACHINE, key, nullptr, L"Kadraj Camera");
    if (FAILED(hr)) return hr;
    hr = SetValue(HKEY_LOCAL_MACHINE, key + L"\\InprocServer32", nullptr, path);
    if (FAILED(hr)) return hr;
    return SetValue(HKEY_LOCAL_MACHINE, key + L"\\InprocServer32", L"ThreadingModel", L"Both");
}

STDAPI DllUnregisterServer()
{
    LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, ClassKey);
    return status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(status);
}

// Called by the Kadraj app -----------------------------------------------------

extern "C" HRESULT __stdcall KadrajCamera_IsSupported(BOOL* supported)
{
    if (!supported) return E_POINTER;
    *supported = FALSE;
    return MFIsVirtualCameraTypeSupported(MFVirtualCameraType_SoftwareCameraSource, supported);
}

// Adds "Kadraj" to the system's camera list for as long as the returned handle lives
// (session lifetime: the camera also disappears if the app exits or crashes).
extern "C" HRESULT __stdcall KadrajCamera_Create(LPCWSTR friendlyName, IMFVirtualCamera** camera)
{
    if (!camera) return E_POINTER;
    *camera = nullptr;

    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;

    IMFVirtualCamera* created = nullptr;
    hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_Session,
        MFVirtualCameraAccess_CurrentUser, friendlyName, ClsidString, nullptr, 0, &created);
    if (SUCCEEDED(hr)) hr = created->Start(nullptr);

    if (FAILED(hr))
    {
        if (created)
        {
            created->Shutdown();
            created->Release();
        }
        MFShutdown();
        return hr;
    }
    *camera = created;
    return S_OK;
}

extern "C" HRESULT __stdcall KadrajCamera_Remove(IMFVirtualCamera* camera)
{
    if (!camera) return E_POINTER;
    HRESULT hr = camera->Remove();
    camera->Shutdown();
    camera->Release();
    MFShutdown();
    return hr;
}
