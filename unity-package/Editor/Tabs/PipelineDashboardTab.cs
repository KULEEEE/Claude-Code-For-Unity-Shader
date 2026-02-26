using System;
using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Pipeline tab: read-only dashboard showing render pipeline and quality settings.
    /// </summary>
    public class PipelineDashboardTab
    {
        private PipelineData _pipelineData;
        private QualitySettingsData _qualityData;
        private Vector2 _scrollPos;
        private bool _loaded;

        public PipelineDashboardTab()
        {
            Refresh();
        }

        public void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            if (!_loaded)
            {
                EditorGUILayout.LabelField("Loading pipeline info...", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.EndScrollView();
                return;
            }

            // Render Pipeline section
            EditorGUILayout.LabelField("Render Pipeline", ShaderInspectorStyles.HeaderLabel);
            EditorGUILayout.Space(4);

            DrawInfoRow("Pipeline Type", _pipelineData.pipelineType);
            DrawInfoRow("Pipeline Asset", _pipelineData.pipelineAsset);
            DrawInfoRow("Asset Type", _pipelineData.pipelineAssetType);
            DrawInfoRow("Graphics API", _pipelineData.graphicsDeviceType);
            DrawInfoRow("Graphics Version", _pipelineData.graphicsDeviceVersion);

            EditorGUILayout.Space(12);

            // Quality Settings section
            EditorGUILayout.LabelField("Quality Settings", ShaderInspectorStyles.HeaderLabel);
            EditorGUILayout.Space(4);

            if (_qualityData != null)
            {
                // Quality level names
                EditorGUILayout.LabelField("Quality Levels:", ShaderInspectorStyles.SectionHeader);
                for (int i = 0; i < _qualityData.qualityNames.Count; i++)
                {
                    string prefix = i == _qualityData.qualityLevel ? "  >> " : "     ";
                    var style = i == _qualityData.qualityLevel ? EditorStyles.boldLabel : EditorStyles.label;
                    EditorGUILayout.LabelField(prefix + _qualityData.qualityNames[i], style);
                }

                EditorGUILayout.Space(4);

                DrawInfoRow("Pixel Light Count", _qualityData.pixelLightCount.ToString());
                DrawInfoRow("Anisotropic Filtering", _qualityData.anisotropicFiltering);
                DrawInfoRow("Anti-Aliasing", _qualityData.antiAliasing.ToString());
                DrawInfoRow("Shadows", _qualityData.shadows);
                DrawInfoRow("Shadow Resolution", _qualityData.shadowResolution);
            }

            EditorGUILayout.Space(12);

            // Platform info
            EditorGUILayout.LabelField("Platform", ShaderInspectorStyles.HeaderLabel);
            EditorGUILayout.Space(4);

            DrawInfoRow("Build Target", EditorUserBuildSettings.activeBuildTarget.ToString());
            DrawInfoRow("Unity Version", Application.unityVersion);
            DrawInfoRow("OS", SystemInfo.operatingSystem);
            DrawInfoRow("Graphics Device", SystemInfo.graphicsDeviceName);

            EditorGUILayout.Space(8);

            if (GUILayout.Button("Refresh", GUILayout.Width(80), GUILayout.Height(24)))
            {
                Refresh();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawInfoRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label + ":", EditorStyles.boldLabel, GUILayout.Width(180));
            EditorGUILayout.LabelField(value ?? "N/A");
            EditorGUILayout.EndHorizontal();
        }

        public void Refresh()
        {
            try
            {
                string pipeJson = PipelineDetector.GetPipelineInfoJson();
                _pipelineData = PipelineData.Parse(pipeJson);

                string qualJson = PipelineDetector.GetQualitySettingsJson();
                _qualityData = QualitySettingsData.Parse(qualJson);

                _loaded = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ShaderInspector] Failed to load pipeline info: {ex.Message}");
                _loaded = false;
            }
        }
    }
}
