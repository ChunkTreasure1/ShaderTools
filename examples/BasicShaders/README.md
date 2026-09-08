# Basic shader workspace

Open this directory as the workspace to try project settings, include resolution, and explicit shader variants. The GLSL pair draws a colored triangle and the HLSL pair provides equivalent vertex and pixel entry points.

`.shadertools.json` configures both compilers and adds `include` to the global include search path. `.shader-variants.json` supplies the stage and entry point for each shader. Relative paths are resolved from this workspace directory.

`shadertoolsconfig.json` provides the same include directory and macro to the separate **HLSL Tools** editor bundled with some Visual Studio installations. That editor does not read `.shadertools.json`. If it reports a missing `colors.hlsli`, close and reopen the HLSL source tabs after updating this example; restarting the Shader Tools language server does not reload HLSL Tools. Its configuration format is documented in the [HLSL Tools README](https://github.com/tgjones/HlslTools#custom-preprocessor-definitions-and-additional-include-directories).

In Shader Tools, use **Ctrl+click** on an include filename or a symbol to open its definition (subject to Visual Studio's Go To Definition mouse gesture setting). **F12** remains available from the keyboard.
