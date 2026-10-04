// FmShellMenu.cpp - 탐색기 컨텍스트 메뉴 (IContextMenu / IContextMenu2 / IContextMenu3)
#include "FmCore.h"

#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <wrl/client.h>

#include <vector>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")

using Microsoft::WRL::ComPtr;

namespace {

constexpr UINT kShellFirst = 1000;   // 앱 항목은 1~999
constexpr UINT kShellLast = 0x7FFF;

// TrackPopupMenu 동안 창 프로시저를 잠깐 바꿔 끼워, 하위 메뉴("보내기", 7-Zip 등)와
// 소유자 그리기 메시지를 셸 확장에 전달한다.
thread_local IContextMenu2* g_cm2 = nullptr;
thread_local IContextMenu3* g_cm3 = nullptr;
thread_local WNDPROC g_oldProc = nullptr;

LRESULT CALLBACK MenuWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    bool menuMsg = msg == WM_INITMENUPOPUP || msg == WM_MENUCHAR ||
                   ((msg == WM_DRAWITEM || msg == WM_MEASUREITEM) && wp == 0);
    if (menuMsg) {
        if (g_cm3) {
            LRESULT r = 0;
            if (SUCCEEDED(g_cm3->HandleMenuMsg2(msg, wp, lp, &r))) return r;
        } else if (g_cm2 && msg != WM_MENUCHAR) {
            if (SUCCEEDED(g_cm2->HandleMenuMsg(msg, wp, lp))) return msg == WM_INITMENUPOPUP ? 0 : TRUE;
        }
    }
    return CallWindowProcW(g_oldProc, hwnd, msg, wp, lp);
}

} // namespace

FM_API int FM_CALL FmShellContextMenu(void* hwndPtr, const wchar_t* folder, const wchar_t* namesDoubleNull,
                                      int screenX, int screenY, const FmMenuItem* custom, int customCount,
                                      int extendedVerbs, int* shellInvoked)
{
    HWND hwnd = static_cast<HWND>(hwndPtr);
    if (shellInvoked) *shellInvoked = 0;

    // ── 셸 메뉴 객체 준비 ──
    ComPtr<IContextMenu> cm;
    PIDLIST_ABSOLUTE pidlFolder = nullptr;
    std::vector<PITEMID_CHILD> children;

    if (folder && *folder) {
        ComPtr<IShellFolder> psf;
        if (SUCCEEDED(SHParseDisplayName(folder, nullptr, &pidlFolder, 0, nullptr)) &&
            SUCCEEDED(SHBindToObject(nullptr, pidlFolder, nullptr, IID_PPV_ARGS(&psf)))) {
            for (const wchar_t* n = namesDoubleNull; n && *n; n += wcslen(n) + 1) {
                PIDLIST_RELATIVE child = nullptr;
                if (SUCCEEDED(psf->ParseDisplayName(hwnd, nullptr, const_cast<LPWSTR>(n), nullptr, &child, nullptr)))
                    children.push_back(static_cast<PITEMID_CHILD>(child));
            }
            bool wantItems = namesDoubleNull && *namesDoubleNull;
            if (!wantItems)
                psf->CreateViewObject(hwnd, IID_PPV_ARGS(&cm));   // 폴더 배경 (붙여넣기, 속성 등)
            else if (!children.empty())
                psf->GetUIObjectOf(hwnd, UINT(children.size()), (PCUITEMID_CHILD_ARRAY)children.data(),
                                   IID_IContextMenu, nullptr, reinterpret_cast<void**>(cm.GetAddressOf()));
        }
    }

    // ── 메뉴 구성: 앱 항목 → 구분선 → 셸 항목 ──
    HMENU menu = CreatePopupMenu();
    for (int i = 0; i < customCount; ++i) {
        if (custom[i].flags & FM_MENU_SEPARATOR)
            AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        else
            AppendMenuW(menu, MF_STRING | ((custom[i].flags & FM_MENU_DISABLED) ? MF_GRAYED : 0),
                        UINT_PTR(custom[i].id), custom[i].text ? custom[i].text : L"");
    }
    if (cm) {
        if (customCount > 0) AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        UINT flags = CMF_NORMAL | CMF_CANRENAME | (extendedVerbs ? CMF_EXTENDEDVERBS : 0);
        if (FAILED(cm->QueryContextMenu(menu, UINT(GetMenuItemCount(menu)), kShellFirst, kShellLast, flags)))
            cm.Reset();
    }
    // 끝에 남은 구분선 정리
    int count = GetMenuItemCount(menu);
    while (count > 0 && (GetMenuState(menu, UINT(count - 1), MF_BYPOSITION) & MF_SEPARATOR)) {
        DeleteMenu(menu, UINT(count - 1), MF_BYPOSITION);
        --count;
    }

    // ── 표시 ──
    ComPtr<IContextMenu2> cm2;
    ComPtr<IContextMenu3> cm3;
    if (cm) {
        cm.As(&cm3);
        if (!cm3) cm.As(&cm2);
    }
    g_cm2 = cm2.Get();
    g_cm3 = cm3.Get();
    g_oldProc = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(MenuWndProc)));

    UINT cmd = count > 0 ? UINT(TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, hwnd, nullptr)) : 0;

    SetWindowLongPtrW(hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(g_oldProc));
    g_cm2 = nullptr;
    g_cm3 = nullptr;
    g_oldProc = nullptr;

    // ── 실행 ──
    int result = 0;
    if (cmd > 0 && cmd < kShellFirst) {
        result = int(cmd);
    } else if (cmd >= kShellFirst && cm) {
        UINT offset = cmd - kShellFirst;
        wchar_t verb[64] = {};
        if (SUCCEEDED(cm->GetCommandString(offset, GCS_VERBW, nullptr, reinterpret_cast<LPSTR>(verb), ARRAYSIZE(verb))) &&
            _wcsicmp(verb, L"rename") == 0) {
            result = FM_MENU_RENAME;   // 셸 이름 바꾸기는 탐색기 창 안에서만 동작하므로 앱이 처리
        } else {
            CMINVOKECOMMANDINFOEX ici{};
            ici.cbSize = sizeof(ici);
            ici.fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
            if (GetKeyState(VK_CONTROL) < 0) ici.fMask |= CMIC_MASK_CONTROL_DOWN;
            if (GetKeyState(VK_SHIFT) < 0) ici.fMask |= CMIC_MASK_SHIFT_DOWN;
            ici.hwnd = hwnd;
            ici.lpVerb = MAKEINTRESOURCEA(offset);
            ici.lpVerbW = MAKEINTRESOURCEW(offset);
            ici.lpDirectoryW = folder;
            ici.nShow = SW_SHOWNORMAL;
            ici.ptInvoke = POINT{ screenX, screenY };
            if (SUCCEEDED(cm->InvokeCommand(reinterpret_cast<LPCMINVOKECOMMANDINFO>(&ici))) && shellInvoked)
                *shellInvoked = 1;
        }
    }

    DestroyMenu(menu);
    for (auto c : children) CoTaskMemFree(c);
    CoTaskMemFree(pidlFolder);
    return result;
}
