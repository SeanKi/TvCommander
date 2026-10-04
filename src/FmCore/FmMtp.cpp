// FmMtp.cpp - MTP(안드로이드폰 등) 지원. Windows Portable Devices(WPD) API 사용.
#include "FmCore.h"

#include <windows.h>
#include <oleauto.h>
#include <PortableDeviceApi.h>
#include <PortableDevice.h>
#include <wrl/client.h>

#include <functional>
#include <mutex>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "PortableDeviceGuids.lib")

using Microsoft::WRL::ComPtr;

namespace {

constexpr int kBatch = 256;

// 프로세스 전체 MTA 를 유지한다. 이후 어느 스레드에서든 (CoInitialize 없이도) WPD 객체를 쓸 수 있다.
void EnsureCom()
{
    static std::once_flag once;
    std::call_once(once, [] {
        CO_MTA_USAGE_COOKIE cookie;
        CoIncrementMTAUsage(&cookie);
    });
}

struct Device {
    ComPtr<IPortableDevice> dev;
    ComPtr<IPortableDeviceContent> content;
    ComPtr<IPortableDeviceProperties> props;
};

Device* AsDevice(void* h) { return static_cast<Device*>(h); }

HRESULT MakeClientInfo(DWORD access, ComPtr<IPortableDeviceValues>& info)
{
    HRESULT hr = CoCreateInstance(CLSID_PortableDeviceValues, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&info));
    if (FAILED(hr)) return hr;
    info->SetStringValue(WPD_CLIENT_NAME, L"MP-Commander");
    info->SetUnsignedIntegerValue(WPD_CLIENT_MAJOR_VERSION, 0);
    info->SetUnsignedIntegerValue(WPD_CLIENT_MINOR_VERSION, 2);
    info->SetUnsignedIntegerValue(WPD_CLIENT_REVISION, 0);
    info->SetUnsignedIntegerValue(WPD_CLIENT_SECURITY_QUALITY_OF_SERVICE, SECURITY_IMPERSONATION);
    info->SetUnsignedIntegerValue(WPD_CLIENT_DESIRED_ACCESS, access);
    return S_OK;
}

HRESULT MakeKeys(ComPtr<IPortableDeviceKeyCollection>& keys)
{
    HRESULT hr = CoCreateInstance(CLSID_PortableDeviceKeyCollection, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&keys));
    if (FAILED(hr)) return hr;
    keys->Add(WPD_OBJECT_ID);
    keys->Add(WPD_OBJECT_NAME);
    keys->Add(WPD_OBJECT_ORIGINAL_FILE_NAME);
    keys->Add(WPD_OBJECT_SIZE);
    keys->Add(WPD_OBJECT_DATE_MODIFIED);
    keys->Add(WPD_OBJECT_CONTENT_TYPE);
    return S_OK;
}

struct Item {
    std::wstring id, name;
    uint64_t size = 0, lastWrite = 0;
    bool isFolder = false;
};

std::wstring GetString(IPortableDeviceValues* v, REFPROPERTYKEY key)
{
    PWSTR s = nullptr;
    std::wstring r;
    if (SUCCEEDED(v->GetStringValue(key, &s)) && s) r = s;
    CoTaskMemFree(s);
    return r;
}

void ReadItem(IPortableDeviceValues* v, Item& it)
{
    auto id = GetString(v, WPD_OBJECT_ID);
    if (!id.empty()) it.id = id;
    it.name = GetString(v, WPD_OBJECT_ORIGINAL_FILE_NAME);
    if (it.name.empty()) it.name = GetString(v, WPD_OBJECT_NAME);
    if (it.name.empty()) it.name = it.id;

    ULONGLONG size = 0;
    if (SUCCEEDED(v->GetUnsignedLargeIntegerValue(WPD_OBJECT_SIZE, &size))) it.size = size;

    GUID ct{};
    if (SUCCEEDED(v->GetGuidValue(WPD_OBJECT_CONTENT_TYPE, &ct)))
        it.isFolder = IsEqualGUID(ct, WPD_CONTENT_TYPE_FOLDER) || IsEqualGUID(ct, WPD_CONTENT_TYPE_FUNCTIONAL_OBJECT);

    PROPVARIANT pv;
    PropVariantInit(&pv);
    if (SUCCEEDED(v->GetValue(WPD_OBJECT_DATE_MODIFIED, &pv)) && pv.vt == VT_DATE) {
        SYSTEMTIME local{}, utc{};
        FILETIME ft{};
        // MTP 날짜는 기기 로컬 시간으로 온다
        if (VariantTimeToSystemTime(pv.date, &local) && TzSpecificLocalTimeToSystemTime(nullptr, &local, &utc) &&
            SystemTimeToFileTime(&utc, &ft))
            it.lastWrite = (uint64_t(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
    }
    PropVariantClear(&pv);
}

// 대량 속성 조회 콜백 (IPortableDevicePropertiesBulk). 객체마다 왕복하는 것보다 훨씬 빠르다.
class BulkCallback : public IPortableDevicePropertiesBulkCallback {
public:
    explicit BulkCallback(std::function<void(IPortableDeviceValuesCollection*)> onValues)
        : _onValues(std::move(onValues)), _done(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {}
    ~BulkCallback() { CloseHandle(_done); }

    HANDLE Done() const { return _done; }
    HRESULT Result() const { return _result; }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IPortableDevicePropertiesBulkCallback) {
            *ppv = static_cast<IPortableDevicePropertiesBulkCallback*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }
    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG r = InterlockedDecrement(&_ref);
        if (r == 0) delete this;
        return r;
    }
    STDMETHODIMP OnStart(REFGUID) override { return S_OK; }
    STDMETHODIMP OnProgress(REFGUID, IPortableDeviceValuesCollection* values) override
    {
        if (values) _onValues(values);
        return S_OK;
    }
    STDMETHODIMP OnEnd(REFGUID, HRESULT hr) override
    {
        _result = hr;
        SetEvent(_done);
        return S_OK;
    }

private:
    LONG _ref = 1;
    std::function<void(IPortableDeviceValuesCollection*)> _onValues;
    HANDLE _done;
    HRESULT _result = S_OK;
};

class Emitter {
public:
    Emitter(FmMtpEntryCallback cb, void* user, volatile long* progress) : _cb(cb), _user(user), _progress(progress) {}

    void Add(Item&& it)
    {
        _items.push_back(std::move(it));
        if (_progress) InterlockedIncrement(_progress);
        if (_items.size() >= kBatch) Flush();
    }

    bool Flush()
    {
        if (_items.empty()) return _keepGoing;
        std::vector<FmMtpEntry> e(_items.size());
        for (size_t i = 0; i < _items.size(); ++i) {
            e[i].objectId = _items[i].id.c_str();
            e[i].name = _items[i].name.c_str();
            e[i].size = _items[i].size;
            e[i].lastWrite = _items[i].lastWrite;
            e[i].isFolder = _items[i].isFolder ? 1 : 0;
        }
        if (_cb(e.data(), int(e.size()), _user) == 0) _keepGoing = false;
        _items.clear();
        return _keepGoing;
    }

    bool KeepGoing() const { return _keepGoing; }

private:
    FmMtpEntryCallback _cb;
    void* _user;
    volatile long* _progress;
    std::vector<Item> _items;
    bool _keepGoing = true;
};

HRESULT CancelledHr() { return HRESULT_FROM_WIN32(ERROR_CANCELLED); }

HRESULT GetObjectSize(Device* d, const wchar_t* objectId, uint64_t& size)
{
    ComPtr<IPortableDeviceKeyCollection> keys;
    HRESULT hr = CoCreateInstance(CLSID_PortableDeviceKeyCollection, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&keys));
    if (FAILED(hr)) return hr;
    keys->Add(WPD_OBJECT_SIZE);
    ComPtr<IPortableDeviceValues> values;
    hr = d->props->GetValues(objectId, keys.Get(), &values);
    if (FAILED(hr)) return hr;
    ULONGLONG s = 0;
    hr = values->GetUnsignedLargeIntegerValue(WPD_OBJECT_SIZE, &s);
    size = s;
    return hr;
}

void CopyId(PCWSTR id, wchar_t* buf, int len)
{
    if (buf && len > 0) wcsncpy_s(buf, size_t(len), id ? id : L"", _TRUNCATE);
}

} // namespace

// ───────────────────────── 기기 ─────────────────────────

FM_API int FM_CALL FmMtpListDevices(FmMtpDeviceCallback cb, void* user)
{
    if (!cb) return E_INVALIDARG;
    EnsureCom();
    ComPtr<IPortableDeviceManager> mgr;
    HRESULT hr = CoCreateInstance(CLSID_PortableDeviceManager, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&mgr));
    if (FAILED(hr)) return hr;
    mgr->RefreshDeviceList();

    DWORD count = 0;
    hr = mgr->GetDevices(nullptr, &count);
    if (FAILED(hr) || count == 0) return hr;
    std::vector<PWSTR> ids(count, nullptr);
    hr = mgr->GetDevices(ids.data(), &count);
    if (FAILED(hr)) return hr;

    bool keepGoing = true;
    for (DWORD i = 0; i < count; ++i) {
        if (keepGoing) {
            DWORD len = 0;
            std::wstring name;
            if (SUCCEEDED(mgr->GetDeviceFriendlyName(ids[i], nullptr, &len)) && len > 0) {
                name.resize(len);
                if (SUCCEEDED(mgr->GetDeviceFriendlyName(ids[i], name.data(), &len)))
                    name.resize(wcslen(name.c_str()));
                else
                    name.clear();
            }
            if (name.empty()) name = L"휴대용 기기";
            keepGoing = cb(ids[i], name.c_str(), user) != 0;
        }
        CoTaskMemFree(ids[i]);
    }
    return S_OK;
}

FM_API void* FM_CALL FmMtpOpen(const wchar_t* deviceId, int* hrOut)
{
    auto fail = [&](HRESULT hr) -> void* { if (hrOut) *hrOut = hr; return nullptr; };
    if (!deviceId) return fail(E_INVALIDARG);
    EnsureCom();

    auto d = std::make_unique<Device>();
    HRESULT hr = CoCreateInstance(CLSID_PortableDeviceFTM, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&d->dev));
    if (FAILED(hr)) return fail(hr);

    ComPtr<IPortableDeviceValues> info;
    hr = MakeClientInfo(GENERIC_READ | GENERIC_WRITE, info);
    if (FAILED(hr)) return fail(hr);
    hr = d->dev->Open(deviceId, info.Get());
    if (hr == E_ACCESSDENIED) {   // 읽기 전용으로 재시도
        MakeClientInfo(GENERIC_READ, info);
        hr = d->dev->Open(deviceId, info.Get());
    }
    if (FAILED(hr)) return fail(hr);

    hr = d->dev->Content(&d->content);
    if (SUCCEEDED(hr)) hr = d->content->Properties(&d->props);
    if (FAILED(hr)) return fail(hr);

    if (hrOut) *hrOut = S_OK;
    return d.release();
}

FM_API void FM_CALL FmMtpClose(void* device)
{
    auto* d = AsDevice(device);
    if (!d) return;
    if (d->dev) d->dev->Close();
    delete d;
}

FM_API void FM_CALL FmMtpCancel(void* device)
{
    auto* d = AsDevice(device);
    if (d && d->dev) d->dev->Cancel();
}

// ───────────────────────── 열거 ─────────────────────────

FM_API int FM_CALL FmMtpEnumChildren(void* device, const wchar_t* parentId, FmMtpEntryCallback cb, void* user,
                                     volatile long* cancelFlag, volatile long* progress)
{
    auto* d = AsDevice(device);
    if (!d || !parentId || !cb) return E_INVALIDARG;
    EnsureCom();

    // 1) 자식 objectId 목록 (빠름)
    ComPtr<IEnumPortableDeviceObjectIDs> en;
    HRESULT hr = d->content->EnumObjects(0, parentId, nullptr, &en);
    if (FAILED(hr)) return hr;

    std::vector<std::wstring> ids;
    for (;;) {
        if (cancelFlag && *cancelFlag) return CancelledHr();
        PWSTR batch[100] = {};
        ULONG fetched = 0;
        hr = en->Next(ARRAYSIZE(batch), batch, &fetched);
        for (ULONG i = 0; i < fetched; ++i) {
            ids.emplace_back(batch[i]);
            CoTaskMemFree(batch[i]);
        }
        if (progress && fetched) InterlockedExchangeAdd(progress, long(fetched));
        if (hr != S_OK) break;   // S_FALSE = 끝
    }
    if (FAILED(hr)) return hr;
    if (ids.empty()) return S_OK;

    ComPtr<IPortableDeviceKeyCollection> keys;
    hr = MakeKeys(keys);
    if (FAILED(hr)) return hr;

    Emitter emit(cb, user, progress);

    // 2) 속성: 대량 조회 우선
    ComPtr<IPortableDevicePropertiesBulk> bulk;
    ComPtr<IPortableDevicePropVariantCollection> idList;
    bool bulkOk = SUCCEEDED(d->props.As(&bulk)) &&
                  SUCCEEDED(CoCreateInstance(CLSID_PortableDevicePropVariantCollection, nullptr, CLSCTX_INPROC_SERVER,
                                             IID_PPV_ARGS(&idList)));
    if (bulkOk) {
        for (auto& id : ids) {
            PROPVARIANT pv;
            PropVariantInit(&pv);
            pv.vt = VT_LPWSTR;
            pv.pwszVal = const_cast<LPWSTR>(id.c_str());
            idList->Add(&pv);   // 복사된다
        }

        // 취소 후에도 기기가 콜백을 늦게 부를 수 있으므로 공유 상태 + 닫힘 표시로 보호
        struct Shared {
            std::mutex mx;
            bool closed = false;
            Emitter* emit = nullptr;
        };
        auto shared = std::make_shared<Shared>();
        shared->emit = &emit;
        auto& mx = shared->mx;
        auto* callback = new BulkCallback([shared](IPortableDeviceValuesCollection* values) {
            std::lock_guard<std::mutex> lk(shared->mx);
            if (shared->closed) return;
            Emitter& emit = *shared->emit;
            DWORD n = 0;
            values->GetCount(&n);
            for (DWORD i = 0; i < n; ++i) {
                ComPtr<IPortableDeviceValues> v;
                if (FAILED(values->GetAt(i, &v))) continue;
                Item it;
                ReadItem(v.Get(), it);
                if (!it.id.empty()) emit.Add(std::move(it));
            }
            emit.Flush();
        });

        GUID context{};
        hr = bulk->QueueGetValuesByObjectList(idList.Get(), keys.Get(), callback, &context);
        if (SUCCEEDED(hr)) hr = bulk->Start(context);
        if (SUCCEEDED(hr)) {
            for (;;) {
                if (WaitForSingleObject(callback->Done(), 100) == WAIT_OBJECT_0) {
                    hr = callback->Result();
                    break;
                }
                if ((cancelFlag && *cancelFlag) || !emit.KeepGoing()) {
                    bulk->Cancel(context);
                    WaitForSingleObject(callback->Done(), 3000);
                    hr = CancelledHr();
                    break;
                }
            }
            {
                std::lock_guard<std::mutex> lk(mx);
                if (SUCCEEDED(hr)) emit.Flush();
                shared->closed = true;
            }
            callback->Release();
            return hr;
        }
        {
            std::lock_guard<std::mutex> lk(mx);
            shared->closed = true;
        }
        callback->Release();
        // 대량 조회를 지원하지 않는 기기 → 아래 개별 조회로
    }

    // 3) 개별 조회
    for (auto& id : ids) {
        if ((cancelFlag && *cancelFlag) || !emit.KeepGoing()) return CancelledHr();
        ComPtr<IPortableDeviceValues> v;
        if (FAILED(d->props->GetValues(id.c_str(), keys.Get(), &v))) continue;
        Item it;
        it.id = id;
        ReadItem(v.Get(), it);
        emit.Add(std::move(it));
    }
    emit.Flush();
    return emit.KeepGoing() ? S_OK : CancelledHr();
}

// ───────────────────────── 전송 ─────────────────────────

FM_API int FM_CALL FmMtpDownload(void* device, const wchar_t* objectId, const wchar_t* localPath, int overwrite,
                                 FmProgressCallback cb, void* user, volatile long* cancelFlag)
{
    auto* d = AsDevice(device);
    if (!d || !objectId || !localPath) return E_INVALIDARG;
    EnsureCom();

    uint64_t total = 0;
    GetObjectSize(d, objectId, total);

    ComPtr<IPortableDeviceResources> res;
    HRESULT hr = d->content->Transfer(&res);
    if (FAILED(hr)) return hr;
    DWORD optimal = 0;
    ComPtr<IStream> src;
    hr = res->GetStream(objectId, WPD_RESOURCE_DEFAULT, STGM_READ, &optimal, &src);
    if (FAILED(hr)) return hr;

    HANDLE f = CreateFileW(localPath, GENERIC_WRITE, 0, nullptr, overwrite ? CREATE_ALWAYS : CREATE_NEW,
                           FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (f == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());

    std::vector<BYTE> buf(std::max<DWORD>(optimal, 256 * 1024));
    uint64_t done = 0;
    hr = S_OK;
    for (;;) {
        if (cancelFlag && *cancelFlag) { hr = CancelledHr(); break; }
        ULONG read = 0;
        HRESULT rh = src->Read(buf.data(), ULONG(buf.size()), &read);
        if (FAILED(rh)) { hr = rh; break; }
        if (read == 0) break;
        DWORD written = 0;
        if (!WriteFile(f, buf.data(), read, &written, nullptr) || written != read) {
            hr = HRESULT_FROM_WIN32(GetLastError());
            break;
        }
        done += read;
        if (cb && cb(done, total, user) != 0) { hr = CancelledHr(); break; }
        if (rh == S_FALSE) break;
    }
    CloseHandle(f);
    if (FAILED(hr)) DeleteFileW(localPath);   // 반쯤 받은 파일은 지운다
    return hr;
}

FM_API int FM_CALL FmMtpUpload(void* device, const wchar_t* parentId, const wchar_t* localPath, const wchar_t* name,
                               FmProgressCallback cb, void* user, volatile long* cancelFlag,
                               wchar_t* newObjectId, int newObjectIdLength)
{
    auto* d = AsDevice(device);
    if (!d || !parentId || !localPath || !name) return E_INVALIDARG;
    EnsureCom();

    HANDLE f = CreateFileW(localPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (f == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    LARGE_INTEGER size{};
    GetFileSizeEx(f, &size);

    ComPtr<IPortableDeviceValues> values;
    HRESULT hr = CoCreateInstance(CLSID_PortableDeviceValues, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&values));
    if (FAILED(hr)) { CloseHandle(f); return hr; }
    values->SetStringValue(WPD_OBJECT_PARENT_ID, parentId);
    values->SetUnsignedLargeIntegerValue(WPD_OBJECT_SIZE, ULONGLONG(size.QuadPart));
    values->SetStringValue(WPD_OBJECT_ORIGINAL_FILE_NAME, name);
    values->SetStringValue(WPD_OBJECT_NAME, name);

    ComPtr<IStream> dst;
    DWORD optimal = 0;
    hr = d->content->CreateObjectWithPropertiesAndData(values.Get(), &dst, &optimal, nullptr);
    if (FAILED(hr)) { CloseHandle(f); return hr; }

    std::vector<BYTE> buf(std::max<DWORD>(optimal, 256 * 1024));
    uint64_t done = 0;
    for (;;) {
        if (cancelFlag && *cancelFlag) { hr = CancelledHr(); break; }
        DWORD read = 0;
        if (!ReadFile(f, buf.data(), DWORD(buf.size()), &read, nullptr)) { hr = HRESULT_FROM_WIN32(GetLastError()); break; }
        if (read == 0) break;
        ULONG written = 0;
        hr = dst->Write(buf.data(), read, &written);
        if (FAILED(hr)) break;
        done += read;
        if (cb && cb(done, uint64_t(size.QuadPart), user) != 0) { hr = CancelledHr(); break; }
    }
    CloseHandle(f);

    if (FAILED(hr)) {
        dst->Revert();
        return hr;
    }
    hr = dst->Commit(STGC_DEFAULT);
    if (FAILED(hr)) return hr;

    ComPtr<IPortableDeviceDataStream> ds;
    if (newObjectId && SUCCEEDED(dst.As(&ds))) {
        PWSTR id = nullptr;
        if (SUCCEEDED(ds->GetObjectID(&id))) CopyId(id, newObjectId, newObjectIdLength);
        CoTaskMemFree(id);
    }
    return S_OK;
}

// ───────────────────────── 삭제 / 폴더 / 이름 ─────────────────────────

FM_API int FM_CALL FmMtpDelete(void* device, const wchar_t* objectId, int recursive)
{
    auto* d = AsDevice(device);
    if (!d || !objectId) return E_INVALIDARG;
    EnsureCom();
    ComPtr<IPortableDevicePropVariantCollection> list;
    HRESULT hr = CoCreateInstance(CLSID_PortableDevicePropVariantCollection, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&list));
    if (FAILED(hr)) return hr;
    PROPVARIANT pv;
    PropVariantInit(&pv);
    pv.vt = VT_LPWSTR;
    pv.pwszVal = const_cast<LPWSTR>(objectId);
    list->Add(&pv);
    ComPtr<IPortableDevicePropVariantCollection> results;
    return d->content->Delete(recursive ? PORTABLE_DEVICE_DELETE_WITH_RECURSION : PORTABLE_DEVICE_DELETE_NO_RECURSION,
                              list.Get(), &results);
}

FM_API int FM_CALL FmMtpCreateFolder(void* device, const wchar_t* parentId, const wchar_t* name,
                                     wchar_t* newObjectId, int newObjectIdLength)
{
    auto* d = AsDevice(device);
    if (!d || !parentId || !name) return E_INVALIDARG;
    EnsureCom();
    ComPtr<IPortableDeviceValues> values;
    HRESULT hr = CoCreateInstance(CLSID_PortableDeviceValues, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&values));
    if (FAILED(hr)) return hr;
    values->SetStringValue(WPD_OBJECT_PARENT_ID, parentId);
    values->SetStringValue(WPD_OBJECT_NAME, name);
    values->SetStringValue(WPD_OBJECT_ORIGINAL_FILE_NAME, name);
    values->SetGuidValue(WPD_OBJECT_CONTENT_TYPE, WPD_CONTENT_TYPE_FOLDER);
    values->SetGuidValue(WPD_OBJECT_FORMAT, WPD_OBJECT_FORMAT_PROPERTIES_ONLY);

    PWSTR id = nullptr;
    hr = d->content->CreateObjectWithPropertiesOnly(values.Get(), &id);
    if (SUCCEEDED(hr)) CopyId(id, newObjectId, newObjectIdLength);
    CoTaskMemFree(id);
    return hr;
}

FM_API int FM_CALL FmMtpRename(void* device, const wchar_t* objectId, const wchar_t* newName)
{
    auto* d = AsDevice(device);
    if (!d || !objectId || !newName) return E_INVALIDARG;
    EnsureCom();
    ComPtr<IPortableDeviceValues> values;
    HRESULT hr = CoCreateInstance(CLSID_PortableDeviceValues, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&values));
    if (FAILED(hr)) return hr;
    values->SetStringValue(WPD_OBJECT_ORIGINAL_FILE_NAME, newName);
    ComPtr<IPortableDeviceValues> results;
    hr = d->props->SetValues(objectId, values.Get(), &results);
    if (FAILED(hr)) return hr;
    HRESULT itemHr = S_OK;
    if (results && SUCCEEDED(results->GetErrorValue(WPD_OBJECT_ORIGINAL_FILE_NAME, &itemHr)) && FAILED(itemHr))
        return itemHr;
    return S_OK;
}

FM_API int FM_CALL FmMtpGetStorageInfo(void* device, const wchar_t* storageId, uint64_t* freeBytes, uint64_t* totalBytes)
{
    auto* d = AsDevice(device);
    if (!d || !storageId) return E_INVALIDARG;
    EnsureCom();
    ComPtr<IPortableDeviceKeyCollection> keys;
    HRESULT hr = CoCreateInstance(CLSID_PortableDeviceKeyCollection, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&keys));
    if (FAILED(hr)) return hr;
    keys->Add(WPD_STORAGE_FREE_SPACE_IN_BYTES);
    keys->Add(WPD_STORAGE_CAPACITY);
    ComPtr<IPortableDeviceValues> values;
    hr = d->props->GetValues(storageId, keys.Get(), &values);
    if (FAILED(hr)) return hr;
    ULONGLONG f = 0, t = 0;
    values->GetUnsignedLargeIntegerValue(WPD_STORAGE_FREE_SPACE_IN_BYTES, &f);
    values->GetUnsignedLargeIntegerValue(WPD_STORAGE_CAPACITY, &t);
    if (freeBytes) *freeBytes = f;
    if (totalBytes) *totalBytes = t;
    return S_OK;
}
