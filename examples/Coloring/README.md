# Проверка цветов GLSL

Откройте эту папку в Visual Studio и сравните `shader.frag` и `shader.vert`.
`FragColor`, `TexCoord`, `ourColor`, `aPos` и uniform-переменные должны иметь
одинаковую категорию в объявлении и при использовании. `vec2`, `vec3`, `vec4`
и `sampler2D` — типы; `main`, `mix`, `texture` — функции; `layout`, `in`,
`out`, `uniform` — ключевые слова. `gl_Position` — встроенная глобальная переменная.

Цвета берутся из текущих настроек **Fonts and Colors** Visual Studio:
при наличии Visual Assist — **VA Method**, **VA Variable**, **VA Class** и **VA Macro**;
иначе — стандартные категории C++. Поэтому функции и данные должны выглядеть
так же, как в C++ с соответствующей палитрой. Параметр **Shader Tools → Use C++ colors for shaders**
включён по умолчанию; его можно отключить, чтобы вернуться к цветам TextMate.
