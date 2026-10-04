// FmCore - TvCommander 네이티브 파일시스템 모듈 (C ABI)
#pragma once
#include <stdint.h>

#ifdef FMCORE_EXPORTS
#define FM_API extern "C" __declspec(dllexport)
#else
#define FM_API extern "C" __declspec(dllimport)
#endif

#define FM_CALL __stdcall

// 디렉터리 항목. 열거 시 name = 파일 이름, 검색 시 name = 전체 경로.
// name 포인터는 콜백 안에서만 유효하다.
typedef struct FmEntry {
    const wchar_t* name;
    uint64_t size;
    uint64_t lastWrite;   // FILETIME (UTC, 1601 기준 100ns)
    uint32_t attributes;
    uint32_t reserved;
} FmEntry;

// 0 을 반환하면 열거를 중단한다.
typedef int (FM_CALL* FmEntryBatchCallback)(const FmEntry* entries, int count, void* user);
// 0 이 아닌 값을 반환하면 복사를 취소한다.
typedef int (FM_CALL* FmProgressCallback)(uint64_t transferred, uint64_t total, void* user);

typedef struct FmSearchParams {
    const wchar_t* root;
    const wchar_t* mask;      // "*.txt;*.log" (PathMatchSpecEx, ; 구분)
    const wchar_t* text;      // 내용 검색 문자열, NULL/빈 문자열이면 사용 안 함
    int caseSensitive;
    int recursive;
    int includeHidden;
    int threadCount;
    int ioTimeoutMs;          // 단일 I/O 호출이 이 시간을 넘으면 CancelSynchronousIo
} FmSearchParams;

typedef struct FmSearchStatus {
    int running;
    int reserved;
    int64_t dirsScanned;
    int64_t filesScanned;
    int64_t found;
    int64_t errors;
} FmSearchStatus;

// 공통
FM_API int   FM_CALL FmGetVersion(void);
FM_API void* FM_CALL FmOpenCurrentThread(void);
FM_API int   FM_CALL FmCancelThreadIo(void* thread);
FM_API void  FM_CALL FmCloseHandle(void* handle);
FM_API int   FM_CALL FmCompareNatural(const wchar_t* a, const wchar_t* b);

// 디렉터리 열거 (반환: Win32 오류 코드, 0 = 성공)
FM_API int FM_CALL FmEnumDirectory(const wchar_t* path, FmEntryBatchCallback cb, void* user,
                                   volatile long* cancelFlag, volatile long* progress);

// 파일 작업
FM_API int FM_CALL FmCopyFile(const wchar_t* src, const wchar_t* dst, int overwrite,
                              FmProgressCallback cb, void* user, volatile long* cancelFlag);
FM_API int FM_CALL FmMoveFile(const wchar_t* src, const wchar_t* dst, int overwrite,
                              FmProgressCallback cb, void* user, volatile long* cancelFlag);
FM_API int FM_CALL FmShellDelete(const wchar_t* pathsDoubleNull, int permanent, void* hwnd);

// 셸 / 볼륨
FM_API void* FM_CALL FmGetShellIcon(const wchar_t* name, int isDirectory, int large);
FM_API int   FM_CALL FmGetUncForDrive(const wchar_t* drive, wchar_t* buffer, int bufferLength);
FM_API int   FM_CALL FmGetDiskFree(const wchar_t* path, uint64_t* freeBytes, uint64_t* totalBytes);

// ───── 탐색기 컨텍스트 메뉴 ─────
// 앱 항목(custom)을 위에, 셸 항목(7-Zip, 보내기, 속성 등)을 아래에 붙인 팝업 메뉴를 띄운다. UI(STA) 스레드에서 호출.
//  folder == NULL 이면 앱 항목만. names 가 빈 목록("\0")이면 폴더 배경 메뉴.
//  반환: 고른 앱 항목 id (1~999), FM_MENU_RENAME (셸의 '이름 바꾸기' → 앱이 처리), 0 (취소 또는 셸 명령 실행)

#define FM_MENU_RENAME (-2)
#define FM_MENU_SEPARATOR 1
#define FM_MENU_DISABLED 2

typedef struct FmMenuItem {
    int id;
    int flags;
    const wchar_t* text;   // "보기\tF3" 처럼 탭 뒤는 단축키 표시
} FmMenuItem;

FM_API int FM_CALL FmShellContextMenu(void* hwnd, const wchar_t* folder, const wchar_t* namesDoubleNull,
                                      int screenX, int screenY, const FmMenuItem* custom, int customCount,
                                      int extendedVerbs, int* shellInvoked);

// ───── MTP (안드로이드폰 등 휴대용 기기, Windows Portable Devices) ─────
// 반환값은 HRESULT (0 = S_OK). 객체는 objectId 로 다룬다. 기기 루트의 objectId 는 L"DEVICE".

typedef struct FmMtpEntry {
    const wchar_t* objectId;
    const wchar_t* name;
    uint64_t size;
    uint64_t lastWrite;   // FILETIME (UTC)
    uint32_t isFolder;    // 폴더 또는 저장소(기능 객체)
    uint32_t reserved;
} FmMtpEntry;

typedef int (FM_CALL* FmMtpDeviceCallback)(const wchar_t* deviceId, const wchar_t* friendlyName, void* user);
typedef int (FM_CALL* FmMtpEntryCallback)(const FmMtpEntry* entries, int count, void* user);

FM_API int   FM_CALL FmMtpListDevices(FmMtpDeviceCallback cb, void* user);
FM_API void* FM_CALL FmMtpOpen(const wchar_t* deviceId, int* hr);
FM_API void  FM_CALL FmMtpClose(void* device);
FM_API void  FM_CALL FmMtpCancel(void* device);
FM_API int   FM_CALL FmMtpEnumChildren(void* device, const wchar_t* parentId, FmMtpEntryCallback cb, void* user,
                                       volatile long* cancelFlag, volatile long* progress);
FM_API int   FM_CALL FmMtpDownload(void* device, const wchar_t* objectId, const wchar_t* localPath, int overwrite,
                                   FmProgressCallback cb, void* user, volatile long* cancelFlag);
FM_API int   FM_CALL FmMtpUpload(void* device, const wchar_t* parentId, const wchar_t* localPath, const wchar_t* name,
                                 FmProgressCallback cb, void* user, volatile long* cancelFlag,
                                 wchar_t* newObjectId, int newObjectIdLength);
FM_API int   FM_CALL FmMtpDelete(void* device, const wchar_t* objectId, int recursive);
FM_API int   FM_CALL FmMtpCreateFolder(void* device, const wchar_t* parentId, const wchar_t* name,
                                       wchar_t* newObjectId, int newObjectIdLength);
FM_API int   FM_CALL FmMtpRename(void* device, const wchar_t* objectId, const wchar_t* newName);
FM_API int   FM_CALL FmMtpGetStorageInfo(void* device, const wchar_t* storageId, uint64_t* freeBytes, uint64_t* totalBytes);

// 파일 찾기 (멀티스레드, I/O 워치독 내장)
FM_API void* FM_CALL FmSearchStart(const FmSearchParams* params, FmEntryBatchCallback cb, void* user);
FM_API void  FM_CALL FmSearchCancel(void* handle);
FM_API int   FM_CALL FmSearchGetStatus(void* handle, FmSearchStatus* status, wchar_t* currentDir, int currentDirLength);
FM_API void  FM_CALL FmSearchFree(void* handle);
