#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal static class PrefabInspector
    {
        // Reused to avoid GC allocations. Safe because all callers run on the
        // main thread and no method using this buffer calls another that also uses it.
        private static readonly List<Component> _componentBuffer = new List<Component>(8);

        internal static string BuildInspectPayload(string path, GameObject root, bool withValues, int? maxDepth, bool omitDefaults, GameObject? targetRoot = null)
        {
            var builder = new StringBuilder(2048);
            using var stringWriter = new StringWriter(builder, CultureInfo.InvariantCulture);
            using var writer = InspectorJsonWriterUtility.Configure(new JsonTextWriter(stringWriter));
            writer.Formatting = Formatting.None;
            writer.WriteStartObject();
            writer.WritePropertyName("asset");
            InspectorJsonWriterUtility.WriteAssetToken(writer, path);
            writer.WritePropertyName("root");
            GameObject inspectRoot = targetRoot ?? root;
            WriteNode(writer, inspectRoot, BuildNodePath(inspectRoot.transform), withValues, maxDepth, omitDefaults, 0);
            writer.WriteEndObject();
            writer.Flush();
            return builder.ToString();
        }

        internal static GameObject? ResolveInspectRoot(GameObject root, string? path, string commandName)
        {
            return InspectorPathParserUtility.IsRootTraversalSentinel(path)
                ? null
                : ResolveNode(root, path, commandName);
        }

        internal static GameObject ResolveNode(GameObject root, string? path, string commandName)
        {
            string normalizedPath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            if (normalizedPath == "/")
            {
                return root;
            }

            if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
            {
                throw new CommandFailureException("PREFAB_NODE_INVALID", commandName + " path must start with `/`: " + normalizedPath);
            }

            Transform current = root.transform;
            int position = 0;
            while (InspectorPathParserUtility.TryGetNextPathSegment(normalizedPath, ref position, out int segmentStart, out int segmentLength))
            {
                ReadOnlySpan<char> segment = normalizedPath.AsSpan(segmentStart, segmentLength);
                InspectorPathParserUtility.ParsePathSegment(segment, commandName, "PREFAB", out int nameLength, out int siblingIndex);
                ReadOnlySpan<char> name = segment.Slice(0, nameLength);
                Transform? matchedChild = null;
                int matchIndex = 0;
                for (int childIndex = 0; childIndex < current.childCount; childIndex++)
                {
                    Transform child = current.GetChild(childIndex);
                    if (!name.SequenceEqual(child.name.AsSpan()))
                    {
                        continue;
                    }

                    if (matchIndex == siblingIndex)
                    {
                        matchedChild = child;
                        break;
                    }

                    matchIndex++;
                }

                if (matchedChild == null)
                {
                    throw new CommandFailureException("PREFAB_NODE_NOT_FOUND", commandName + " could not find path: " + normalizedPath);
                }

                current = matchedChild;
            }

            return current.gameObject;
        }

        internal static void ApplyNodeState(GameObject target, PrefabNodeSpec? spec, string defaultName, bool allowMissingName)
        {
            if (spec == null)
            {
                throw new CommandFailureException("PREFAB_SPEC_INVALID", "Node spec is empty.");
            }

            string name = string.IsNullOrWhiteSpace(spec.Name)
                ? (allowMissingName ? defaultName : InspectorPathParserUtility.RequireNodeName(spec.Name, "prefab", "PREFAB"))
                : spec.Name.Trim();
            target.name = name;

            PrefabTransformSpec? transformSpec = spec.Transform;
            Transform transform = target.transform;
            InspectorUtility.ApplyNodeStateCore(
                target,
                spec.IsActive,
                spec.Tag,
                spec.Layer,
                InspectorUtility.MergeVector3(transform.localPosition, transformSpec?.LocalPosition?.X, transformSpec?.LocalPosition?.Y, transformSpec?.LocalPosition?.Z),
                InspectorUtility.MergeVector3(transform.localEulerAngles, transformSpec?.LocalRotationEuler?.X, transformSpec?.LocalRotationEuler?.Y, transformSpec?.LocalRotationEuler?.Z),
                InspectorUtility.MergeVector3(transform.localScale, transformSpec?.LocalScale?.X, transformSpec?.LocalScale?.Y, transformSpec?.LocalScale?.Z),
                "PREFAB");
        }

        internal static void ApplyNodeState(GameObject target, PrefabNodeMutationSpec? spec)
        {
            if (spec == null)
            {
                throw new CommandFailureException("PREFAB_SPEC_INVALID", "Node values are empty.");
            }

            if (spec.Name != null)
            {
                target.name = InspectorPathParserUtility.RequireNodeName(spec.Name, "set-node", "PREFAB");
            }

            PrefabTransformSpec? transformSpec = spec.Transform;
            Transform transform = target.transform;
            InspectorUtility.ApplyNodeStateCore(
                target,
                spec.IsActive,
                spec.Tag,
                spec.Layer,
                InspectorUtility.MergeVector3(transform.localPosition, transformSpec?.LocalPosition?.X, transformSpec?.LocalPosition?.Y, transformSpec?.LocalPosition?.Z),
                InspectorUtility.MergeVector3(transform.localEulerAngles, transformSpec?.LocalRotationEuler?.X, transformSpec?.LocalRotationEuler?.Y, transformSpec?.LocalRotationEuler?.Z),
                InspectorUtility.MergeVector3(transform.localScale, transformSpec?.LocalScale?.X, transformSpec?.LocalScale?.Y, transformSpec?.LocalScale?.Z),
                "PREFAB");
        }

        internal static void ApplyNodeState(GameObject target, JObject values, string commandName)
        {
            if (values == null)
            {
                throw new CommandFailureException("PREFAB_SPEC_INVALID", "Node values are empty.");
            }

            string? name = InspectorMutationReaderUtility.ReadOptionalString(values, "name");
            if (name != null)
            {
                target.name = InspectorPathParserUtility.RequireNodeName(name, commandName, "PREFAB");
            }

            JObject? transformValues = InspectorMutationReaderUtility.ReadOptionalObject(values, "transform", "PREFAB_SPEC_INVALID", commandName + " transform value must be an object.");
            Transform transform = target.transform;
            InspectorUtility.ApplyNodeStateCore(
                target,
                InspectorMutationReaderUtility.ReadOptionalBoolean(values, "active"),
                InspectorMutationReaderUtility.ReadOptionalString(values, "tag"),
                InspectorMutationReaderUtility.ReadOptionalProperty(values, "layer"),
                InspectorMutationReaderUtility.MergeVector3(
                    transform.localPosition,
                    InspectorMutationReaderUtility.ReadOptionalObject(transformValues, "localPosition", "PREFAB_SPEC_INVALID", commandName + " transform.localPosition must be an object.")),
                InspectorMutationReaderUtility.MergeVector3(
                    transform.localEulerAngles,
                    InspectorMutationReaderUtility.ReadOptionalObject(transformValues, "localRotationEuler", "PREFAB_SPEC_INVALID", commandName + " transform.localRotationEuler must be an object.")),
                InspectorMutationReaderUtility.MergeVector3(
                    transform.localScale,
                    InspectorMutationReaderUtility.ReadOptionalObject(transformValues, "localScale", "PREFAB_SPEC_INVALID", commandName + " transform.localScale must be an object.")),
                "PREFAB");
        }

        private static void WriteNode(JsonTextWriter writer, GameObject gameObject, string nodePath, bool withValues, int? maxDepth, bool omitDefaults, int currentDepth)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("path");
            writer.WriteValue(nodePath);
            writer.WritePropertyName("name");
            writer.WriteValue(gameObject.name);

            if (!omitDefaults || !gameObject.activeSelf)
            {
                writer.WritePropertyName("active");
                writer.WriteValue(gameObject.activeSelf);
            }

            if (!omitDefaults || !string.Equals(gameObject.tag, "Untagged", StringComparison.Ordinal))
            {
                writer.WritePropertyName("tag");
                writer.WriteValue(gameObject.tag);
            }

            if (!omitDefaults || gameObject.layer != 0)
            {
                writer.WritePropertyName("layer");
                InspectorJsonWriterUtility.WriteLayerToken(writer, gameObject.layer);
            }

            if (InspectorJsonWriterUtility.ShouldWriteTransformToken(gameObject.transform, omitDefaults))
            {
                writer.WritePropertyName("transform");
                InspectorJsonWriterUtility.WriteTransformToken(writer, gameObject.transform, omitDefaults);
            }

            WriteComponents(writer, gameObject, withValues, omitDefaults);
            WriteChildren(writer, gameObject.transform, nodePath, withValues, maxDepth, omitDefaults, currentDepth);
            writer.WriteEndObject();
        }

        internal static string BuildNodePath(Transform transform)
        {
            if (transform.parent == null)
            {
                return "/";
            }

            var builder = new StringBuilder(64);
            AppendNodePath(builder, transform);
            return builder.ToString();
        }

        private static void WriteComponents(JsonTextWriter writer, GameObject gameObject, bool withValues, bool omitDefaults)
        {
            _componentBuffer.Clear();
            gameObject.GetComponents(_componentBuffer);

            int componentCount = CountInspectableComponents(_componentBuffer);
            if (omitDefaults && componentCount == 0)
            {
                return;
            }

            writer.WritePropertyName("components");
            writer.WriteStartArray();
            for (int index = 0; index < _componentBuffer.Count; index++)
            {
                Component component = _componentBuffer[index];
                if (component == null || component is Transform)
                {
                    continue;
                }

                Type componentType = component.GetType();
                writer.WriteStartObject();
                writer.WritePropertyName("type");
                writer.WriteValue(componentType.FullName);
                writer.WritePropertyName("componentIndex");
                writer.WriteValue(CountPriorComponentMatches(_componentBuffer, index, componentType));

                if (withValues)
                {
                    JObject values = SerializedValueApplier.BuildInspectableValues(component);
                    if (omitDefaults)
                    {
                        InspectorDefaultPruningUtility.PruneDefaultInspectableValues(values);
                    }

                    if (!omitDefaults || values.HasValues)
                    {
                        writer.WritePropertyName("values");
                        values.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static void WriteChildren(JsonTextWriter writer, Transform parent, string nodePath, bool withValues, int? maxDepth, bool omitDefaults, int currentDepth)
        {
            int childCount = parent.childCount;
            if (omitDefaults && childCount == 0)
            {
                return;
            }

            writer.WritePropertyName("children");
            writer.WriteStartArray();
            if (maxDepth.HasValue && currentDepth >= maxDepth.Value)
            {
                for (int index = 0; index < childCount; index++)
                {
                    Transform child = parent.GetChild(index);
                    string childPath = BuildChildPath(nodePath, child);
                    writer.WriteStartObject();
                    writer.WritePropertyName("name");
                    writer.WriteValue(child.name);
                    writer.WritePropertyName("path");
                    writer.WriteValue(childPath);
                    writer.WritePropertyName("childCount");
                    writer.WriteValue(child.childCount);
                    writer.WriteEndObject();
                }
            }
            else
            {
                for (int index = 0; index < childCount; index++)
                {
                    Transform child = parent.GetChild(index);
                    WriteNode(writer, child.gameObject, BuildChildPath(nodePath, child), withValues, maxDepth, omitDefaults, currentDepth + 1);
                }
            }

            writer.WriteEndArray();
        }

        private static int CountInspectableComponents(List<Component> components)
        {
            int count = 0;
            for (int index = 0; index < components.Count; index++)
            {
                Component component = components[index];
                if (component != null && !(component is Transform))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountPriorComponentMatches(List<Component> components, int endExclusive, Type componentType)
        {
            int count = 0;
            for (int index = 0; index < endExclusive; index++)
            {
                Component component = components[index];
                if (component == null || component is Transform)
                {
                    continue;
                }

                if (component.GetType() == componentType)
                {
                    count++;
                }
            }

            return count;
        }

        private static void AppendNodePath(StringBuilder builder, Transform transform)
        {
            if (transform.parent == null)
            {
                return;
            }

            AppendNodePath(builder, transform.parent);
            builder.Append('/');
            AppendPathSegment(builder, transform.name, GetSiblingIndex(transform));
        }

        private static string BuildChildPath(string parentPath, Transform child)
        {
            var builder = new StringBuilder(parentPath.Length + child.name.Length + 16);
            if (string.Equals(parentPath, "/", StringComparison.Ordinal))
            {
                builder.Append('/');
            }
            else
            {
                builder.Append(parentPath);
                builder.Append('/');
            }

            AppendPathSegment(builder, child.name, GetSiblingIndex(child));
            return builder.ToString();
        }

        private static void AppendPathSegment(StringBuilder builder, string name, int index)
        {
            builder.Append(name);
            builder.Append('[');
            builder.Append(index);
            builder.Append(']');
        }

        private static int GetSiblingIndex(Transform transform)
        {
            int siblingIndex = 0;
            for (int index = 0; index < transform.parent.childCount; index++)
            {
                Transform sibling = transform.parent.GetChild(index);
                if (sibling == transform)
                {
                    break;
                }

                if (string.Equals(sibling.name, transform.name, StringComparison.Ordinal))
                {
                    siblingIndex++;
                }
            }

            return siblingIndex;
        }
    }
}
