---
name: shader-logs
description: Get shader-related console logs from Unity
---

Retrieve shader-related console log entries from Unity Editor using `get_shader_logs`.

If the user specified a severity filter (error, warning, or all), use it. Default is "all".

Display the logs grouped by severity:
1. Errors first (if any)
2. Warnings
3. Info messages

For each error, suggest potential fixes if the error message is recognizable.
