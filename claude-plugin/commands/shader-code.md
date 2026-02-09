---
name: shader-code
description: Read shader source code with optional include resolution
---

Read a shader's source code using `get_shader_code`.

Ask the user for the shader path if not provided as an argument.
Ask whether to resolve includes (inline referenced .cginc/.hlsl files) - default is no.

Display the shader source code with syntax highlighting (use ```hlsl code blocks).
If includes are resolved, clearly mark where each include file's content begins and ends.
