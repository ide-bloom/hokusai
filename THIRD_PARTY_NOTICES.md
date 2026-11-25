# Third-party notices

Hokusai is licensed under the Apache License 2.0 (see `LICENSE`). It uses the following third-party components,
each under its own license. Versions are those pinned in `Directory.Packages.props` and `native/agent/vcpkg.json`.

| Component | Used by | License |
|---|---|---|
| [Microsoft Detours](https://github.com/microsoft/Detours) | ODBC agent | MIT |
| [FlaUI](https://github.com/FlaUI/FlaUI) (FlaUI.Core, FlaUI.UIA3) | UI recorder, end-to-end tests | MIT |
| [Microsoft.Data.SqlClient](https://github.com/dotnet/SqlClient) | SQL Server access, Extended Events | MIT |
| [System.Data.Odbc](https://github.com/dotnet/runtime) | ODBC access | MIT |
| [System.Data.OleDb](https://github.com/dotnet/runtime) | Access (ACE) access | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | SQLite access | MIT |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) | DPAPI credential storage | MIT |
| [Microsoft.SqlServer.TransactSql.ScriptDom](https://github.com/microsoft/SqlScriptDOM) | T-SQL parsing | MIT |
| [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) | ETW file events | MIT |
| [Scriban](https://github.com/scriban/scriban) | Code generation templates | BSD-2-Clause |
| [System.CommandLine](https://github.com/dotnet/command-line-api) | CLI | MIT |
| [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) | Generated MCP servers, tests | Apache-2.0 |
| [Microsoft.Extensions.*](https://github.com/dotnet/runtime) and ASP.NET Core packages | Generated projects | MIT |
| [xUnit.net v3](https://github.com/xunit/xunit) | Tests, generated tests | Apache-2.0 |
| [Microsoft.NETFramework.ReferenceAssemblies](https://github.com/microsoft/dotnet) | Building the .NET Framework test app | MIT |

The full license texts are included in each package and in the linked repositories.

## BSD-2-Clause (Scriban)

Copyright (c) Alexandre Mutel. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following
   disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
   following disclaimer in the documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## MIT License (Microsoft components, FlaUI)

Copyright (c) Microsoft Corporation; Copyright (c) Roemer and the FlaUI contributors.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
