# GLSL coloring sample

Open this folder in Visual Studio and compare `shader.frag` and `shader.vert`.
`FragColor`, `TexCoord`, `ourColor`, `aPos`, and uniform variables should use
the same color category in declarations and references. `vec2`, `vec3`, `vec4`,
and `sampler2D` are types; `main`, `mix`, and `texture` are functions; `layout`,
`in`, `out`, and `uniform` are keywords. `gl_Position` is a built-in global variable.

Colors come from Visual Studio's **Fonts and Colors** settings. If Visual Assist
is available, the extension uses categories such as **VA Method**, **VA Variable**,
**VA Class**, and **VA Macro**; otherwise, it uses standard C++ categories.
Functions and variables should therefore follow your C++ color settings.
**Shader Tools → Use C++ colors for shaders** is enabled by default.
Turn it off to use TextMate colors instead.
