# Development

## Requirements

- Windows x64; Visual Studio 2022 17.14+ or Visual Studio 2026.
- **Visual Studio extension development**, MSBuild and the **.NET Framework 4.7.2 targeting pack**. Add missing components through Visual Studio Installer.
- **.NET SDK 10** and **Node.js 20+** for the full build and tests.
- **clang-format** for the formatting test; that check is skipped if it is missing.
- NuGet access for restore; GitHub access if the bundled server needs downloading.

Regular Windows PowerShell 5.1 and Developer PowerShell both work. The script finds MSBuild and sets the matching VS targets version.

## Build

Open PowerShell in the **repository root**, containing `Shader Tools for Visual Studio.sln`, `README.md` and `scripts`. Check it first:

```powershell
Get-Location
Test-Path .\scripts\build.ps1
```

The second command must print `True`. Otherwise, use `Set-Location` to enter your checkout directory. Quote paths containing spaces. The `ShaderTools.VisualStudio` subfolder is not the root.

Build and run the tests:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

The script verifies the server files, restores NuGet packages, builds the Release VSIX and runs unit and package tests. The execution-policy option applies only to this process.

Output: `ShaderTools.VisualStudio/bin/Release/ShaderTools.VisualStudio.vsix`.

For a build without tests:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -SkipTests
```

Use the script instead of `dotnet build` or a bare MSBuild command: it supplies the VSIX-specific build options. It does not install the extension.

## Run checks separately

After building, run these from the repository root:

```powershell
dotnet test .\ShaderTools.Tests\ShaderTools.Tests.csproj -c Release
node .\tests\lsp-smoke.mjs
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-package.ps1
```

The Node test uses the local server and bridge. The package test extracts the VSIX and checks its contents and server behavior, including include navigation, diagnostics, variants, formatting and shutdown.

The build workflow has been checked with Windows PowerShell 5.1 and VS 2022 Developer PowerShell, including a source copy without `bin` or `obj`. NuGet packages and the toolchain were already installed. Tests cover individual argument edits, unfinished and nested calls, stable hint identities, signature changes and completion signatures from the actual server, alongside the other editor and protocol checks.

## Troubleshooting

| Error | What to check |
| --- | --- |
| Script not found | Check the working directory with the commands above. |
| Script execution disabled | Use the full `powershell.exe` command above. Managed policies may require your administrator's help. |
| Hundreds of missing `Microsoft.VisualStudio` / `Newtonsoft` types | Update `build.ps1`. Older copies mixed VS 2022 shell settings with VS 2026 MSBuild and skipped NuGet reference imports. |
| MSBuild, VSSDK or reference assemblies missing | Use the build script and check the VS workload and targeting pack. |
| SDK or Node.js missing | Check the versions listed above and reopen the terminal after installation. |
| DLL or VSIX locked | Close VS instances using this development build and retry. |

For restore/download failures, check the first error and your package sources or network connection. Include the command, working directory and first error when reporting a build problem.

## Layout

- `ShaderTools.VisualStudio`: VS integration and editor features; .NET Framework 4.7.2.
- `ShaderTools.Core`: configuration, variants, protocol and classification; .NET Standard 2.0.
- `ShaderTools.Bridge`: server process and connection; .NET Framework 4.7.2.
- `ShaderTools.Tests`: .NET 10 unit tests, run separately from the VS solution.
- `tests/lsp-smoke.mjs`: tests against the actual server.

## Check in VS

Open the solution, set `ShaderTools.VisualStudio` as the startup project and press **F5** to use the experimental `Exp` instance. Close it before redeploying. For a Release installation, close VS and open the generated VSIX.

Use `examples/BasicShaders` and `examples/Coloring` to check completion, navigation, diagnostics while typing, variants, settings reload, colors, folding and hints. Also test theme changes, Save As, multiple views and workspace changes.

For argument hints, declare `float example(float first, int second);` and type `example(3.3, 2` in a function body. `first:` should appear when you enter `3`, and `second:` when you enter `2`, without waiting for `)`. Deleting `2` should remove only `second:`. This sequence, an empty argument with a closing parenthesis, and parameter renames at both call sites were checked in a separate VS 2022 test profile for 0.2.4. Unit tests also cover nested and multiline calls. Repeat these checks when changing hint handling.

Automated tests do not cover the whole editor. The latest Ctrl+click handler still needs a live mouse test; earlier GLSL highlighting, folding and hints were checked in VS 2022. HLSL may use the separate HLSL Tools editor; see the BasicShaders README for its configuration.

## Update the server

Version, URL and hashes are pinned in `scripts/acquire-server.ps1`. Update the bundled notices and run the full build when changing them. Check the released server's capabilities before adding client features.
