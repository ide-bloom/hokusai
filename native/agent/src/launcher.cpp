// hokusai_launcher{32,64}.exe: starts a program with the agent loaded, or loads the agent into a running
// process the user explicitly chose. The launcher must match the target's bitness; Hokusai picks the right
// one.
//
//   hokusai_launcher64 launch <agent.dll> <program.exe> [arguments...]   prints "pid=<n>"
//   hokusai_launcher64 launch-wait ...                                     also waits and prints
//   "exit=<code>" hokusai_launcher64 attach <agent.dll> <pid>
#include <windows.h>

#include <detours/detours.h>
#include <shellapi.h>

#include <cstdio>
#include <string>

namespace
{

std::wstring Quote(const std::wstring& arg)
{
    if (!arg.empty() && arg.find_first_of(L" \t\"") == std::wstring::npos)
    {
        return arg;
    }

    std::wstring quoted = L"\"";
    size_t backslashes = 0;
    for (const wchar_t c : arg)
    {
        if (c == L'\\')
        {
            ++backslashes;
            continue;
        }

        quoted.append(c == L'"' ? backslashes * 2 + 1 : backslashes, L'\\');
        backslashes = 0;
        quoted += c;
    }

    quoted.append(backslashes * 2, L'\\');
    return quoted + L"\"";
}

int Fail(const wchar_t* what)
{
    fwprintf(stderr, L"%ls failed: error %lu\n", what, GetLastError());
    return 1;
}

int Launch(const std::wstring& agent, int argc, wchar_t** argv, bool wait)
{
    std::wstring commandLine = Quote(argv[0]);
    for (int i = 1; i < argc; ++i)
    {
        commandLine += L" " + Quote(argv[i]);
    }

    char agentPath[MAX_PATH * 3] = {};
    WideCharToMultiByte(CP_ACP, 0, agent.c_str(), -1, agentPath, sizeof(agentPath), nullptr, nullptr);
    LPCSTR dlls[] = {agentPath};
    STARTUPINFOW si{sizeof(si)};
    PROCESS_INFORMATION pi{};

    // A console program would otherwise share the launcher's (often redirected) standard handles and die when
    // the launcher exits and its pipe closes; give it a console of its own. GUI programs are unaffected.
    SHFILEINFOW info{};
    const auto type = SHGetFileInfoW(argv[0], 0, &info, sizeof(info), SHGFI_EXETYPE);
    const bool console = type != 0 && HIWORD(type) == 0;
    const DWORD flags = CREATE_DEFAULT_ERROR_MODE | (console ? CREATE_NEW_CONSOLE : 0);
    if (!DetourCreateProcessWithDllsW(argv[0], commandLine.data(), nullptr, nullptr, FALSE, flags, nullptr,
                                      nullptr, &si, &pi, 1, dlls, nullptr))
    {
        return Fail(L"DetourCreateProcessWithDlls");
    }

    wprintf(L"pid=%lu\n", pi.dwProcessId);
    fflush(stdout);
    CloseHandle(pi.hThread);
    if (wait)
    {
        // Used by diagnostics and tests: report how the program ended.
        DWORD exitCode = 0;
        WaitForSingleObject(pi.hProcess, INFINITE);
        GetExitCodeProcess(pi.hProcess, &exitCode);
        wprintf(L"exit=0x%08lx\n", exitCode);
    }

    CloseHandle(pi.hProcess);
    return 0;
}

int Attach(const std::wstring& agent, DWORD pid)
{
    HANDLE process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                                     PROCESS_VM_WRITE | PROCESS_VM_READ,
                                 FALSE, pid);
    if (process == nullptr)
    {
        return Fail(L"OpenProcess");
    }

    const SIZE_T bytes = (agent.size() + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (remote == nullptr || !WriteProcessMemory(process, remote, agent.c_str(), bytes, nullptr))
    {
        CloseHandle(process);
        return Fail(L"WriteProcessMemory");
    }

    // kernel32 is mapped at the same address in every process of the same bitness.
    auto* loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(
        GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
    HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
    if (thread == nullptr)
    {
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        CloseHandle(process);
        return Fail(L"CreateRemoteThread");
    }

    WaitForSingleObject(thread, 30000);
    DWORD exitCode = 0;
    GetExitCodeThread(thread, &exitCode);
    CloseHandle(thread);
    VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    CloseHandle(process);
    if (exitCode == 0)
    {
        fwprintf(stderr, L"The agent did not load into process %lu.\n", pid);
        return 1;
    }

    wprintf(L"pid=%lu\n", pid);
    return 0;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc >= 4 && (std::wstring(argv[1]) == L"launch" || std::wstring(argv[1]) == L"launch-wait"))
    {
        return Launch(argv[2], argc - 3, argv + 3, std::wstring(argv[1]) == L"launch-wait");
    }

    if (argc == 4 && std::wstring(argv[1]) == L"attach")
    {
        return Attach(argv[2], static_cast<DWORD>(std::wcstoul(argv[3], nullptr, 10)));
    }

    fwprintf(stderr, L"usage: %ls launch <agent.dll> <program.exe> [args...] | attach <agent.dll> <pid>\n",
             argv[0]);
    return 2;
}
