#nullable enable
using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal static class InspectorUtility
    {
        internal static Vector3? MergeVector3(Vector3 current, float? x, float? y, float? z)
        {
            if (!x.HasValue && !y.HasValue && !z.HasValue)
            {
                return null;
            }

            return new Vector3(
                x ?? current.x,
                y ?? current.y,
                z ?? current.z);
        }

        internal static int ResolveLayer(JToken token, string errorPrefix)
        {
            if (token.Type == JTokenType.Integer)
            {
                int layerIndex = token.Value<int>();
                if (layerIndex < 0 || layerIndex > 31)
                {
                    throw new CommandFailureException(errorPrefix + "_NODE_INVALID", "Layer index is out of range: " + layerIndex);
                }

                return layerIndex;
            }

            if (token.Type != JTokenType.String)
            {
                throw new CommandFailureException(errorPrefix + "_NODE_INVALID", "Layer must be a string or an int.");
            }

            string? layerName = token.Value<string>();
            if (string.IsNullOrWhiteSpace(layerName))
            {
                throw new CommandFailureException(errorPrefix + "_NODE_INVALID", "Layer string is empty.");
            }

            if (int.TryParse(layerName, out int numericLayer))
            {
                return ResolveLayer(new JValue(numericLayer), errorPrefix);
            }

            int resolvedLayer = LayerMask.NameToLayer(layerName);
            if (resolvedLayer < 0)
            {
                throw new CommandFailureException(errorPrefix + "_NODE_INVALID", "Could not find layer: " + layerName);
            }

            return resolvedLayer;
        }

        internal static void ApplyTransformSpec(Transform transform, Vector3? position, Vector3? rotation, Vector3? scale)
        {
            if (position.HasValue)
            {
                transform.localPosition = position.Value;
            }

            if (rotation.HasValue)
            {
                transform.localEulerAngles = rotation.Value;
            }

            if (scale.HasValue)
            {
                transform.localScale = scale.Value;
            }
        }

        internal static void ApplyNodeStateCore(
            GameObject target,
            bool? active,
            string? tag,
            JToken? layer,
            Vector3? position,
            Vector3? rotation,
            Vector3? scale,
            string errorPrefix)
        {
            if (active.HasValue)
            {
                target.SetActive(active.Value);
            }

            if (tag != null)
            {
                string normalizedTag = tag.Trim();
                if (string.IsNullOrWhiteSpace(normalizedTag))
                {
                    throw new CommandFailureException(errorPrefix + "_NODE_INVALID", "Tag is empty.");
                }

                target.tag = normalizedTag;
            }

            if (layer != null)
            {
                target.layer = ResolveLayer(layer, errorPrefix);
            }

            ApplyTransformSpec(target.transform, position, rotation, scale);
        }
    }
}
