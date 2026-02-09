# Unity Shader Development Context

## Quick Commands

| Command | Description |
|---------|-------------|
| `/shader` | Interactive tool menu (recommended for first-time use) |
| `/shader-help` | Full project shader health analysis |
| `/shader-list` | List all shaders in the project |
| `/shader-compile` | Compile and check for errors |
| `/shader-analyze` | Analyze variant count and keywords |
| `/shader-code` | Read shader source code |
| `/shader-props` | View shader property definitions |
| `/material-info` | Inspect material settings |
| `/shader-logs` | View shader console logs |
| `/shader-status` | Check Unity connection and environment |

## Available Tools

- **compile_shader**: Compile a shader and check for errors/warnings. Always use after modifying shader code.
- **analyze_shader_variants**: Analyze keyword combinations and variant count. Use to detect variant explosion.
- **get_shader_properties**: Query shader property definitions (name, type, default value, attributes).
- **get_shader_code**: Read shader source code with optional include file resolution.
- **get_material_info**: Inspect material settings, property values, and active keywords.
- **list_shaders**: List all shaders in the project with optional name/path filtering.
- **get_shader_logs**: Retrieve shader-related console log entries from Unity Editor.

## Available Resources

- `unity://pipeline/info` - Current render pipeline (Built-in/URP/HDRP) and its settings
- `unity://shader/includes` - Project include files (.cginc/.hlsl) with contents
- `unity://shader/keywords` - All global/local shader keywords in use
- `unity://editor/platform` - Build target, Graphics API, Unity version

## Workflow Guide

### After Shader Modification
1. Use `compile_shader` to verify compilation succeeds
2. Check for errors and warnings in the result
3. If errors exist, analyze the error messages and suggest fixes

### Variant Analysis
1. Use `analyze_shader_variants` to check variant count
2. If variant count > 1000, warn the user about variant explosion
3. Suggest keyword optimization strategies:
   - Convert unused `multi_compile` to `shader_feature`
   - Combine related keywords
   - Use `#pragma skip_variants` for unused combinations

### Shader Inspection
1. Use `get_shader_code` with `resolveIncludes: true` to see full source
2. Use `get_shader_properties` to understand the property interface
3. Use `get_material_info` to see how materials use the shader

### Pipeline-Aware Development
1. Check `unity://pipeline/info` before suggesting shader changes
2. URP shaders use `Packages/com.unity.render-pipelines.universal/ShaderLibrary/`
3. HDRP shaders use `Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/`
4. Built-in pipeline uses `UnityCG.cginc`

## Mobile Optimization Checklist

When the build target is mobile (Android/iOS):
- Identify variables that can use `half` instead of `float`
- Minimize texture sampling count per fragment
- Replace complex math operations with approximations (e.g., `pow` -> lookup table)
- Convert unnecessary `multi_compile` to `shader_feature` to reduce build size
- Check for unnecessary per-pixel calculations that can move to vertex shader
- Suggest `[NoScaleOffset]` attribute for textures that don't use tiling/offset

## Common Shader Patterns

### URP Lit Shader Structure
```hlsl
Shader "Custom/MyShader"
{
    Properties { ... }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            ...
            ENDHLSL
        }
    }
}
```

### Common Error Patterns
- `undeclared identifier`: Missing include file or typo in variable name
- `cannot implicitly convert`: Type mismatch (float vs half vs int)
- `Shader error in '...'`: Syntax error — check braces and semicolons
- `Too many texture interpolators`: Reduce varying count or use packing
