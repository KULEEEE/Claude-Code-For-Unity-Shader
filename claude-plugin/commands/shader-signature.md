---
name: shader-signature
description: Get function signature help for shader code
---

Get function signature help at a position in a shader file using `shader_signature_help`.

Ask the user for:
1. The shader file path (e.g., `Assets/Shaders/MyShader.shader`). If not provided, use `list_shaders` to help them choose.
2. The line number and character position (should be inside a function call).
3. Optionally, the user can provide shader source code directly via the `content` parameter.

After getting signature help:
1. Display the function signature clearly
2. Highlight the active parameter
3. Show parameter documentation if available
