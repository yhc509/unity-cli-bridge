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
    /// Long badges wrap inside the pill. Short badges stay on one line. A wrapped badge
    /// takes its height from <c>GUIStyle.CalcHeight</c> for the width it is actually
    /// given, so the second line is not clipped. <c>TextClipping.Ellipsis</c> is not
    /// part of the 2021.3 API.
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
        private static GUIStyle? _clippedField;

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
                GUI.DrawTexture(new Rect(iconX, iconY, iconSize, iconSize), icon!, ScaleMode.ScaleToFit, true);
            }

            GUIStyle headerTitle = _headerTitle!;
            GUIContent title = new GUIContent("Unity CLI Bridge");
            Vector2 titleSize = headerTitle.CalcSize(title);
            float titleX = icon != null ? iconX + iconSize + 10f : iconX;
            float titleY = rect.y + ((contentHeight - titleSize.y) * 0.5f);
            GUI.Label(new Rect(titleX, titleY, titleSize.x + 4f, titleSize.y), title, headerTitle);

            if (string.IsNullOrEmpty(version))
            {
                return;
            }

            GUIStyle versionPill = _versionPill!;
            GUIContent versionContent = new GUIContent(version);
            Vector2 versionSize = versionPill.CalcSize(versionContent);
            float pillWidth = versionSize.x + 16f;
            float pillHeight = Mathf.Max(22f, versionSize.y + 6f);
            float pillX = rect.xMax - 12f - pillWidth;
            float pillY = rect.y + ((contentHeight - pillHeight) * 0.5f);
            GUI.Label(new Rect(pillX, pillY, pillWidth, pillHeight), versionContent, versionPill);
        }

        private static float _windowWidth = 420f;
        private static GUIStyle? _scrollContent;

        /// <summary>
        /// Records the EditorWindow width for this OnGUI pass. The scroll body is sized
        /// from this instead of <see cref="EditorGUIUtility.currentViewWidth"/>, which
        /// follows the laid-out content and cannot be used to cap that content.
        /// </summary>
        internal static void SetWindowWidth(float windowWidth)
        {
            _windowWidth = windowWidth < 120f ? 420f : windowWidth;
        }

        /// <summary>
        /// Pixels the scroll view spends on the vertical scrollbar and its own padding.
        /// Always reserved, including when the window is tall enough that the bar is
        /// hidden: IMGUI decides the bar after width is calculated, and a bar that
        /// appears at the default height must not push the content sideways.
        /// <see cref="GUILayout.Width"/> cannot express this. A vertical group's minimum
        /// is raised to its widest child, so the cap is a style <c>fixedWidth</c>.
        /// </summary>
        internal static float ScrollChrome()
        {
            float scrollbar = 16f;
            float scrollPadding = 0f;
            if (GUI.skin != null)
            {
                GUIStyle bar = GUI.skin.verticalScrollbar;
                if (bar != null)
                {
                    float measured = bar.fixedWidth + bar.margin.left;
                    if (measured >= 8f)
                    {
                        scrollbar = measured;
                    }
                }

                GUIStyle scroll = GUI.skin.scrollView;
                if (scroll != null)
                {
                    // Padding sits inside the scroll group and is added to its minimum.
                    // Margin sits outside the group, so the width it is given is already smaller.
                    scrollPadding = scroll.padding.horizontal + scroll.margin.horizontal;
                }
            }

            // A few points of slack cover retina rounding and the window border, so the
            // scroll group's minimum stays inside the client width Unity compares against.
            return scrollbar + scrollPadding + 6f;
        }

        internal static float ScrollContentWidth()
        {
            return Mathf.Max(160f, _windowWidth - ScrollChrome());
        }

        /// <summary>
        /// Width of the section body. Section padding is 12 + 8; the remainder keeps a
        /// wrapped help box from measuring wider than the rect it is given.
        /// </summary>
        internal static float SectionInnerWidth()
        {
            return Mathf.Max(120f, ScrollContentWidth() - 28f);
        }

        internal static float SectionContentWidth()
        {
            return SectionInnerWidth();
        }

        internal static void BeginScrollContent()
        {
            if (_scrollContent == null)
            {
                _scrollContent = new GUIStyle
                {
                    padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0),
                    stretchWidth = false,
                    stretchHeight = false,
                };
            }

            _scrollContent.fixedWidth = ScrollContentWidth();
            GUILayout.BeginVertical(_scrollContent);
        }

        internal static void EndScrollContent()
        {
            GUILayout.EndVertical();
        }

        internal static Rect GetShrinkRow(float height)
        {
            return GUILayoutUtility.GetRect(
                0f,
                0f,
                height,
                height,
                GUIStyle.none,
                GUILayout.MinWidth(0f),
                GUILayout.ExpandWidth(true));
        }

        internal static float LabelColumnWidth(float rowWidth)
        {
            float preferred = EditorGUIUtility.labelWidth;
            float maxLabel = Mathf.Max(48f, rowWidth - 80f);
            return Mathf.Clamp(preferred, 48f, maxLabel);
        }

        internal static GUIStyle ClippedFieldStyle()
        {
            EnsureStyles();
            return _clippedField!;
        }

        internal static void BadgeField(string label, string text, BadgeKind kind)
        {
            EnsureStyles();
            GUIStyle style = BadgeStyle(kind);
            GUIContent content = new GUIContent(text ?? string.Empty);
            float labelWidth = EditorGUIUtility.labelWidth;
            float rowWidth = SectionContentWidth();
            float available = Mathf.Max(48f, rowWidth - labelWidth - 4f);
            ApplyBadgeLayout(style, content, available, out float badgeWidth, out float height);

            Rect row = GUILayoutUtility.GetRect(
                0f,
                height,
                GUILayout.ExpandWidth(true),
                GUILayout.MinWidth(0f),
                GUILayout.MaxWidth(rowWidth));
            float usedLabelWidth = Mathf.Min(labelWidth, Mathf.Max(0f, row.width - 48f));
            Rect labelRect = new Rect(row.x, row.y, usedLabelWidth, row.height);
            float badgeX = labelRect.xMax;
            float usedBadgeWidth = Mathf.Min(badgeWidth, Mathf.Max(32f, row.xMax - badgeX));
            EditorGUI.LabelField(labelRect, label);
            GUI.Label(new Rect(badgeX, row.y, usedBadgeWidth, row.height), content, style);
        }

        internal static void MeasureBadge(string text, BadgeKind kind, float available, out float width, out float height)
        {
            EnsureStyles();
            GUIStyle style = BadgeStyle(kind);
            GUIContent content = new GUIContent(text ?? string.Empty);
            ApplyBadgeLayout(style, content, Mathf.Max(32f, available), out width, out height);
        }

        internal static void DrawBadge(Rect rect, string text, BadgeKind kind)
        {
            EnsureStyles();
            GUIStyle style = BadgeStyle(kind);
            GUIContent content = new GUIContent(text ?? string.Empty);
            ApplyBadgeLayout(style, content, Mathf.Max(32f, rect.width), out float width, out _);
            float used = Mathf.Min(width, Mathf.Max(0f, rect.width));
            GUI.Label(new Rect(rect.x, rect.y, used, rect.height), content, style);
        }

        internal static void DrawWrappedHelp(string message, MessageType type)
        {
            float width = SectionInnerWidth();
            GUIContent content = HelpContent(message, type);
            GUIStyle style = EditorStyles.helpBox;
            float measured = style.CalcHeight(content, Mathf.Max(1f, width));
            float inner = Mathf.Max(1f, width - style.padding.horizontal);
            measured = Mathf.Max(measured, style.CalcHeight(content, inner));
            Texture? image = content.image;
            if (image != null)
            {
                float textWidth = Mathf.Max(1f, width - image!.width - 8f);
                measured = Mathf.Max(measured, style.CalcHeight(new GUIContent(content.text), textWidth));
                measured = Mathf.Max(measured, image!.height + style.padding.vertical);
            }

            float height = Mathf.Max(EditorGUIUtility.singleLineHeight, measured);
            Rect rect = GUILayoutUtility.GetRect(
                0f,
                0f,
                height,
                height,
                style,
                GUILayout.MinWidth(0f),
                GUILayout.ExpandWidth(true));
            EditorGUI.HelpBox(rect, message ?? string.Empty, type);
        }

        private static void ApplyBadgeLayout(GUIStyle style, GUIContent content, float available, out float width, out float height)
        {
            style.wordWrap = false;
            style.alignment = TextAnchor.MiddleLeft;
            float preferred = style.CalcSize(content).x;
            if (preferred <= available + 1f)
            {
                width = preferred;
                height = Mathf.Max(20f, style.CalcHeight(content, Mathf.Max(1f, width)));
                return;
            }

            style.wordWrap = true;
            style.alignment = TextAnchor.UpperLeft;
            width = available;
            float innerWidth = Mathf.Max(1f, width - style.padding.horizontal);
            float fullWidthHeight = style.CalcHeight(content, width);
            float innerWidthHeight = style.CalcHeight(content, innerWidth);
            height = Mathf.Max(20f, Mathf.Max(fullWidthHeight, innerWidthHeight));
        }

        internal static bool PrimaryButton(string text, params GUILayoutOption[] options)
        {
            EnsureStyles();
            return GUILayout.Button(text, _primaryButton!, ShrinkOptions(options));
        }

        internal static bool WideButton(string text, params GUILayoutOption[] options)
        {
            return GUILayout.Button(text, ShrinkOptions(options));
        }

        private static GUILayoutOption[] ShrinkOptions(GUILayoutOption[] options)
        {
            int count = options == null ? 0 : options.Length;
            GUILayoutOption[] result = new GUILayoutOption[count + 2];
            for (int i = 0; i < count; i++)
            {
                result[i] = options![i];
            }

            result[count] = GUILayout.MinWidth(0f);
            result[count + 1] = GUILayout.ExpandWidth(true);
            return result;
        }

        private static GUIContent HelpContent(string message, MessageType type)
        {
            string text = message ?? string.Empty;
            Texture? image = HelpIcon(type);
            return image != null ? new GUIContent(text, image!) : new GUIContent(text);
        }

        private static Texture? HelpIcon(MessageType type)
        {
            string? name;
            switch (type)
            {
                case MessageType.Info:
                    name = "console.infoicon";
                    break;
                case MessageType.Warning:
                    name = "console.warnicon";
                    break;
                case MessageType.Error:
                    name = "console.erroricon";
                    break;
                default:
                    return null;
            }

            GUIContent icon = EditorGUIUtility.IconContent(name);
            return icon != null ? icon.image : null;
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

            GUIStyle section = new GUIStyle
            {
                padding = new RectOffset(12, 8, 6, 8),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(2, 2, 2, 2),
            };
            section.normal.background = sectionTexture;
            _section = section;

            GUIStyle headerTitle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                richText = false,
                wordWrap = false,
            };
            SetAllTextColors(headerTitle, Color.white);
            _headerTitle = headerTitle;

            Texture2D pillTexture = Track(CreateRoundedTexture(32, 20, 8f, new Color(0f, 0f, 0f, 0f), Orange, 1.5f));
            GUIStyle versionPill = new GUIStyle(EditorStyles.label)
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
            versionPill.normal.background = pillTexture;
            SetAllTextColors(versionPill, Orange);
            _versionPill = versionPill;

            GUIStyle clippedField = new GUIStyle(EditorStyles.textField)
            {
                wordWrap = false,
                clipping = TextClipping.Clip,
                stretchWidth = true,
            };
            _clippedField = clippedField;

            Color installed = proSkin ? Hex(0x2F, 0x8F, 0x4E) : Hex(0x2E, 0x7D, 0x46);
            Color none = proSkin ? Hex(0x5C, 0x5C, 0x5C) : Hex(0x6B, 0x6B, 0x6B);
            _badgeInstalled = CreateBadgeStyle(installed, Color.white);
            _badgeUpdate = CreateBadgeStyle(Orange, Navy);
            _badgeNone = CreateBadgeStyle(none, Color.white);

            Texture2D buttonNormal = Track(CreateRoundedTexture(32, 24, 6f, Orange, Orange, 0f));
            Texture2D buttonHover = Track(CreateRoundedTexture(32, 24, 6f, Color.Lerp(Orange, Color.white, 0.14f), Orange, 0f));
            Texture2D buttonActive = Track(CreateRoundedTexture(32, 24, 6f, Color.Lerp(Orange, Color.black, 0.12f), Orange, 0f));
            GUIStyle primaryButton = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(6, 6, 6, 6),
                padding = new RectOffset(10, 10, 4, 4),
                richText = false,
            };
            primaryButton.normal.background = buttonNormal;
            primaryButton.hover.background = buttonHover;
            primaryButton.active.background = buttonActive;
            primaryButton.onNormal.background = buttonNormal;
            primaryButton.onHover.background = buttonHover;
            primaryButton.onActive.background = buttonActive;
            primaryButton.focused.background = buttonNormal;
            SetAllTextColors(primaryButton, Navy);
            _primaryButton = primaryButton;
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
                Texture2D owned = OwnedTextures[i];
                if (owned != null)
                {
                    Object.DestroyImmediate(owned);
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
                GUILayout.Label(title, EditorStyles.boldLabel, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
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
