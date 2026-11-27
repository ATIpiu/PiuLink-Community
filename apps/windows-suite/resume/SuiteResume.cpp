#include <windows.h>
#include <commctrl.h>
#include <msi.h>
#include <tlhelp32.h>
#include <shlobj.h>
#include <shldisp.h>
#include <exdisp.h>
#include <wrl/client.h>
#include <comdef.h>
#include <string>
#include <BootstrapperApplicationTypes.h>
#include <BAFunctions.h>

using Microsoft::WRL::ComPtr;

namespace {
struct Role {
    const wchar_t* package;
    const wchar_t* executable;
    const wchar_t* arguments;
    std::wstring path;
    bool resume = false;
};

Role roles[] = {{L"Host", L"PiuLink.Host.exe", L"--silent"}, {L"Client", L"PiuLink.Client.exe", L""}};
ComPtr<IBootstrapperEngine> engine;

DWORD RunningInThisSession(const Role& role) {
    DWORD session = 0;
    if (role.path.empty() || !ProcessIdToSessionId(GetCurrentProcessId(), &session) || !session) return 0;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    DWORD found = 0;
    for (BOOL more = Process32FirstW(snapshot, &entry); more; more = Process32NextW(snapshot, &entry)) {
        DWORD otherSession = 0;
        if (_wcsicmp(entry.szExeFile, role.executable) != 0 ||
            !ProcessIdToSessionId(entry.th32ProcessID, &otherSession) || otherSession != session) continue;
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
        if (!process) continue;
        wchar_t path[32768];
        DWORD length = ARRAYSIZE(path);
        if (QueryFullProcessImageNameW(process, 0, path, &length) && !_wcsicmp(role.path.c_str(), path))
            found = entry.th32ProcessID;
        CloseHandle(process);
        if (found) break;
    }
    CloseHandle(snapshot);
    return found;
}

// Ask the interactive desktop shell to launch the installed program. This also keeps
// the tray application in the desktop user's context if Setup was started elevated.
HRESULT LaunchFromDesktop(const Role& role) {
    ComPtr<IShellWindows> windows;
    HRESULT hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&windows));
    if (FAILED(hr)) return hr;
    _variant_t empty, desktop(static_cast<long>(CSIDL_DESKTOP));
    long window = 0;
    ComPtr<IDispatch> dispatch;
    hr = windows->FindWindowSW(&desktop, &empty, SWC_DESKTOP, &window, SWFO_NEEDDISPATCH, &dispatch);
    if (FAILED(hr) || !dispatch) return FAILED(hr) ? hr : E_FAIL;
    ComPtr<IServiceProvider> provider;
    if (FAILED(hr = dispatch.As(&provider))) return hr;
    ComPtr<IShellBrowser> browser;
    if (FAILED(hr = provider->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser)))) return hr;
    ComPtr<IShellView> view;
    if (FAILED(hr = browser->QueryActiveShellView(&view))) return hr;
    ComPtr<IDispatch> background;
    if (FAILED(hr = view->GetItemObject(SVGIO_BACKGROUND, IID_PPV_ARGS(&background)))) return hr;
    ComPtr<IShellFolderViewDual> folder;
    if (FAILED(hr = background.As(&folder))) return hr;
    ComPtr<IDispatch> application;
    if (FAILED(hr = folder->get_Application(&application))) return hr;
    ComPtr<IShellDispatch2> shell;
    if (FAILED(hr = application.As(&shell))) return hr;
    return shell->ShellExecute(_bstr_t(role.path.c_str()), _variant_t(role.arguments), empty,
                               _variant_t(L"open"), _variant_t(static_cast<long>(SW_SHOWNORMAL)));
}

HRESULT LaunchAsSetupUser(const Role& role) {
    HANDLE token;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return HRESULT_FROM_WIN32(GetLastError());
    TOKEN_ELEVATION elevation{};
    DWORD size = 0;
    BOOL known = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size);
    CloseHandle(token);
    if (!known || elevation.TokenIsElevated) return E_ACCESSDENIED;
    SHELLEXECUTEINFOW launch{sizeof(launch)};
    launch.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
    launch.lpFile = role.path.c_str();
    launch.lpParameters = role.arguments;
    launch.nShow = SW_SHOWNORMAL;
    return ShellExecuteExW(&launch) ? S_OK : HRESULT_FROM_WIN32(GetLastError());
}

void Log(const Role& role, const wchar_t* action, unsigned long value) {
    wchar_t message[160];
    swprintf_s(message, L"PiuLink suite resume: %s %s (0x%lx)", role.package, action, value);
    engine->Log(BOOTSTRAPPER_LOG_LEVEL_STANDARD, message);
}

HRESULT WINAPI OnMessage(BA_FUNCTIONS_MESSAGE message, const LPVOID args, LPVOID, LPVOID) {
    if (message == BA_FUNCTIONS_MESSAGE_ONEXECUTEPACKAGEBEGIN) {
        const auto& pkg = *static_cast<const BA_ONEXECUTEPACKAGEBEGIN_ARGS*>(args);
        if (!pkg.fExecute || (pkg.action != BOOTSTRAPPER_ACTION_STATE_INSTALL &&
            pkg.action != BOOTSTRAPPER_ACTION_STATE_REPAIR && pkg.action != BOOTSTRAPPER_ACTION_STATE_MINOR_UPGRADE))
            return S_OK;
        for (auto& role : roles) {
            if (wcscmp(pkg.wzPackageId, role.package) != 0) continue;
            const DWORD pid = RunningInThisSession(role);
            role.resume = pid != 0;
            if (pid) Log(role, L"was running", pid);
        }
    } else if (message == BA_FUNCTIONS_MESSAGE_ONAPPLYCOMPLETE) {
        const auto& apply = *static_cast<const BA_ONAPPLYCOMPLETE_ARGS*>(args);
        const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        for (auto& role : roles) {
            if (!role.resume) continue;
            role.resume = false;
            if (apply.restart != BOOTSTRAPPER_APPLY_RESTART_NONE || RunningInThisSession(role) ||
                GetFileAttributesW(role.path.c_str()) == INVALID_FILE_ATTRIBUTES) continue;
            HRESULT hr = E_FAIL;
            try { hr = LaunchFromDesktop(role); } catch (const _com_error& error) { hr = error.Error(); }
            if (FAILED(hr) && !RunningInThisSession(role)) hr = LaunchAsSetupUser(role);
            Log(role, L"desktop launch", hr);
        }
        if (SUCCEEDED(com)) CoUninitialize();
    } else if (message != BA_FUNCTIONS_MESSAGE_ONTHEMELOADED) {
        return E_NOTIMPL;
    }
    return S_OK;
}
} // namespace

extern "C" __declspec(dllexport) HRESULT WINAPI BAFunctionsCreate(const BA_FUNCTIONS_CREATE_ARGS* args,
                                                                  BA_FUNCTIONS_CREATE_RESULTS* result) {
    engine = args->pEngine;
    PWSTR folder = nullptr;
    HRESULT hr = SHGetKnownFolderPath(FOLDERID_ProgramFilesX64, 0, nullptr, &folder);
    if (FAILED(hr)) return hr;
    try {
        for (auto& role : roles)
            role.path = std::wstring(folder) + L"\\ATIPiu Labs\\PiuLink " + role.package + L"\\" + role.executable;
    } catch (const std::bad_alloc&) { hr = E_OUTOFMEMORY; }
    CoTaskMemFree(folder);
    result->pfnBAFunctionsProc = OnMessage;
    result->pvBAFunctionsProcContext = nullptr;
    return hr;
}

extern "C" __declspec(dllexport) void WINAPI BAFunctionsDestroy(const BA_FUNCTIONS_DESTROY_ARGS*, BA_FUNCTIONS_DESTROY_RESULTS*) {
    engine.Reset();
}
