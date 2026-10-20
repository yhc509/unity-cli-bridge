#nullable enable
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal static class InspectorJsonWriterUtility
    {
        internal static JsonTextWriter Configure(JsonTextWriter writer)
        {
            writer.FloatFormatHandling = FloatFormatHandling.String;
            return writer;
        }

        internal static void WriteAssetToken(JsonWriter writer, string path)
        {
            AssetRecord record = AssetCommandSupport.BuildRecordFromPath(path, allowPackages: true);
            writer.WriteStartObject();
            writer.WritePropertyName("path");
            writer.WriteValue(record.path);
            writer.WritePropertyName("guid");
            writer.WriteValue(record.guid);
            writer.WritePropertyName("assetName");
            writer.WriteValue(record.assetName);
            writer.WritePropertyName("mainType");
            writer.WriteValue(record.mainType);
            writer.WritePropertyName("isFolder");
            writer.WriteValue(record.isFolder);
            writer.WritePropertyName("exists");
            writer.WriteValue(record.exists);
            writer.WriteEndObject();
        }

        internal static void WriteLayerToken(JsonWriter writer, int layerIndex)
        {
            string layerName = LayerMask.LayerToName(layerIndex);
            if (string.IsNullOrWhiteSpace(layerName))
            {
                writer.WriteValue(layerIndex);
                return;
            }

            writer.WriteValue(layerName);
        }

        internal static bool ShouldWriteTransformToken(Transform transform, bool omitDefaults)
        {
            return !omitDefaults
                || !IsExactlyZero(transform.localPosition)
                || !IsExactlyIdentity(transform.localRotation)
                || !IsExactlyOne(transform.localScale);
        }

        internal static void WriteTransformToken(JsonWriter writer, Transform transform, bool omitDefaults)
        {
            writer.WriteStartObject();
            if (!omitDefaults || !IsExactlyZero(transform.localPosition))
            {
                writer.WritePropertyName("localPosition");
                WriteVector3Token(writer, transform.localPosition);
            }

            if (!omitDefaults || !IsExactlyIdentity(transform.localRotation))
            {
                writer.WritePropertyName("localRotationEuler");
                WriteVector3Token(writer, transform.localEulerAngles);
            }

            if (!omitDefaults || !IsExactlyOne(transform.localScale))
            {
                writer.WritePropertyName("localScale");
                WriteVector3Token(writer, transform.localScale);
            }

            writer.WriteEndObject();
        }

        internal static void WriteVector3Token(JsonWriter writer, Vector3 vector)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("x");
            writer.WriteValue(vector.x);
            writer.WritePropertyName("y");
            writer.WriteValue(vector.y);
            writer.WritePropertyName("z");
            writer.WriteValue(vector.z);
            writer.WriteEndObject();
        }

        private static bool IsExactlyZero(Vector3 value)
        {
            return value.x == 0f && value.y == 0f && value.z == 0f;
        }

        private static bool IsExactlyOne(Vector3 value)
        {
            return value.x == 1f && value.y == 1f && value.z == 1f;
        }

        private static bool IsExactlyIdentity(Quaternion value)
        {
            return value.x == 0f && value.y == 0f && value.z == 0f && value.w == 1f;
        }

        // One cache per inspect call. Inspect runs on the editor main thread and does not re-enter.
        [System.ThreadStatic]
        private static Dictionary<ulong, PropertyModification[]>? _prefabModificationCache;

        internal static void BeginPrefabModificationCache()
        {
            _prefabModificationCache = new Dictionary<ulong, PropertyModification[]>();
        }

        internal static void EndPrefabModificationCache()
        {
            _prefabModificationCache = null;
        }

        internal static bool? ResolveComponentEnabled(Component component)
        {
            bool? runtime = ReadRuntimeEnabled(component);
            bool? overridden = TryReadPrefabEnabledOverride(component);
            if (overridden.HasValue)
            {
                return overridden.Value;
            }

            if (runtime.HasValue)
            {
                return runtime.Value;
            }

            return ReadSerializedEnabled(component);
        }

        internal static void WriteEnabledProperty(JsonWriter writer, bool? enabled, bool omitDefaults)
        {
            if (!SceneInspectValueRules.ShouldWriteEnabledFlag(enabled, omitDefaults) || !enabled.HasValue)
            {
                return;
            }

            writer.WritePropertyName("enabled");
            writer.WriteValue(enabled.Value);
        }

        internal static void WritePrefabSource(JsonWriter writer, GameObject gameObject, bool omitDefaults)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(gameObject))
            {
                return;
            }

            string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            GameObject nearest = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            GameObject outermost = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
            bool isRoot = nearest == gameObject;
            bool nested = nearest != null && outermost != null && nearest != outermost;
            bool added = PrefabUtility.IsAddedGameObjectOverride(gameObject);

            writer.WritePropertyName("prefab");
            writer.WriteStartObject();
            writer.WritePropertyName("assetPath");
            writer.WriteValue(assetPath);
            if (SceneInspectValueRules.ShouldWritePrefabFlag(isRoot, omitDefaults))
            {
                writer.WritePropertyName("isRoot");
                writer.WriteValue(isRoot);
            }

            if (SceneInspectValueRules.ShouldWritePrefabFlag(nested, omitDefaults))
            {
                writer.WritePropertyName("nested");
                writer.WriteValue(nested);
            }

            if (added)
            {
                writer.WritePropertyName("added");
                writer.WriteValue(true);
            }

            writer.WriteEndObject();
        }

        private static bool? ReadRuntimeEnabled(Component component)
        {
            if (component is Behaviour behaviour)
            {
                return behaviour.enabled;
            }

            if (component is Renderer renderer)
            {
                return renderer.enabled;
            }

            if (component is Collider collider)
            {
                return collider.enabled;
            }

            return null;
        }

        private static bool? ReadSerializedEnabled(Component component)
        {
            var serializedObject = new SerializedObject(component);
            SerializedProperty property = serializedObject.FindProperty("m_Enabled");
            if (property == null || property.propertyType != SerializedPropertyType.Boolean)
            {
                return null;
            }

            return property.boolValue;
        }

        private static bool? TryReadPrefabEnabledOverride(Component component)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(component))
            {
                return null;
            }

            bool found = false;
            bool value = false;
            GameObject? cursor = component.gameObject;
            var seenRoots = new HashSet<ulong>();
            while (cursor != null)
            {
                GameObject root = PrefabUtility.GetNearestPrefabInstanceRoot(cursor);
                if (root == null)
                {
                    break;
                }

                ulong rootId = UnityObjectIdentity.GetId(root);
                if (!seenRoots.Add(rootId))
                {
                    break;
                }

                PropertyModification[] modifications = GetPropertyModifications(root, rootId);
                for (int index = 0; index < modifications.Length; index++)
                {
                    PropertyModification modification = modifications[index];
                    if (modification == null
                        || !string.Equals(modification.propertyPath, "m_Enabled", System.StringComparison.Ordinal)
                        || !ModificationTargetsComponent(modification.target, component))
                    {
                        continue;
                    }

                    value = IsEnabledModificationValue(modification.value);
                    found = true;
                }

                Transform parent = root.transform.parent;
                cursor = parent != null ? parent.gameObject : null;
            }

            return found ? value : (bool?)null;
        }

        private static PropertyModification[] GetPropertyModifications(GameObject root, ulong rootId)
        {
            Dictionary<ulong, PropertyModification[]>? cache = _prefabModificationCache;
            if (cache != null && cache.TryGetValue(rootId, out PropertyModification[]? cached))
            {
                return cached;
            }

            PropertyModification[] modifications = PrefabUtility.GetPropertyModifications(root) ?? System.Array.Empty<PropertyModification>();
            if (cache != null)
            {
                cache[rootId] = modifications;
            }

            return modifications;
        }

        private static bool ModificationTargetsComponent(UnityEngine.Object? target, Component component)
        {
            if (target == null)
            {
                return false;
            }

            UnityEngine.Object? current = component;
            for (int depth = 0; current != null && depth < 8; depth++)
            {
                if (current == target)
                {
                    return true;
                }

                current = PrefabUtility.GetCorrespondingObjectFromSource(current);
            }

            return false;
        }

        private static bool IsEnabledModificationValue(string? value)
        {
            return string.Equals(value, "1", System.StringComparison.Ordinal)
                || string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
