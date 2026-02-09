---
name: shader-analyze
description: Analyze shader variants and keyword combinations
---

Analyze a Unity shader's variant combinations using `analyze_shader_variants`.

Ask the user for the shader path if not provided as an argument.
If you don't know the available shaders, use `list_shaders` first to show them.

After analysis:
1. Show the total variant count
2. List all keyword combinations
3. If variant count > 1000, warn about variant explosion and suggest optimizations:
   - Convert unused `multi_compile` to `shader_feature`
   - Combine related keywords
   - Use `#pragma skip_variants`
4. If variant count is reasonable, confirm it's healthy
