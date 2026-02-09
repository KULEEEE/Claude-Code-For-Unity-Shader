using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Detects the current render pipeline (Built-in, URP, HDRP) and extracts settings.
    /// Uses reflection to avoid hard dependencies on URP/HDRP packages.
    /// </summary>
    public static class PipelineDetector
    {
        public enum PipelineType
        {
            BuiltIn,
            URP,
            HDRP,
            Custom
        }

        public static PipelineType DetectPipeline()
        {
            var rpAsset = GraphicsSettings.currentRenderPipeline;
            if (rpAsset == null)
                return PipelineType.BuiltIn;

            string typeName = rpAsset.GetType().FullName;
            if (typeName.Contains("Universal") || typeName.Contains("URP"))
                return PipelineType.URP;
            if (typeName.Contains("HighDefinition") || typeName.Contains("HDRP") || typeName.Contains("HDRenderPipeline"))
                return PipelineType.HDRP;

            return PipelineType.Custom;
        }

        public static string GetPipelineInfoJson()
        {
            var pipeline = DetectPipeline();
            var rpAsset = GraphicsSettings.currentRenderPipeline;

            var builder = JsonHelper.StartObject()
                .Key("pipelineType").Value(pipeline.ToString())
                .Key("pipelineAsset").Value(rpAsset != null ? rpAsset.name : "None")
                .Key("pipelineAssetType").Value(rpAsset != null ? rpAsset.GetType().FullName : "None");

            // Extract SRP asset properties via reflection (avoids URP/HDRP hard dependency)
            if (rpAsset != null)
            {
                builder.Key("settings").BeginObject();
                TryAddReflectedProperty(builder, rpAsset, "supportsCameraDepthTexture");
                TryAddReflectedProperty(builder, rpAsset, "supportsCameraOpaqueTexture");
                TryAddReflectedProperty(builder, rpAsset, "supportsHDR");
                TryAddReflectedProperty(builder, rpAsset, "msaaSampleCount");
                TryAddReflectedProperty(builder, rpAsset, "renderScale");
                TryAddReflectedProperty(builder, rpAsset, "supportsTerrainHoles");

#if UNITY_6000_0_OR_NEWER
                // Unity 6.0+ may have additional Render Graph info
                TryAddReflectedProperty(builder, rpAsset, "useRenderGraph");
                TryAddReflectedProperty(builder, rpAsset, "enableRenderGraph");
#endif
                builder.EndObject();
            }

            // Graphics API info
            builder.Key("graphicsDeviceType").Value(SystemInfo.graphicsDeviceType.ToString());
            builder.Key("graphicsDeviceVersion").Value(SystemInfo.graphicsDeviceVersion);

            return builder.ToString();
        }

        public static string GetQualitySettingsJson()
        {
            var builder = JsonHelper.StartObject()
                .Key("qualityLevel").Value(QualitySettings.GetQualityLevel())
                .Key("qualityNames").BeginArray();

            string[] names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
                builder.Value(names[i]);
            builder.EndArray();

            builder.Key("pixelLightCount").Value(QualitySettings.pixelLightCount)
                .Key("anisotropicFiltering").Value(QualitySettings.anisotropicFiltering.ToString())
                .Key("antiAliasing").Value(QualitySettings.antiAliasing)
                .Key("shadows").Value(QualitySettings.shadows.ToString())
                .Key("shadowResolution").Value(QualitySettings.shadowResolution.ToString())
                .Key("shadowDistance").Value(QualitySettings.shadowDistance);

            return builder.ToString();
        }

        private static void TryAddReflectedProperty(JsonHelper.JsonBuilder builder, object obj, string propertyName)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null) return;

                object value = prop.GetValue(obj);
                if (value == null) return;

                builder.Key(propertyName);

                if (value is bool boolVal)
                    builder.Value(boolVal);
                else if (value is int intVal)
                    builder.Value(intVal);
                else if (value is float floatVal)
                    builder.Value(floatVal);
                else
                    builder.Value(value.ToString());
            }
            catch
            {
                // Property not available in this Unity/SRP version — skip silently
            }
        }
    }
}
