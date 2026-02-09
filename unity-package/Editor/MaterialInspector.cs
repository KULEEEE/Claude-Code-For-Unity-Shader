using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Collects material information including shader assignments, property values, and keywords.
    /// </summary>
    public static class MaterialInspector
    {
        #region List All Materials

        public static string ListAllMaterials(string filter = null)
        {
            var guids = AssetDatabase.FindAssets("t:Material");
            var builder = JsonHelper.StartObject()
                .Key("materials").BeginArray();

            int count = 0;
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
                    continue;

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) continue;

                string shaderName = material.shader != null ? material.shader.name : "None";

                if (!string.IsNullOrEmpty(filter) &&
                    !material.name.ToLowerInvariant().Contains(filter.ToLowerInvariant()) &&
                    !shaderName.ToLowerInvariant().Contains(filter.ToLowerInvariant()) &&
                    !path.ToLowerInvariant().Contains(filter.ToLowerInvariant()))
                    continue;

                builder.BeginObject()
                    .Key("name").Value(material.name)
                    .Key("path").Value(path)
                    .Key("shaderName").Value(shaderName)
                    .Key("renderQueue").Value(material.renderQueue)
                    .Key("enableInstancing").Value(material.enableInstancing)
                .EndObject();

                count++;
            }

            builder.EndArray()
                .Key("totalCount").Value(count);

            return builder.ToString();
        }

        #endregion

        #region Get Material Info

        public static string GetMaterialInfo(string materialPath)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                return JsonHelper.StartObject()
                    .Key("error").Value($"Material not found: {materialPath}")
                    .ToString();
            }

            var shader = material.shader;
            var builder = JsonHelper.StartObject()
                .Key("name").Value(material.name)
                .Key("path").Value(materialPath)
                .Key("shaderName").Value(shader != null ? shader.name : "None")
                .Key("renderQueue").Value(material.renderQueue)
                .Key("enableInstancing").Value(material.enableInstancing)
                .Key("doubleSidedGI").Value(material.doubleSidedGI)
                .Key("globalIlluminationFlags").Value(material.globalIlluminationFlags.ToString());

            // Properties
            if (shader != null)
            {
                int propCount = shader.GetPropertyCount();
                builder.Key("properties").BeginArray();

                for (int i = 0; i < propCount; i++)
                {
                    string propName = shader.GetPropertyName(i);
                    var propType = shader.GetPropertyType(i);

                    builder.BeginObject()
                        .Key("name").Value(propName)
                        .Key("type").Value(propType.ToString());

                    try
                    {
                        switch (propType)
                        {
                            case ShaderPropertyType.Float:
                            case ShaderPropertyType.Range:
                                builder.Key("value").Value(material.GetFloat(propName));
                                break;
                            case ShaderPropertyType.Color:
                                var color = material.GetColor(propName);
                                builder.Key("value").Value($"({color.r}, {color.g}, {color.b}, {color.a})");
                                break;
                            case ShaderPropertyType.Vector:
                                var vec = material.GetVector(propName);
                                builder.Key("value").Value($"({vec.x}, {vec.y}, {vec.z}, {vec.w})");
                                break;
                            case ShaderPropertyType.Texture:
                                var tex = material.GetTexture(propName);
                                builder.Key("value").Value(tex != null ? tex.name : "None");
                                if (tex != null)
                                {
                                    builder.Key("textureSize").Value($"{tex.width}x{tex.height}");
                                }
                                var offset = material.GetTextureOffset(propName);
                                var scale = material.GetTextureScale(propName);
                                builder.Key("offset").Value($"({offset.x}, {offset.y})")
                                    .Key("scale").Value($"({scale.x}, {scale.y})");
                                break;
                            case ShaderPropertyType.Int:
                                builder.Key("value").Value(material.GetInt(propName));
                                break;
                        }
                    }
                    catch
                    {
                        builder.Key("value").Value("[Error reading value]");
                    }

                    builder.EndObject();
                }

                builder.EndArray();
            }

            // Keywords
            builder.Key("keywords").BeginArray();
            var keywords = material.shaderKeywords;
            foreach (var kw in keywords)
                builder.Value(kw);
            builder.EndArray();

            // Enabled passes
            builder.Key("passCount").Value(material.passCount);

            return builder.ToString();
        }

        #endregion

        #region Get Material Keywords

        public static string GetMaterialKeywords(string materialPath = null)
        {
            if (!string.IsNullOrEmpty(materialPath))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    return JsonHelper.StartObject()
                        .Key("error").Value($"Material not found: {materialPath}")
                        .ToString();
                }

                var builder = JsonHelper.StartObject()
                    .Key("materialName").Value(material.name)
                    .Key("keywords").BeginArray();

                foreach (var kw in material.shaderKeywords)
                    builder.Value(kw);

                builder.EndArray();
                return builder.ToString();
            }

            // Get all unique keywords across all materials in project
            var allKeywords = new HashSet<string>();
            var guids = AssetDatabase.FindAssets("t:Material");

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/")) continue;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;

                foreach (var kw in mat.shaderKeywords)
                    allKeywords.Add(kw);
            }

            var resultBuilder = JsonHelper.StartObject()
                .Key("keywords").BeginArray();

            foreach (var kw in allKeywords)
                resultBuilder.Value(kw);

            resultBuilder.EndArray()
                .Key("totalCount").Value(allKeywords.Count);

            return resultBuilder.ToString();
        }

        #endregion
    }
}
