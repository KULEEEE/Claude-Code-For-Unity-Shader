---
name: shader-compile
description: Compile a shader and check for errors
---

Compile a Unity shader using `compile_shader`.

Ask the user for the shader path if not provided as an argument (e.g., `Assets/Shaders/MyShader.shader`).
If you don't know the available shaders, use `list_shaders` first to show them, then let the user choose.

After compilation:
1. Report whether it succeeded or failed
2. List any errors with line numbers
3. List any warnings
4. Show the variant count
5. If there are errors, suggest specific fixes based on the error messages
