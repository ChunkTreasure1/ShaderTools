# Shader Tools for Visual Studio

GLSL and HLSL editing in Visual Studio, using [shader-sense](https://github.com/antaalt/shader-sense). WGSL support is basic.

Requires **Visual Studio 2022 17.14+ or Visual Studio 2026, x64**. Most editor testing has been done in VS 2022.

## What it does

- Syntax highlighting, completion, hover information and signature help.
- Compiler diagnostics through glslang, DXC and Naga.
- Go to definition, including files referenced by `#include`.
- Folding, parameter hints and dimming of inactive code.
- Shader variants and formatting through clang-format.

The language server and DXC are included in the VSIX. Formatting needs a separate `clang-format.exe`. Node.js and the .NET SDK are only needed when building or testing the extension.

## Install and try it

1. [Build the extension](docs/DEVELOPMENT.md).
2. Close Visual Studio and open `ShaderTools.VisualStudio/bin/Release/ShaderTools.VisualStudio.vsix`.
3. Start VS and open [examples/BasicShaders](examples/BasicShaders) as a folder, or open your own shader project.

If the older **GLSL language integration** extension is installed, disable it to avoid competing GLSL editors.

Commands are under **Extensions > Shader Tools**. **Shader Explorer** lists variants and symbols. **Show server output** opens the log; **Restart language server** restarts analysis.

Common shortcuts, with the default VS key bindings:

| Action | Shortcut |
| --- | --- |
| Completion | `Ctrl+Space` |
| Go to definition | `F12` or `Ctrl+click` |
| Signature help | `Ctrl+Shift+Space` |
| Format document | `Ctrl+K, Ctrl+D` |
| Comment / uncomment | `Ctrl+K, Ctrl+C` / `Ctrl+K, Ctrl+U` |

Ctrl+click follows VS's Go To Definition mouse setting. Its handler is included, but a live mouse test is still pending; server-side navigation is covered by the tests.

## Project settings

Use **Extensions > Shader Tools > Project settings** to open `.shadertools.json` in the solution or folder root. For a standalone file, the extension looks for the nearest parent configuration.

For example, an OpenGL project can use:

```json
{
  "includes": ["shaders/include"],
  "defines": { "USE_SHADOWS": "1" },
  "glsl": {
    "targetClient": "None",
    "spirvVersion": "None"
  }
}
```

Include paths are relative to the configuration file. Set the GLSL version in the shader's `#version` directive. For Vulkan, use a matching target such as `Vulkan1_3` with `SPIRV1_6`; see [BasicShaders](examples/BasicShaders/.shadertools.json) and the [settings schema](schemas/shadertools.schema.json).

Global settings are under **Tools > Options > Shader Tools > General**. Project settings take precedence. Configuration changes are loaded after saving; if a file is invalid, the last working settings are kept and the error is logged.

**Use C++ colors for shaders** uses Visual Assist colors when available, or the current C++ colors otherwise. Turn it off to use TextMate colors. Classification uses declarations in the current document, so some symbols from includes or macros may keep the default color.

For formatting, the extension looks for clang-format in the VS installation and `PATH`. You can also set **clang-format directory** and restart the language server. A project `.clang-format` file is handled by the formatter.

## HLSL Tools compatibility

Some VS installations also have the separate **HLSL Tools** editor. It reads `shadertoolsconfig.json`, while this extension reads `.shadertools.json`. Restarting our server does not reload the other editor's settings.

BasicShaders includes both configuration files. Close and reopen HLSL tabs after changing the HLSL Tools configuration. If you prefer that editor for HLSL, turn off **Enable HLSL** in our options and reopen the files.

## Shader variants

**Edit shader variants** opens `.shader-variants.json`. A variant sets the entry point, stage, defines and include paths for one shader:

```json
[
  {
    "name": "Fragment",
    "file": "shaders/material.frag",
    "entryPoint": "main",
    "stage": "fragment",
    "defines": { "USE_SHADOWS": "1" },
    "includes": ["shaders/include"]
  }
]
```

Select a variant in **Shader Explorer > Variants** and click **Activate**. **Automatic** clears the selection. **Refresh** reloads the list; **Load...** also accepts Shader Validator's variant database format. For an HLSL pixel shader, use the stage name `fragment`.

## Limits

- One solution/folder context is used per VS instance. Symbol search covers files indexed by the server.
- Build settings from `.vcxproj`, CMake or a game engine are not imported automatically.
- Symbol rename, find all references, GPU debugging and SPIR-V/DXIL export are not implemented.
- VS 2026 and some editor scenarios, including theme changes and multiple views of a file, still need more manual testing.

Build instructions: [DEVELOPMENT.md](docs/DEVELOPMENT.md). Bundled component licenses: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
