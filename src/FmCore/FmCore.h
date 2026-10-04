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

// 파일 찾기 (멀티스레드, I/O 워치독 내장)
FM_API void* FM_CALL FmSearchStart(const FmSearchParams* params, FmEntryBatchCallback cb, void* user);
FM_API void  FM_CALL FmSearchCancel(void* handle);
FM_API int   FM_CALL FmSearchGetStatus(void* handle, FmSearchStatus* status, wchar_t* currentDir, int currentDirLength);
FM_API void  FM_CALL FmSearchFree(void* handle);
