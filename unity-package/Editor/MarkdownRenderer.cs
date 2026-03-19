using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Renders Markdown text in Unity IMGUI using rich text tags.
    /// Two-phase approach: Parse into segments (RichText / CodeBlock), then render each.
    /// </summary>
    public static class MarkdownRenderer
    {
        private enum SegmentType { RichText, CodeBlock }

        private struct Segment
        {
            public SegmentType type;
            public string text;
            public string language; // for code blocks
        }

        /// <summary>
        /// Render markdown content in the current IMGUI layout.
        /// </summary>
        public static void Render(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return;

            var segments = Parse(markdown);
            foreach (var seg in segments)
            {
                if (seg.type == SegmentType.CodeBlock)
                    RenderCodeBlock(seg.text, seg.language);
                else
                    RenderRichText(seg.text);
            }
        }

        #region Parsing

        private static List<Segment> Parse(string markdown)
        {
            var segments = new List<Segment>();
            var lines = markdown.Split('\n');
            var richTextLines = new StringBuilder();
            bool inCodeBlock = false;
            string codeLanguage = "";
            var codeLines = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // Check for code block fence
                if (line.TrimStart().StartsWith("```"))
                {
                    if (!inCodeBlock)
                    {
                        // Flush accumulated rich text
                        if (richTextLines.Length > 0)
                        {
                            segments.Add(new Segment
                            {
                                type = SegmentType.RichText,
                                text = richTextLines.ToString()
                            });
                            richTextLines.Clear();
                        }

                        inCodeBlock = true;
                        codeLanguage = line.TrimStart().Substring(3).Trim();
                        codeLines.Clear();
                    }
                    else
                    {
                        // End code block
                        segments.Add(new Segment
                        {
                            type = SegmentType.CodeBlock,
                            text = codeLines.ToString(),
                            language = codeLanguage
                        });
                        inCodeBlock = false;
                        codeLanguage = "";
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    if (codeLines.Length > 0)
                        codeLines.Append('\n');
                    codeLines.Append(line);
                }
                else
                {
                    if (richTextLines.Length > 0)
                        richTextLines.Append('\n');
                    richTextLines.Append(line);
                }
            }

            // Flush remaining
            if (inCodeBlock && codeLines.Length > 0)
            {
                // Unclosed code block - render as code anyway
                segments.Add(new Segment
                {
                    type = SegmentType.CodeBlock,
                    text = codeLines.ToString(),
                    language = codeLanguage
                });
            }
            else if (richTextLines.Length > 0)
            {
                segments.Add(new Segment
                {
                    type = SegmentType.RichText,
                    text = richTextLines.ToString()
                });
            }

            return segments;
        }

        #endregion

        #region Rich Text Rendering

        private static void RenderRichText(string text)
        {
            var lines = text.Split('\n');
            var sb = new StringBuilder();

            foreach (var rawLine in lines)
            {
                string line = rawLine;

                // Horizontal rule
                if (Regex.IsMatch(line.Trim(), @"^-{3,}$") || Regex.IsMatch(line.Trim(), @"^\*{3,}$"))
                {
                    // Flush previous content
                    if (sb.Length > 0)
                    {
                        DrawRichLabel(sb.ToString());
                        sb.Clear();
                    }
                    EditorGUILayout.LabelField("────────────────────────────────────",
                        ShaderInspectorStyles.MarkdownText);
                    continue;
                }

                // Headers
                var headerMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                if (headerMatch.Success)
                {
                    // Flush
                    if (sb.Length > 0)
                    {
                        DrawRichLabel(sb.ToString());
                        sb.Clear();
                    }

                    int level = headerMatch.Groups[1].Value.Length;
                    string headerText = ProcessInlineMarkdown(EscapeRichText(headerMatch.Groups[2].Value));
                    int fontSize = 22 - (level * 2); // h1=20, h2=18, h3=16, h4=14, h5=12, h6=10
                    if (fontSize < 10) fontSize = 10;

                    DrawRichLabel($"<size={fontSize}><b>{headerText}</b></size>");
                    continue;
                }

                // Blockquote
                if (line.TrimStart().StartsWith("> "))
                {
                    if (sb.Length > 0)
                    {
                        DrawRichLabel(sb.ToString());
                        sb.Clear();
                    }

                    string quoteText = ProcessInlineMarkdown(EscapeRichText(line.TrimStart().Substring(2)));
                    DrawRichLabel($"<color=#888888>\u2502 {quoteText}</color>");
                    continue;
                }

                // Unordered list
                var ulMatch = Regex.Match(line, @"^(\s*)[-*+]\s+(.+)$");
                if (ulMatch.Success)
                {
                    string indent = ulMatch.Groups[1].Value;
                    string content = ProcessInlineMarkdown(EscapeRichText(ulMatch.Groups[2].Value));
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append($"{indent}\u2022 {content}");
                    continue;
                }

                // Ordered list
                var olMatch = Regex.Match(line, @"^(\s*)(\d+)\.\s+(.+)$");
                if (olMatch.Success)
                {
                    string indent = olMatch.Groups[1].Value;
                    string number = olMatch.Groups[2].Value;
                    string content = ProcessInlineMarkdown(EscapeRichText(olMatch.Groups[3].Value));
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append($"{indent}{number}. {content}");
                    continue;
                }

                // Regular line
                string processed = ProcessInlineMarkdown(EscapeRichText(line));
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(processed);
            }

            // Flush remaining
            if (sb.Length > 0)
            {
                DrawRichLabel(sb.ToString());
            }
        }

        private static void DrawRichLabel(string richText)
        {
            var style = ShaderInspectorStyles.MarkdownText;
            var content = new GUIContent(richText);
            float height = style.CalcHeight(content, EditorGUIUtility.currentViewWidth - 120);
            EditorGUILayout.SelectableLabel(richText, style, GUILayout.Height(height + 2));
        }

        #endregion

        #region Code Block Rendering

        private static void RenderCodeBlock(string code, string language)
        {
            // Outer container with dark background
            EditorGUILayout.BeginVertical(ShaderInspectorStyles.CodeBlockContainer);

            // Header: language label + copy button
            EditorGUILayout.BeginHorizontal();
            if (!string.IsNullOrEmpty(language))
            {
                var oldColor = GUI.color;
                GUI.color = ShaderInspectorStyles.DimText;
                EditorGUILayout.LabelField(language, EditorStyles.miniLabel, GUILayout.Width(100));
                GUI.color = oldColor;
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Copy Code", EditorStyles.miniButton, GUILayout.Width(70)))
            {
                EditorGUIUtility.systemCopyBuffer = code;
            }
            EditorGUILayout.EndHorizontal();

            // Code content - selectable for copy
            var style = ShaderInspectorStyles.CodeBlockText;
            var content = new GUIContent(code);
            float width = EditorGUIUtility.currentViewWidth - 140;
            if (width < 100) width = 100;
            float height = style.CalcHeight(content, width);
            EditorGUILayout.SelectableLabel(code, style, GUILayout.Height(height + 4));

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        #endregion

        #region Inline Markdown Processing

        /// <summary>
        /// Escape angle brackets to prevent IMGUI rich text parser conflicts.
        /// Must be called BEFORE adding rich text tags.
        /// </summary>
        private static string EscapeRichText(string text)
        {
            // Replace < and > with unicode equivalents to prevent IMGUI parsing issues
            // But preserve our own rich text tags that we'll add later
            text = text.Replace("<", "\uFF1C");
            text = text.Replace(">", "\uFF1E");
            return text;
        }

        /// <summary>
        /// Process inline markdown: bold, italic, inline code, strikethrough, links.
        /// Called AFTER EscapeRichText so we can safely insert rich text tags.
        /// </summary>
        private static string ProcessInlineMarkdown(string text)
        {
            // Inline code: `code` → orange bold
            text = Regex.Replace(text, @"`([^`]+)`",
                "<color=#E8912D><b>$1</b></color>");

            // Bold + Italic: ***text*** or ___text___
            text = Regex.Replace(text, @"\*{3}(.+?)\*{3}",
                "<b><i>$1</i></b>");
            text = Regex.Replace(text, @"_{3}(.+?)_{3}",
                "<b><i>$1</i></b>");

            // Bold: **text** or __text__
            text = Regex.Replace(text, @"\*{2}(.+?)\*{2}",
                "<b>$1</b>");
            text = Regex.Replace(text, @"_{2}(.+?)_{2}",
                "<b>$1</b>");

            // Italic: *text* or _text_
            text = Regex.Replace(text, @"(?<!\w)\*([^*]+?)\*(?!\w)",
                "<i>$1</i>");
            text = Regex.Replace(text, @"(?<!\w)_([^_]+?)_(?!\w)",
                "<i>$1</i>");

            // Strikethrough: ~~text~~ → dimmed
            text = Regex.Replace(text, @"~~(.+?)~~",
                "<color=#888888>$1</color>");

            // Links: [text](url) → colored text
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)",
                "<color=#4EC9B0>$1</color>");

            return text;
        }

        #endregion
    }
}
