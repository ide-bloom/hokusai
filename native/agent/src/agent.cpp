// Entry point of hokusai_agent{32,64}.dll. Loaded only into processes the user launched through Hokusai or
// explicitly attached (see launcher.cpp); it opens the event ring and installs the ODBC hooks.
#include "hooks.h"
#include "json.h"
#include "ring.h"

#include <windows.h>

#include <detours/detours.h>

namespace
{
bool g_attached = false;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    // Detours' helper process (used for cross-bitness launches) loads the DLL only to call ordinal 1.
    if (DetourIsHelperProcess())
    {
        return TRUE;
    }

    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
    {
        DetourRestoreAfterWith();
        if (!hokusai::RingOpen())
        {
            return TRUE; // without the ring there is nowhere to report; leave the process untouched
        }

        // odbc32.dll is a static import of this DLL, so it is loaded before DllMain and hooking is safe here.
        const long error = hokusai::AttachHooks();
        g_attached = error == NO_ERROR;
        hokusai::JsonWriter w;
        w.Begin()
            .String("t", "hello")
            .Number("pid", static_cast<std::int64_t>(GetCurrentProcessId()))
            .Number("bits", static_cast<std::int64_t>(sizeof(void*) * 8))
            .Number("hookError", error)
            .End();
        hokusai::RingWrite(w.Text());
        break;
    }
    case DLL_PROCESS_DETACH:
        if (g_attached)
        {
            hokusai::DetachHooks();
        }

        hokusai::RingClose();
        break;
    default:
        break;
    }

    return TRUE;
}
