#nullable enable
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// IMGUI skins for Window &gt; Unity CLI Manager. Every API used here is available on
    /// Unity 2021.3 LTS and Unity 6. Styles and generated textures rebuild when
    /// <see cref="EditorGUIUtility.isProSkin"/> changes.
    /// Long badges wrap inside the pill instead of using <c>TextClipping.Ellipsis</c>,
    /// which is not part of the 2021.3 API. Wrapping keeps the 420px minimum width from
    /// clipping "Installed (vX) -&gt; update available (vY)".
    /// </summary>
    internal static class CliManagerStyles
    {
        internal enum BadgeKind
        {
            Installed,
            Update,
            None,
        }

        internal static readonly Color Navy = Hex(0x0F, 0x1E, 0x3D);
        internal static readonly Color Orange = Hex(0xF2, 0x6B, 0x21);

        private static readonly List<Texture2D> OwnedTextures = new List<Texture2D>();
        private static bool _hasBuilt;
        private static bool _builtForProSkin;
        private static GUIStyle? _section;
        private static GUIStyle? _headerTitle;
        private static GUIStyle? _versionPill;
        private static GUIStyle? _primaryButton;
        private static GUIStyle? _badgeInstalled;
        private static GUIStyle? _badgeUpdate;
        private static GUIStyle? _badgeNone;

        internal static void EnsureStyles()
        {
            bool proSkin = EditorGUIUtility.isProSkin;
            if (_hasBuilt && _builtForProSkin == proSkin && _section != null)
            {
                return;
            }

            DestroyOwnedTextures();
            Build(proSkin);
            _builtForProSkin = proSkin;
            _hasBuilt = true;
        }

        internal static void DrawHeader(Texture2D? icon, string version)
        {
            EnsureStyles();
            Rect rect = GUILayoutUtility.GetRect(0f, 52f, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            EditorGUI.DrawRect(rect, Navy);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 2f, rect.width, 2f), Orange);

            const float iconSize = 36f;
            float contentHeight = rect.height - 2f;
            float iconX = rect.x + 12f;
            if (icon != null)
            {
                float iconY = rect.y + ((contentHeight - iconSize) * 0.5f);
                GUI.DrawTexture(new Rect(iconX, iconY, iconSize, iconSize), icon, ScaleMode.ScaleToFit, true);
            }

            GUIContent title = new GUIContent("Unity CLI Bridge");
            Vector2 titleSize = _headerTitle!.CalcSize(title);
            float titleX = icon != null ? iconX + iconSize + 10f : iconX;
            float titleY = rect.y + ((contentHeight - titleSize.y) * 0.5f);
            GUI.Label(new Rect(titleX, titleY, titleSize.x + 4f, titleSize.y), title, _headerTitle);

            if (string.IsNullOrEmpty(version))
            {
                return;
            }

            GUIContent versionContent = new GUIContent(version);
            Vector2 versionSize = _versionPill!.CalcSize(versionContent);
            float pillWidth = versionSize.x + 16f;
            float pillHeight = Mathf.Max(22f, versionSize.y + 6f);
            float pillX = rect.xMax - 12f - pillWidth;
            float pillY = rect.y + ((contentHeight - pillHeight) * 0.5f);
            GUI.Label(new Rect(pillX, pillY, pillWidth, pillHeight), versionContent, _versionPill);
        }

        internal static void BadgeField(string label, string text, BadgeKind kind)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                DrawBadge(text, kind, 0f);
                GUILayout.FlexibleSpace();
            }
        }

        internal static void DrawBadge(string text, BadgeKind kind, float reservedRight)
        {
            EnsureStyles();
            GUIStyle style = BadgeStyle(kind);
            GUIContent content = new GUIContent(text ?? string.Empty);
            float viewWidth = EditorGUIUtility.currentViewWidth;
            if (viewWidth < 120f)
            {
                viewWidth = 420f;
            }

            float available = Mathf.Max(64f, viewWidth - EditorGUIUtility.labelWidth - 36f - reservedRight);
            float preferred = style.CalcSize(content).x;
            float width = Mathf.Min(preferred, available);
            float height = Mathf.Max(20f, style.CalcHeight(content, width));
            GUILayout.Label(content, style, GUILayout.Width(width), GUILayout.Height(height));
        }

        internal static bool PrimaryButton(string text, params GUILayoutOption[] options)
        {
            EnsureStyles();
            return GUILayout.Button(text, _primaryButton!, options);
        }

        internal static void HighlightRow(Rect row)
        {
            Color fill = Orange;
            fill.a = EditorGUIUtility.isProSkin ? 0.14f : 0.10f;
            EditorGUI.DrawRect(row, fill);
            EditorGUI.DrawRect(new Rect(row.x, row.y, 2f, row.height), Orange);
        }

        private static GUIStyle BadgeStyle(BadgeKind kind)
        {
            EnsureStyles();
            switch (kind)
            {
                case BadgeKind.Installed:
                    return _badgeInstalled!;
                case BadgeKind.Update:
                    return _badgeUpdate!;
                default:
                    return _badgeNone!;
            }
        }

        private static void Build(bool proSkin)
        {
            Color sectionFill = proSkin ? Hex(0x3A, 0x3A, 0x3A) : Hex(0xFF, 0xFF, 0xFF);
            Color sectionBorder = proSkin ? Hex(0x22, 0x22, 0x22) : Hex(0xC2, 0xC2, 0xC2);
            Texture2D sectionTexture = Track(CreateFramedTexture(sectionFill, sectionBorder));

            _section = new GUIStyle
            {
                padding = new RectOffset(12, 8, 6, 8),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(2, 2, 2, 2),
            };
            _section.normal.background = sectionTexture;

            _headerTitle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                richText = false,
                wordWrap = false,
            };
            SetAllTextColors(_headerTitle, Color.white);

            Texture2D pillTexture = Track(CreateRoundedTexture(32, 20, 8f, new Color(0f, 0f, 0f, 0f), Orange, 1.5f));
            _versionPill = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 2, 2),
                border = new RectOffset(8, 8, 8, 8),
                richText = false,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            _versionPill.normal.background = pillTexture;
            SetAllTextColors(_versionPill, Orange);

            Color installed = proSkin ? Hex(0x2F, 0x8F, 0x4E) : Hex(0x2E, 0x7D, 0x46);
            Color none = proSkin ? Hex(0x5C, 0x5C, 0x5C) : Hex(0x6B, 0x6B, 0x6B);
            _badgeInstalled = CreateBadgeStyle(installed, Color.white);
            _badgeUpdate = CreateBadgeStyle(Orange, Navy);
            _badgeNone = CreateBadgeStyle(none, Color.white);

            Texture2D buttonNormal = Track(CreateRoundedTexture(32, 24, 6f, Orange, Orange, 0f));
            Texture2D buttonHover = Track(CreateRoundedTexture(32, 24, 6f, Color.Lerp(Orange, Color.white, 0.14f), Orange, 0f));
            Texture2D buttonActive = Track(CreateRoundedTexture(32, 24, 6f, Color.Lerp(Orange, Color.black, 0.12f), Orange, 0f));
            _primaryButton = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(6, 6, 6, 6),
                padding = new RectOffset(10, 10, 4, 4),
                richText = false,
            };
            _primaryButton.normal.background = buttonNormal;
            _primaryButton.hover.background = buttonHover;
            _primaryButton.active.background = buttonActive;
            _primaryButton.onNormal.background = buttonNormal;
            _primaryButton.onHover.background = buttonHover;
            _primaryButton.onActive.background = buttonActive;
            _primaryButton.focused.background = buttonNormal;
            SetAllTextColors(_primaryButton, Navy);
        }

        private static GUIStyle CreateBadgeStyle(Color background, Color text)
        {
            GUIStyle style = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 3, 3),
                border = new RectOffset(8, 8, 8, 8),
                richText = false,
                wordWrap = true,
                clipping = TextClipping.Clip,
            };
            style.normal.background = Track(CreateRoundedTexture(32, 20, 8f, background, background, 0f));
            SetAllTextColors(style, text);
            return style;
        }

        private static void SetAllTextColors(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
            style.onNormal.textColor = color;
            style.onHover.textColor = color;
            style.onActive.textColor = color;
            style.onFocused.textColor = color;
        }

        private static Texture2D CreateFramedTexture(Color fill, Color border)
        {
            const int size = 8;
            Texture2D texture = CreateTexture(size, size);
            Color32[] pixels = new Color32[size * size];
            Color32 fill32 = fill;
            Color32 border32 = border;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool edge = x < 2 || y < 2 || x >= size - 2 || y >= size - 2;
                    pixels[(y * size) + x] = edge ? border32 : fill32;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Texture2D CreateRoundedTexture(int width, int height, float radius, Color fill, Color border, float borderWidth)
        {
            Texture2D texture = CreateTexture(width, height);
            Color32[] pixels = new Color32[width * height];
            float halfWidth = (width - 1) * 0.5f;
            float halfHeight = (height - 1) * 0.5f;
            float clampedRadius = Mathf.Clamp(radius, 0f, Mathf.Min(halfWidth, halfHeight));
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float distance = RoundedBoxDistance(x - halfWidth, y - halfHeight, halfWidth, halfHeight, clampedRadius);
                    float coverage = Mathf.Clamp01(0.5f - distance);
                    Color color = fill;
                    if (borderWidth > 0.01f)
                    {
                        float ring = Mathf.Clamp01((-distance) / borderWidth);
                        color = Color.Lerp(border, fill, ring);
                    }

                    color.a *= coverage;
                    pixels[(y * width) + x] = color;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static float RoundedBoxDistance(float px, float py, float halfWidth, float halfHeight, float radius)
        {
            float qx = Mathf.Abs(px) - (halfWidth - radius);
            float qy = Mathf.Abs(py) - (halfHeight - radius);
            float outsideX = Mathf.Max(qx, 0f);
            float outsideY = Mathf.Max(qy, 0f);
            float outside = Mathf.Sqrt((outsideX * outsideX) + (outsideY * outsideY));
            float inside = Mathf.Min(Mathf.Max(qx, qy), 0f);
            return (outside + inside) - radius;
        }

        private static Texture2D CreateTexture(int width, int height)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            return texture;
        }

        private static Texture2D Track(Texture2D texture)
        {
            OwnedTextures.Add(texture);
            return texture;
        }

        private static void DestroyOwnedTextures()
        {
            for (int i = 0; i < OwnedTextures.Count; i++)
            {
                if (OwnedTextures[i] != null)
                {
                    Object.DestroyImmediate(OwnedTextures[i]);
                }
            }

            OwnedTextures.Clear();
        }

        private static Color Hex(byte red, byte green, byte blue)
        {
            return new Color(red / 255f, green / 255f, blue / 255f, 1f);
        }

        internal sealed class SectionScope : GUI.Scope
        {
            private readonly Rect _rect;

            internal SectionScope(string title)
            {
                EnsureStyles();
                _rect = EditorGUILayout.BeginVertical(_section!);
                GUILayout.Label(title, EditorStyles.boldLabel);
                Rect rule = GUILayoutUtility.GetRect(0f, 1f, GUILayout.ExpandWidth(true));
                GUILayout.Space(4f);
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(new Rect(_rect.x, _rect.y, 3f, _rect.height), Orange);
                    Color ruleColor = EditorGUIUtility.isProSkin
                        ? new Color(1f, 1f, 1f, 0.14f)
                        : new Color(0f, 0f, 0f, 0.12f);
                    EditorGUI.DrawRect(rule, ruleColor);
                }
            }

            protected override void CloseScope()
            {
                EditorGUILayout.EndVertical();
            }
        }
    }
}
