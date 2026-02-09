---
name: shader-help
description: Unity shader analysis and optimization helper
---

Analyze the shaders in this Unity project.

1. Use `list_shaders` to get the list of all shaders in the project
2. For each shader, analyze the variant count and warn if excessive (>1000 variants)
3. Use `compile_shader` to check for compilation errors and warnings on any problematic shaders
4. Check `unity://pipeline/info` to understand the current render pipeline
5. Check `unity://editor/platform` to understand the current build target
6. Based on the build target, provide optimization suggestions:
   - Mobile: half precision, reduced texture samples, simplified math
   - Console/PC: suggest quality improvements if performance allows
7. Report a summary of findings and actionable recommendations
