#pragma once

namespace hokusai
{

// Attaches the ODBC hooks inside a Detours transaction. Returns the Detours error code (NO_ERROR on success).
long AttachHooks();
long DetachHooks();

} // namespace hokusai
