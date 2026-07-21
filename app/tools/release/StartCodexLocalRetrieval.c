#define UNICODE
#define _UNICODE

#include <windows.h>
#include <wchar.h>

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int showCommand)
{
    (void)instance;
    (void)previous;
    (void)commandLine;
    (void)showCommand;

    wchar_t launcherPath[MAX_PATH];
    if (GetModuleFileNameW(NULL, launcherPath, MAX_PATH) == 0)
    {
        MessageBoxW(NULL, L"Could not locate the launcher.", L"Codex Local Retrieval", MB_ICONERROR);
        return 1;
    }

    wchar_t rootPath[MAX_PATH];
    wcscpy_s(rootPath, MAX_PATH, launcherPath);
    wchar_t *lastSlash = wcsrchr(rootPath, L'\\');
    if (lastSlash == NULL)
    {
        MessageBoxW(NULL, L"Could not locate the application folder.", L"Codex Local Retrieval", MB_ICONERROR);
        return 1;
    }
    *lastSlash = L'\0';

    wchar_t appPath[MAX_PATH];
    swprintf_s(appPath, MAX_PATH, L"%s\\app\\CodexLocalRetrieval.Native.exe", rootPath);

    wchar_t appDirectory[MAX_PATH];
    swprintf_s(appDirectory, MAX_PATH, L"%s\\app", rootPath);

    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);

    if (!CreateProcessW(appPath, NULL, NULL, NULL, FALSE, 0, NULL, appDirectory, &startup, &process))
    {
        MessageBoxW(NULL, L"Could not start app\\CodexLocalRetrieval.Native.exe. Keep the app folder next to this launcher.", L"Codex Local Retrieval", MB_ICONERROR);
        return 1;
    }

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 0;
}
