using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Core shader analysis logic. Uses conditional compilation for Unity 6.0+ APIs
    /// and reflection fallback for older versions.
    /// </summary>
    public static class ShaderAnalyzer
    {
        #region Reflection Cache

        private static MethodInfo _getShaderMessageCount;
        private static MethodInfo _getShaderMessage;
        private static MethodInfo _getVariantCount;
        private static MethodInfo _getShaderGlobalKeywords;
        private static MethodInfo _getShaderLocalKeywords;
        private static MethodInfo _hasProceduralInstancing;
        private static bool _reflectionInitialized;

        private static void InitReflection()
        {
            if (_reflectionInitialized) return;
            _reflectionInitialized = true;

            try
            {
                var shaderUtilType = typeof(ShaderUtil);
                _getShaderMessageCount = shaderUtilType.GetMethod("GetShaderMessageCount",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _getShaderMessage = shaderUtilType.GetMethod("GetShaderMessage",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _getVariantCount = shaderUtilType.GetMethod("GetVariantCount",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _getShaderGlobalKeywords = shaderUtilType.GetMethod("GetShaderGlobalKeywords",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _getShaderLocalKeywords = shaderUtilType.GetMethod("GetShaderLocalKeywords",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _hasProceduralInstancing = shaderUtilType.GetMethod("HasProceduralInstancing",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ShaderMCP] Reflection init failed: {ex.Message}");
            }
        }

        #endregion

        #region List All Shaders

        public static string ListAllShaders(string filter = null)
        {
            InitReflection();

            var guids = AssetDatabase.FindAssets("t:Shader");
            var builder = JsonHelper.StartObject()
                .Key("shaders").BeginArray();

            int count = 0;
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
                    continue;

                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null) continue;

                if (!string.IsNullOrEmpty(filter) &&
                    !shader.name.ToLowerInvariant().Contains(filter.ToLowerInvariant()) &&
                    !path.ToLowerInvariant().Contains(filter.ToLowerInvariant()))
                    continue;

                int passCount = shader.passCount;
                int variantCount = GetVariantCount(shader);

                builder.BeginObject()
                    .Key("name").Value(shader.name)
                    .Key("path").Value(path)
                    .Key("passCount").Value(passCount)
                    .Key("variantCount").Value(variantCount)
                    .Key("isSupported").Value(shader.isSupported)
                .EndObject();

                count++;
            }

            builder.EndArray()
                .Key("totalCount").Value(count);

            return builder.ToString();
        }

        #endregion

        #region Compile Shader

        public static string CompileShader(string shaderPath)
        {
            InitReflection();

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null)
            {
                return JsonHelper.StartObject()
                    .Key("success").Value(false)
                    .Key("error").Value($"Shader not found at path: {shaderPath}")
                    .ToString();
            }

            // Force reimport to trigger compilation
            AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceUpdate);

            var errors = new List<string>();
            var warnings = new List<string>();

            // Get shader messages
            try
            {
                int messageCount = GetShaderMessageCount(shader);
                for (int i = 0; i < messageCount; i++)
                {
                    GetShaderMessageInfo(shader, i, out string message, out int severity);
                    if (severity == 0) // Error
                        errors.Add(message);
                    else
                        warnings.Add(message);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ShaderMCP] Failed to get shader messages: {ex.Message}");
            }

            int variantCount = GetVariantCount(shader);

            var builder = JsonHelper.StartObject()
                .Key("success").Value(errors.Count == 0)
                .Key("shaderName").Value(shader.name)
                .Key("path").Value(shaderPath)
                .Key("errors").BeginArray();

            foreach (var e in errors) builder.Value(e);
            builder.EndArray().Key("warnings").BeginArray();
            foreach (var w in warnings) builder.Value(w);
            builder.EndArray()
                .Key("variantCount").Value(variantCount)
                .Key("passCount").Value(shader.passCount)
                .Key("isSupported").Value(shader.isSupported);

            return builder.ToString();
        }

        #endregion

        #region Variant Info

        public static string GetVariantInfo(string shaderPath)
        {
            InitReflection();

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null)
            {
                return JsonHelper.StartObject()
                    .Key("error").Value($"Shader not found: {shaderPath}")
                    .ToString();
            }

            var builder = JsonHelper.StartObject()
                .Key("shaderName").Value(shader.name)
                .Key("path").Value(shaderPath)
                .Key("totalVariantCount").Value(GetVariantCount(shader))
                .Key("passCount").Value(shader.passCount);

            // Keywords
            builder.Key("globalKeywords").BeginArray();
            var globalKeywords = GetGlobalKeywords(shader);
            if (globalKeywords != null)
            {
                foreach (var kw in globalKeywords)
                    builder.Value(kw);
            }
            builder.EndArray();

            builder.Key("localKeywords").BeginArray();
            var localKeywords = GetLocalKeywords(shader);
            if (localKeywords != null)
            {
                foreach (var kw in localKeywords)
                    builder.Value(kw);
            }
            builder.EndArray();

#if UNITY_6000_0_OR_NEWER
            // Unity 6.0+: Use shader.keywordSpace directly
            try
            {
                var keywordSpace = shader.keywordSpace;
                builder.Key("keywordSpace").BeginArray();
                foreach (var keyword in keywordSpace.keywords)
                {
                    builder.BeginObject()
                        .Key("name").Value(keyword.name)
                        .Key("type").Value(keyword.type.ToString())
                        .Key("isValid").Value(keyword.isValid)
                    .EndObject();
                }
                builder.EndArray();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ShaderMCP] keywordSpace access failed: {ex.Message}");
            }
#endif

            bool hasInstancing = false;
            try
            {
                if (_hasProceduralInstancing != null)
                    hasInstancing = (bool)_hasProceduralInstancing.Invoke(null, new object[] { shader });
            }
            catch { }
            builder.Key("hasProceduralInstancing").Value(hasInstancing);

            return builder.ToString();
        }

        #endregion

        #region Shader Properties

        public static string GetShaderProperties(string shaderPath)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null)
            {
                return JsonHelper.StartObject()
                    .Key("error").Value($"Shader not found: {shaderPath}")
                    .ToString();
            }

            int propCount = shader.GetPropertyCount();

            var builder = JsonHelper.StartObject()
                .Key("shaderName").Value(shader.name)
                .Key("path").Value(shaderPath)
                .Key("propertyCount").Value(propCount)
                .Key("properties").BeginArray();

            for (int i = 0; i < propCount; i++)
            {
                string propName = shader.GetPropertyName(i);
                var propType = shader.GetPropertyType(i);
                string description = shader.GetPropertyDescription(i);

                builder.BeginObject()
                    .Key("name").Value(propName)
                    .Key("type").Value(propType.ToString())
                    .Key("description").Value(description);

                // Get default value based on type
                try
                {
                    switch (propType)
                    {
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            builder.Key("defaultFloat").Value(shader.GetPropertyDefaultFloatValue(i));
                            if (propType == ShaderPropertyType.Range)
                            {
                                var range = shader.GetPropertyRangeLimits(i);
                                builder.Key("rangeMin").Value(range.x)
                                    .Key("rangeMax").Value(range.y);
                            }
                            break;
                        case ShaderPropertyType.Color:
                            var color = shader.GetPropertyDefaultVectorValue(i);
                            builder.Key("defaultColor").Value($"({color.x}, {color.y}, {color.z}, {color.w})");
                            break;
                        case ShaderPropertyType.Vector:
                            var vec = shader.GetPropertyDefaultVectorValue(i);
                            builder.Key("defaultVector").Value($"({vec.x}, {vec.y}, {vec.z}, {vec.w})");
                            break;
                        case ShaderPropertyType.Texture:
                            builder.Key("textureDimension").Value(shader.GetPropertyTextureDimension(i).ToString());
                            builder.Key("textureDefaultName").Value(shader.GetPropertyTextureDefaultName(i));
                            break;
                    }
                }
                catch { }

                // Property attributes
                try
                {
                    var flags = shader.GetPropertyFlags(i);
                    builder.Key("flags").Value(flags.ToString());
                }
                catch { }

                builder.EndObject();
            }

            builder.EndArray();
            return builder.ToString();
        }

        #endregion

        #region Shader Code

        public static string GetShaderCode(string shaderPath, bool resolveIncludes = false)
        {
            if (!File.Exists(shaderPath))
            {
                // Try with project root
                string fullPath = Path.Combine(Application.dataPath, "..", shaderPath);
                if (!File.Exists(fullPath))
                {
                    return JsonHelper.StartObject()
                        .Key("error").Value($"Shader file not found: {shaderPath}")
                        .ToString();
                }
                shaderPath = fullPath;
            }

            string code = File.ReadAllText(shaderPath);

            var builder = JsonHelper.StartObject()
                .Key("path").Value(shaderPath)
                .Key("code").Value(code)
                .Key("lineCount").Value(code.Split('\n').Length);

            if (resolveIncludes)
            {
                var includes = new List<string>();
                ResolveIncludes(code, Path.GetDirectoryName(shaderPath), includes, new HashSet<string>());
                builder.Key("resolvedIncludes").BeginArray();
                foreach (var inc in includes)
                {
                    builder.BeginObject()
                        .Key("path").Value(inc);
                    try
                    {
                        builder.Key("content").Value(File.ReadAllText(inc));
                    }
                    catch
                    {
                        builder.Key("content").Value("[Could not read file]");
                    }
                    builder.EndObject();
                }
                builder.EndArray();
            }

            return builder.ToString();
        }

        private static void ResolveIncludes(string code, string basePath, List<string> includes, HashSet<string> visited)
        {
            string[] lines = code.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("#include")) continue;

                int start = trimmed.IndexOf('"');
                int end = trimmed.LastIndexOf('"');
                if (start < 0 || end <= start) continue;

                string includePath = trimmed.Substring(start + 1, end - start - 1);

                // Try resolving relative to current file
                string fullPath = Path.GetFullPath(Path.Combine(basePath, includePath));
                if (!File.Exists(fullPath))
                {
                    // Try Unity include paths
                    string dataPath = Application.dataPath;
                    fullPath = Path.GetFullPath(Path.Combine(dataPath, includePath));
                    if (!File.Exists(fullPath))
                    {
                        fullPath = Path.GetFullPath(Path.Combine(dataPath, "..", "Packages", includePath));
                    }
                }

                if (File.Exists(fullPath) && !visited.Contains(fullPath))
                {
                    visited.Add(fullPath);
                    includes.Add(fullPath);

                    // Recursively resolve
                    try
                    {
                        string includeCode = File.ReadAllText(fullPath);
                        ResolveIncludes(includeCode, Path.GetDirectoryName(fullPath), includes, visited);
                    }
                    catch { }
                }
            }
        }

        #endregion

        #region Include Files

        public static string GetIncludeFiles()
        {
            var builder = JsonHelper.StartObject()
                .Key("includes").BeginArray();

            string[] extensions = { "*.cginc", "*.hlsl", "*.glslinc", "*.compute" };
            int count = 0;

            foreach (var ext in extensions)
            {
                var guids = AssetDatabase.FindAssets("");
                var files = Directory.GetFiles(Application.dataPath, ext, SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    string relativePath = "Assets" + file.Substring(Application.dataPath.Length).Replace('\\', '/');
                    builder.BeginObject()
                        .Key("path").Value(relativePath)
                        .Key("fileName").Value(Path.GetFileName(file))
                        .Key("extension").Value(Path.GetExtension(file));

                    try
                    {
                        string content = File.ReadAllText(file);
                        builder.Key("lineCount").Value(content.Split('\n').Length)
                            .Key("content").Value(content);
                    }
                    catch
                    {
                        builder.Key("lineCount").Value(0)
                            .Key("content").Value("[Could not read]");
                    }

                    builder.EndObject();
                    count++;
                }
            }

            builder.EndArray()
                .Key("totalCount").Value(count);

            return builder.ToString();
        }

        #endregion

        #region Internal Helpers

        private static int GetShaderMessageCount(Shader shader)
        {
            InitReflection();
            try
            {
                if (_getShaderMessageCount != null)
                    return (int)_getShaderMessageCount.Invoke(null, new object[] { shader });
            }
            catch { }

            // Fallback: try public API (Unity 2021.2+)
            try
            {
                var messages = ShaderUtil.GetShaderMessages(shader);
                return messages.Length;
            }
            catch { }

            return 0;
        }

        private static void GetShaderMessageInfo(Shader shader, int index, out string message, out int severity)
        {
            message = "";
            severity = 1; // default to warning

            // Try public API first (Unity 2021.2+)
            try
            {
                var messages = ShaderUtil.GetShaderMessages(shader);
                if (index < messages.Length)
                {
                    message = messages[index].message;
                    severity = messages[index].severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error ? 0 : 1;
                    return;
                }
            }
            catch { }

            // Reflection fallback
            try
            {
                if (_getShaderMessage != null)
                {
                    var result = _getShaderMessage.Invoke(null, new object[] { shader, index });
                    if (result != null)
                    {
                        message = result.ToString();
                    }
                }
            }
            catch { }
        }

        private static int GetVariantCount(Shader shader)
        {
            InitReflection();
            try
            {
                if (_getVariantCount != null)
                {
                    // GetVariantCount(shader, usedBySceneOnly)
                    var result = _getVariantCount.Invoke(null, new object[] { shader, false });
                    if (result is ulong ulongVal)
                        return (int)Math.Min(ulongVal, int.MaxValue);
                    if (result is int intVal)
                        return intVal;
                    return Convert.ToInt32(result);
                }
            }
            catch { }
            return -1; // Unknown
        }

        private static string[] GetGlobalKeywords(Shader shader)
        {
#if UNITY_6000_0_OR_NEWER
            try
            {
                var keywordSpace = shader.keywordSpace;
                var keywords = new List<string>();
                foreach (var kw in keywordSpace.keywords)
                {
                    if (kw.type == ShaderKeywordType.UserDefined)
                        keywords.Add(kw.name);
                }
                return keywords.ToArray();
            }
            catch { }
#endif
            // Reflection fallback
            InitReflection();
            try
            {
                if (_getShaderGlobalKeywords != null)
                    return (string[])_getShaderGlobalKeywords.Invoke(null, new object[] { shader });
            }
            catch { }
            return Array.Empty<string>();
        }

        private static string[] GetLocalKeywords(Shader shader)
        {
#if UNITY_6000_0_OR_NEWER
            try
            {
                var keywordSpace = shader.keywordSpace;
                var keywords = new List<string>();
                foreach (var kw in keywordSpace.keywords)
                {
                    if (kw.type != ShaderKeywordType.UserDefined)
                        keywords.Add(kw.name);
                }
                return keywords.ToArray();
            }
            catch { }
#endif
            InitReflection();
            try
            {
                if (_getShaderLocalKeywords != null)
                    return (string[])_getShaderLocalKeywords.Invoke(null, new object[] { shader });
            }
            catch { }
            return Array.Empty<string>();
        }

        #endregion
    }
}
