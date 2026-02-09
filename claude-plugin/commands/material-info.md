---
name: material-info
description: Inspect a material's shader, properties, and keywords
---

Get detailed information about a Unity material using `get_material_info`.

Ask the user for the material path if not provided as an argument (e.g., `Assets/Materials/Character.mat`).

Display:
1. Material name and path
2. Assigned shader name
3. Active keywords
4. Property values (formatted by type - colors as hex, vectors as (x,y,z,w), textures as asset paths)
5. Render queue and other settings
