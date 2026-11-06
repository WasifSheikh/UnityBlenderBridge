using System;
using UnityEditor;
using UnityEngine;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>Look of the Blender Bridge window (built once, works in light and dark skins).</summary>
    internal static class BBStyles
    {
        public static readonly Color Grey = new Color(0.5f, 0.5f, 0.5f), Green = new Color(0.30f, 0.69f, 0.31f), Blue = new Color(0.25f, 0.55f, 0.95f),
                                     Orange = new Color(0.96f, 0.62f, 0.13f), Red = new Color(0.90f, 0.30f, 0.25f),
                                     Accent = new Color(0.45f, 0.75f, 1.0f);
        public static Color ConflictBg => EditorGUIUtility.isProSkin ? new Color(1f, 0.6f, 0.1f, 0.10f) : new Color(1f, 0.6f, 0.1f, 0.18f);
        public static Color WarningBg => EditorGUIUtility.isProSkin ? new Color(0.3f, 0.6f, 1f, 0.08f) : new Color(0.3f, 0.6f, 1f, 0.14f);

        public static GUIStyle HeaderBox, Title, Section, Hint, Card, Row, RowText, RowLink, GroupFoldout, BadgeText;
        static bool ready;

        public static void Init()
        {
            if (ready && Title != null) return;
            ready = true;
            HeaderBox = new GUIStyle(EditorStyles.toolbar) { fixedHeight = 0, padding = new RectOffset(8, 8, 6, 6) };
            Title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
            Section = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12, margin = new RectOffset(0, 0, 0, 4) };
            Hint = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { richText = true };
            Card = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 8, 10), margin = new RectOffset(6, 6, 4, 6) };
            Row = new GUIStyle { padding = new RectOffset(4, 4, 2, 2), margin = new RectOffset(0, 0, 0, 1) };
            RowText = new GUIStyle(EditorStyles.wordWrappedLabel) { margin = new RectOffset(2, 2, 2, 2) };
            RowLink = new GUIStyle(EditorStyles.label) { margin = new RectOffset(2, 2, 2, 2) };
            GroupFoldout = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            BadgeText = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white }, padding = new RectOffset(8, 8, 2, 2) };
        }

        /// <summary>A small coloured status pill.</summary>
        public static void Badge(string text, Color color)
        {
            var content = new GUIContent(text);
            var size = BadgeText.CalcSize(content);
            var r = GUILayoutUtility.GetRect(size.x + 8, 18, GUILayout.Width(size.x + 8), GUILayout.Height(18));
            r.y += 4;
            EditorGUI.DrawRect(r, color * new Color(1, 1, 1, 0.9f));
            GUI.Label(r, content, BadgeText);
        }

        /// <summary>Tints the buttons drawn inside it.</summary>
        public readonly struct Tint : IDisposable
        {
            readonly Color prev;
            public Tint(Color c) { prev = GUI.backgroundColor; GUI.backgroundColor = c; }
            public void Dispose() => GUI.backgroundColor = prev;
        }
    }
}
