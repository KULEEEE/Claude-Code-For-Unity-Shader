using System.Collections.Generic;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Data models for the Shader Inspector window.
    /// Parsed from JSON responses returned by ShaderAnalyzer, MaterialInspector, etc.
    /// </summary>

    public class ShaderInfo
    {
        public string name;
        public string path;
        public int passCount;
        public int variantCount;
        public bool isSupported;
    }

    public class ShaderListData
    {
        public List<ShaderInfo> shaders = new List<ShaderInfo>();
        public int totalCount;

        public static ShaderListData Parse(string json)
        {
            var data = new ShaderListData();
            data.totalCount = JsonHelper.GetInt(json, "totalCount");
            var objects = JsonHelper.GetArrayObjects(json, "shaders");
            foreach (var obj in objects)
            {
                data.shaders.Add(new ShaderInfo
                {
                    name = JsonHelper.GetString(obj, "name") ?? "",
                    path = JsonHelper.GetString(obj, "path") ?? "",
                    passCount = JsonHelper.GetInt(obj, "passCount"),
                    variantCount = JsonHelper.GetInt(obj, "variantCount"),
                    isSupported = JsonHelper.GetBool(obj, "isSupported", true)
                });
            }
            return data;
        }
    }

    public class CompileResult
    {
        public bool success;
        public string shaderName;
        public string path;
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
        public int variantCount;
        public int passCount;
        public bool isSupported;

        public static CompileResult Parse(string json)
        {
            var r = new CompileResult();
            r.success = JsonHelper.GetBool(json, "success");
            r.shaderName = JsonHelper.GetString(json, "shaderName") ?? "";
            r.path = JsonHelper.GetString(json, "path") ?? "";
            r.variantCount = JsonHelper.GetInt(json, "variantCount");
            r.passCount = JsonHelper.GetInt(json, "passCount");
            r.isSupported = JsonHelper.GetBool(json, "isSupported");
            r.errors = JsonHelper.GetStringArray(json, "errors");
            r.warnings = JsonHelper.GetStringArray(json, "warnings");
            return r;
        }
    }

    public class VariantInfo
    {
        public string shaderName;
        public string path;
        public int totalVariantCount;
        public int passCount;
        public List<string> globalKeywords = new List<string>();
        public List<string> localKeywords = new List<string>();
        public bool hasProceduralInstancing;

        public static VariantInfo Parse(string json)
        {
            var v = new VariantInfo();
            v.shaderName = JsonHelper.GetString(json, "shaderName") ?? "";
            v.path = JsonHelper.GetString(json, "path") ?? "";
            v.totalVariantCount = JsonHelper.GetInt(json, "totalVariantCount");
            v.passCount = JsonHelper.GetInt(json, "passCount");
            v.globalKeywords = JsonHelper.GetStringArray(json, "globalKeywords");
            v.localKeywords = JsonHelper.GetStringArray(json, "localKeywords");
            v.hasProceduralInstancing = JsonHelper.GetBool(json, "hasProceduralInstancing");
            return v;
        }
    }

    public class ShaderProperty
    {
        public string name;
        public string type;
        public string description;
        public string flags;
    }

    public class ShaderPropertiesData
    {
        public string shaderName;
        public string path;
        public int propertyCount;
        public List<ShaderProperty> properties = new List<ShaderProperty>();

        public static ShaderPropertiesData Parse(string json)
        {
            var d = new ShaderPropertiesData();
            d.shaderName = JsonHelper.GetString(json, "shaderName") ?? "";
            d.path = JsonHelper.GetString(json, "path") ?? "";
            d.propertyCount = JsonHelper.GetInt(json, "propertyCount");
            var objects = JsonHelper.GetArrayObjects(json, "properties");
            foreach (var obj in objects)
            {
                d.properties.Add(new ShaderProperty
                {
                    name = JsonHelper.GetString(obj, "name") ?? "",
                    type = JsonHelper.GetString(obj, "type") ?? "",
                    description = JsonHelper.GetString(obj, "description") ?? "",
                    flags = JsonHelper.GetString(obj, "flags") ?? ""
                });
            }
            return d;
        }
    }

    public class ShaderCodeData
    {
        public string path;
        public string code;
        public int lineCount;

        public static ShaderCodeData Parse(string json)
        {
            var d = new ShaderCodeData();
            d.path = JsonHelper.GetString(json, "path") ?? "";
            d.code = JsonHelper.GetString(json, "code") ?? "";
            d.lineCount = JsonHelper.GetInt(json, "lineCount");
            return d;
        }
    }

    public class MaterialInfo
    {
        public string name;
        public string path;
        public string shaderName;
        public int renderQueue;
        public bool enableInstancing;
    }

    public class MaterialListData
    {
        public List<MaterialInfo> materials = new List<MaterialInfo>();
        public int totalCount;

        public static MaterialListData Parse(string json)
        {
            var data = new MaterialListData();
            data.totalCount = JsonHelper.GetInt(json, "totalCount");
            var objects = JsonHelper.GetArrayObjects(json, "materials");
            foreach (var obj in objects)
            {
                data.materials.Add(new MaterialInfo
                {
                    name = JsonHelper.GetString(obj, "name") ?? "",
                    path = JsonHelper.GetString(obj, "path") ?? "",
                    shaderName = JsonHelper.GetString(obj, "shaderName") ?? "",
                    renderQueue = JsonHelper.GetInt(obj, "renderQueue"),
                    enableInstancing = JsonHelper.GetBool(obj, "enableInstancing")
                });
            }
            return data;
        }
    }

    public class MaterialDetailData
    {
        public string name;
        public string path;
        public string shaderName;
        public int renderQueue;
        public bool enableInstancing;
        public List<string> keywords = new List<string>();
        public List<ShaderProperty> properties = new List<ShaderProperty>();
        public int passCount;

        public static MaterialDetailData Parse(string json)
        {
            var d = new MaterialDetailData();
            d.name = JsonHelper.GetString(json, "name") ?? "";
            d.path = JsonHelper.GetString(json, "path") ?? "";
            d.shaderName = JsonHelper.GetString(json, "shaderName") ?? "";
            d.renderQueue = JsonHelper.GetInt(json, "renderQueue");
            d.enableInstancing = JsonHelper.GetBool(json, "enableInstancing");
            d.passCount = JsonHelper.GetInt(json, "passCount");
            d.keywords = JsonHelper.GetStringArray(json, "keywords");
            var objects = JsonHelper.GetArrayObjects(json, "properties");
            foreach (var obj in objects)
            {
                d.properties.Add(new ShaderProperty
                {
                    name = JsonHelper.GetString(obj, "name") ?? "",
                    type = JsonHelper.GetString(obj, "type") ?? "",
                });
            }
            return d;
        }
    }

    public class PipelineData
    {
        public string pipelineType;
        public string pipelineAsset;
        public string pipelineAssetType;
        public string graphicsDeviceType;
        public string graphicsDeviceVersion;

        public static PipelineData Parse(string json)
        {
            var d = new PipelineData();
            d.pipelineType = JsonHelper.GetString(json, "pipelineType") ?? "Unknown";
            d.pipelineAsset = JsonHelper.GetString(json, "pipelineAsset") ?? "None";
            d.pipelineAssetType = JsonHelper.GetString(json, "pipelineAssetType") ?? "None";
            d.graphicsDeviceType = JsonHelper.GetString(json, "graphicsDeviceType") ?? "Unknown";
            d.graphicsDeviceVersion = JsonHelper.GetString(json, "graphicsDeviceVersion") ?? "";
            return d;
        }
    }

    public class QualitySettingsData
    {
        public int qualityLevel;
        public List<string> qualityNames = new List<string>();
        public int pixelLightCount;
        public string anisotropicFiltering;
        public int antiAliasing;
        public string shadows;
        public string shadowResolution;

        public static QualitySettingsData Parse(string json)
        {
            var d = new QualitySettingsData();
            d.qualityLevel = JsonHelper.GetInt(json, "qualityLevel");
            d.qualityNames = JsonHelper.GetStringArray(json, "qualityNames");
            d.pixelLightCount = JsonHelper.GetInt(json, "pixelLightCount");
            d.anisotropicFiltering = JsonHelper.GetString(json, "anisotropicFiltering") ?? "";
            d.antiAliasing = JsonHelper.GetInt(json, "antiAliasing");
            d.shadows = JsonHelper.GetString(json, "shadows") ?? "";
            d.shadowResolution = JsonHelper.GetString(json, "shadowResolution") ?? "";
            return d;
        }
    }

    public class LogEntry
    {
        public string timestamp;
        public string severity;
        public string message;
        public string stackTrace;
    }

    public class LogsData
    {
        public List<LogEntry> logs = new List<LogEntry>();
        public int totalCount;

        public static LogsData Parse(string json)
        {
            var d = new LogsData();
            d.totalCount = JsonHelper.GetInt(json, "totalCount");
            var objects = JsonHelper.GetArrayObjects(json, "logs");
            foreach (var obj in objects)
            {
                d.logs.Add(new LogEntry
                {
                    timestamp = JsonHelper.GetString(obj, "timestamp") ?? "",
                    severity = JsonHelper.GetString(obj, "severity") ?? "info",
                    message = JsonHelper.GetString(obj, "message") ?? "",
                    stackTrace = JsonHelper.GetString(obj, "stackTrace") ?? ""
                });
            }
            return d;
        }
    }
}
