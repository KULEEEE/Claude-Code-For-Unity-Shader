using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ShaderIncludeGraph.Editor
{
    /// <summary>
    /// Analyzes shader files and builds #include dependency trees.
    /// No external dependencies - works standalone in any Unity project.
    /// </summary>
    public static class ShaderIncludeAnalyzer
    {
        /// <summary>
        /// Analyze a shader/include file and extract detailed information.
        /// </summary>
        public static ShaderFileInfo AnalyzeFile(string assetPath)
        {
            var info = new ShaderFileInfo();
            info.assetPath = assetPath;

            string fullPath = assetPath;
            if (!File.Exists(fullPath))
            {
                fullPath = Path.Combine(Application.dataPath, "..", assetPath);
                if (!File.Exists(fullPath))
                {
                    info.error = "File not found";
                    return info;
                }
            }

            string code;
            try
            {
                code = File.ReadAllText(fullPath);
            }
            catch (Exception ex)
            {
                info.error = ex.Message;
                return info;
            }

            info.fileName = Path.GetFileName(fullPath);
            info.extension = Path.GetExtension(fullPath).ToLowerInvariant();
            info.lineCount = code.Split('\n').Length;
            info.fileSize = new FileInfo(fullPath).Length;

            string[] lines = code.Split('\n');

            // Shader name (root .shader files)
            if (info.extension == ".shader")
            {
                var m = Regex.Match(code, @"Shader\s+""([^""]+)""");
                if (m.Success) info.shaderName = m.Groups[1].Value;
            }

            int subShaderCount = 0;
            int passCount = 0;
            bool inProperties = false;
            int braceDepth = 0;
            bool propertiesFound = false;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // --- Properties block (for .shader) ---
                if (info.extension == ".shader" && !propertiesFound && line.StartsWith("Properties"))
                {
                    inProperties = true;
                    braceDepth = 0;
                }
                if (inProperties)
                {
                    foreach (char c in line)
                    {
                        if (c == '{') braceDepth++;
                        else if (c == '}') braceDepth--;
                    }
                    // Parse property lines like: _MainTex ("Texture", 2D) = "white" {}
                    var propMatch = Regex.Match(line, @"^(\w+)\s*\(\s*""([^""]*)""\s*,\s*(\w+)");
                    if (propMatch.Success)
                    {
                        info.properties.Add(new ShaderPropertyEntry
                        {
                            name = propMatch.Groups[1].Value,
                            displayName = propMatch.Groups[2].Value,
                            type = propMatch.Groups[3].Value
                        });
                    }
                    if (braceDepth <= 0 && line.Contains("}"))
                    {
                        inProperties = false;
                        propertiesFound = true;
                    }
                    continue;
                }

                // --- SubShader / Pass count ---
                if (Regex.IsMatch(line, @"^SubShader\s*\{?"))
                    subShaderCount++;
                if (Regex.IsMatch(line, @"^Pass\s*\{?"))
                    passCount++;

                // --- Tags ---
                var tagMatch = Regex.Match(line, @"""RenderType""\s*=\s*""([^""]+)""");
                if (tagMatch.Success && !info.tags.ContainsKey("RenderType"))
                    info.tags["RenderType"] = tagMatch.Groups[1].Value;
                var queueMatch = Regex.Match(line, @"""Queue""\s*=\s*""([^""]+)""");
                if (queueMatch.Success && !info.tags.ContainsKey("Queue"))
                    info.tags["Queue"] = queueMatch.Groups[1].Value;
                var lightMatch = Regex.Match(line, @"""LightMode""\s*=\s*""([^""]+)""");
                if (lightMatch.Success && !info.lightModes.Contains(lightMatch.Groups[1].Value))
                    info.lightModes.Add(lightMatch.Groups[1].Value);

                // --- #pragma ---
                if (line.StartsWith("#pragma"))
                {
                    string pragmaBody = line.Substring(7).Trim();

                    if (pragmaBody.StartsWith("target"))
                        info.target = pragmaBody.Substring(6).Trim();
                    else if (pragmaBody.StartsWith("vertex"))
                        info.entryPoints["vertex"] = pragmaBody.Substring(6).Trim();
                    else if (pragmaBody.StartsWith("fragment"))
                        info.entryPoints["fragment"] = pragmaBody.Substring(8).Trim();
                    else if (pragmaBody.StartsWith("geometry"))
                        info.entryPoints["geometry"] = pragmaBody.Substring(8).Trim();
                    else if (pragmaBody.StartsWith("hull"))
                        info.entryPoints["hull"] = pragmaBody.Substring(4).Trim();
                    else if (pragmaBody.StartsWith("domain"))
                        info.entryPoints["domain"] = pragmaBody.Substring(6).Trim();
                    else if (pragmaBody.StartsWith("kernel"))
                        info.entryPoints["kernel"] = pragmaBody.Substring(6).Trim();
                    else if (pragmaBody.StartsWith("surface"))
                        info.pragmas.Add(pragmaBody);
                    else if (pragmaBody.StartsWith("multi_compile") || pragmaBody.StartsWith("shader_feature"))
                    {
                        // Extract keyword variants
                        string[] parts = pragmaBody.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        string keyword = parts[0]; // multi_compile or shader_feature (with possible _local suffix)
                        var variants = new List<string>();
                        for (int i = 1; i < parts.Length; i++)
                            variants.Add(parts[i]);
                        info.keywords.Add(new ShaderKeywordEntry { directive = keyword, variants = variants });
                    }
                    else
                    {
                        info.pragmas.Add(pragmaBody);
                    }
                }

                // --- #define ---
                if (line.StartsWith("#define"))
                {
                    string defineBody = line.Substring(7).Trim();
                    int spaceIdx = defineBody.IndexOfAny(new[] { ' ', '\t', '(' });
                    string macroName = spaceIdx > 0 ? defineBody.Substring(0, spaceIdx) : defineBody;
                    if (!string.IsNullOrEmpty(macroName))
                        info.defines.Add(macroName);
                }

                // --- Functions ---
                var funcMatch = Regex.Match(line,
                    @"^(void|float[234]?|half[234]?|fixed[234]?|int[234]?|uint[234]?|bool|real[234]?|float[234]x[234]|half[234]x[234])\s+(\w+)\s*\(");
                if (funcMatch.Success)
                {
                    string funcName = funcMatch.Groups[2].Value;
                    string returnType = funcMatch.Groups[1].Value;
                    // Skip common macros that look like functions
                    if (funcName != "if" && funcName != "for" && funcName != "while")
                        info.functions.Add(new ShaderFunctionEntry { name = funcName, returnType = returnType });
                }

                // --- Structs ---
                var structMatch = Regex.Match(line, @"^struct\s+(\w+)");
                if (structMatch.Success)
                    info.structs.Add(structMatch.Groups[1].Value);

                // --- CBUFFER ---
                var cbufMatch = Regex.Match(line, @"^CBUFFER_START\s*\(\s*(\w+)\s*\)");
                if (cbufMatch.Success)
                    info.cbuffers.Add(cbufMatch.Groups[1].Value);

                // --- Program blocks ---
                if (line == "CGPROGRAM" || line == "HLSLPROGRAM" || line == "CGINCLUDE" || line == "HLSLINCLUDE")
                {
                    if (!info.programBlocks.Contains(line))
                        info.programBlocks.Add(line);
                }
            }

            info.subShaderCount = subShaderCount;
            info.passCount = passCount;

            // Code preview (first 30 lines)
            int previewLines = Mathf.Min(30, lines.Length);
            var preview = new List<string>();
            for (int i = 0; i < previewLines; i++)
                preview.Add(lines[i]);
            info.codePreview = string.Join("\n", preview);
            if (lines.Length > previewLines)
                info.codePreview += "\n// ... (" + (lines.Length - previewLines) + " more lines)";

            return info;
        }

        /// <summary>
        /// Build the include tree for a shader file.
        /// Returns null if the file cannot be found.
        /// </summary>
        public static IncludeTreeNode BuildIncludeTree(string shaderPath)
        {
            string filePath = shaderPath;
            if (!File.Exists(filePath))
            {
                filePath = Path.Combine(Application.dataPath, "..", shaderPath);
                if (!File.Exists(filePath))
                    return null;
            }

            string code = File.ReadAllText(filePath);
            int lineCount = code.Split('\n').Length;
            string fileName = Path.GetFileName(filePath);

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            visited.Add(Path.GetFullPath(filePath));

            var root = new IncludeTreeNode
            {
                name = fileName,
                path = shaderPath,
                lineCount = lineCount
            };

            BuildChildren(root, code, Path.GetDirectoryName(Path.GetFullPath(filePath)), visited);
            return root;
        }

        private static void BuildChildren(IncludeTreeNode parent, string code, string basePath, HashSet<string> visited)
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

                // Resolve file path
                string fullPath = Path.GetFullPath(Path.Combine(basePath, includePath));
                if (!File.Exists(fullPath))
                {
                    string dataPath = Application.dataPath;
                    fullPath = Path.GetFullPath(Path.Combine(dataPath, includePath));
                    if (!File.Exists(fullPath))
                    {
                        fullPath = Path.GetFullPath(Path.Combine(dataPath, "..", "Packages", includePath));
                    }
                }

                if (!File.Exists(fullPath)) continue;

                string normalizedPath = Path.GetFullPath(fullPath);
                if (visited.Contains(normalizedPath)) continue;
                visited.Add(normalizedPath);

                string childCode;
                int childLineCount;
                try
                {
                    childCode = File.ReadAllText(fullPath);
                    childLineCount = childCode.Split('\n').Length;
                }
                catch
                {
                    continue;
                }

                // Convert to relative path
                string relativePath = fullPath;
                string assetsRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                if (relativePath.StartsWith(assetsRoot))
                {
                    relativePath = relativePath.Substring(assetsRoot.Length + 1).Replace('\\', '/');
                }

                var childNode = new IncludeTreeNode
                {
                    name = Path.GetFileName(fullPath),
                    path = relativePath,
                    lineCount = childLineCount
                };

                parent.children.Add(childNode);
                BuildChildren(childNode, childCode, Path.GetDirectoryName(fullPath), visited);
            }
        }
    }

    /// <summary>
    /// Represents a single node in the shader include hierarchy.
    /// </summary>
    public class IncludeTreeNode
    {
        public string name;
        public string path;
        public int lineCount;
        public List<IncludeTreeNode> children = new List<IncludeTreeNode>();
    }

    /// <summary>
    /// Detailed analysis result for a shader/include file.
    /// </summary>
    public class ShaderFileInfo
    {
        public string assetPath;
        public string fileName;
        public string extension;
        public int lineCount;
        public long fileSize;
        public string error;

        // .shader specific
        public string shaderName;
        public int subShaderCount;
        public int passCount;
        public List<ShaderPropertyEntry> properties = new List<ShaderPropertyEntry>();
        public Dictionary<string, string> tags = new Dictionary<string, string>();
        public List<string> lightModes = new List<string>();

        // Pragmas & keywords
        public string target;
        public Dictionary<string, string> entryPoints = new Dictionary<string, string>();
        public List<string> pragmas = new List<string>();
        public List<ShaderKeywordEntry> keywords = new List<ShaderKeywordEntry>();
        public List<string> programBlocks = new List<string>();

        // Code structure
        public List<string> defines = new List<string>();
        public List<ShaderFunctionEntry> functions = new List<ShaderFunctionEntry>();
        public List<string> structs = new List<string>();
        public List<string> cbuffers = new List<string>();

        // Preview
        public string codePreview;

        public string FileTypeLabel
        {
            get
            {
                switch (extension)
                {
                    case ".shader": return "Shader";
                    case ".cginc": return "CG Include";
                    case ".hlsl": return "HLSL Include";
                    case ".glslinc": return "GLSL Include";
                    case ".compute": return "Compute Shader";
                    default: return "Unknown";
                }
            }
        }

        public string FileSizeLabel
        {
            get
            {
                if (fileSize < 1024) return $"{fileSize} B";
                if (fileSize < 1024 * 1024) return $"{fileSize / 1024f:F1} KB";
                return $"{fileSize / (1024f * 1024f):F1} MB";
            }
        }
    }

    public class ShaderPropertyEntry
    {
        public string name;
        public string displayName;
        public string type;
    }

    public class ShaderKeywordEntry
    {
        public string directive;
        public List<string> variants = new List<string>();
    }

    public class ShaderFunctionEntry
    {
        public string name;
        public string returnType;
    }
}
