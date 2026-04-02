using System;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Renders unified diff output in IMGUI with color-coded lines.
    /// Added lines are green, removed lines are red, headers are cyan.
    /// </summary>
    public static class DiffRenderer
    {
        private const int MaxDisplayLines = 500;

        /// <summary>
        /// Render a unified diff string in the current IMGUI layout.
        /// </summary>
        public static void Render(string unifiedDiff)
        {
            if (string.IsNullOrEmpty(unifiedDiff))
            {
                EditorGUILayout.LabelField("No diff available", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var lines = unifiedDiff.Split(new[] { '\n' }, StringSplitOptions.None);
            bool truncated = lines.Length > MaxDisplayLines;
            int displayCount = truncated ? MaxDisplayLines : lines.Length;

            // Header with copy button
            EditorGUILayout.BeginHorizontal();
            var oldColor = GUI.color;
            GUI.color = SVNToolStyles.DimText;
            EditorGUILayout.LabelField($"Diff ({lines.Length} lines)", EditorStyles.miniLabel);
            GUI.color = oldColor;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Copy Diff", EditorStyles.miniButton, GUILayout.Width(70)))
            {
                EditorGUIUtility.systemCopyBuffer = unifiedDiff;
            }
            EditorGUILayout.EndHorizontal();

            // Diff content
            EditorGUILayout.BeginVertical(SVNToolStyles.DiffContainer);

            for (int i = 0; i < displayCount; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    EditorGUILayout.Space(2);
                    continue;
                }

                DrawDiffLine(line);
            }

            if (truncated)
            {
                EditorGUILayout.Space(4);
                oldColor = GUI.color;
                GUI.color = SVNToolStyles.WarningColor;
                EditorGUILayout.LabelField(
                    $"... {lines.Length - MaxDisplayLines} more lines (use Copy Diff to see full)",
                    EditorStyles.centeredGreyMiniLabel);
                GUI.color = oldColor;
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawDiffLine(string line)
        {
            Color bgColor = Color.clear;
            Color textColor = new Color(0.85f, 0.85f, 0.85f);

            if (line.StartsWith("@@"))
            {
                bgColor = SVNToolStyles.DiffHeaderBg;
                textColor = SVNToolStyles.DiffHeaderColor;
            }
            else if (line.StartsWith("+++") || line.StartsWith("---"))
            {
                textColor = SVNToolStyles.DiffHeaderColor;
            }
            else if (line.StartsWith("+"))
            {
                bgColor = SVNToolStyles.DiffAddBg;
                textColor = SVNToolStyles.DiffAddColor;
            }
            else if (line.StartsWith("-"))
            {
                bgColor = SVNToolStyles.DiffRemoveBg;
                textColor = SVNToolStyles.DiffRemoveColor;
            }
            else
            {
                textColor = SVNToolStyles.DiffContextColor;
            }

            // Draw background rect
            var rect = EditorGUILayout.GetControlRect(false, 16);
            if (bgColor != Color.clear)
            {
                EditorGUI.DrawRect(rect, bgColor);
            }

            // Draw text
            var oldColor = GUI.color;
            GUI.color = textColor;
            GUI.Label(rect, line, SVNToolStyles.DiffLineText);
            GUI.color = oldColor;
        }
    }
}
