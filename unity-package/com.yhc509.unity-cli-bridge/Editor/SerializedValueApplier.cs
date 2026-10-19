using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    internal static partial class SerializedValueApplier
    {
        private static readonly HashSet<string> _skippedPropertyPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_Script",
            "m_GameObject",
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
        };

        public static void Apply(Component component, JObject values)
        {
            if (component == null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            var serializedObject = new SerializedObject(component);
            serializedObject.Update();

            foreach (JProperty property in values.Properties())
            {
                SerializedProperty? serializedProperty = FindPropertyWithFallback(serializedObject, component.GetType(), property.Name, out IReadOnlyList<string>? attemptedAliasPaths);
                if (serializedProperty == null)
                {
                    string fallbackKey = "m_" + ToPascalCase(property.Name);
                    if (attemptedAliasPaths != null)
                    {
                        throw new CommandFailureException(
                            "COMPONENT_VALUE_KEY_INVALID",
                            "Could not find serialized field: "
                            + property.Name
                            + " (tried keys: alias="
                            + FormatAliasPathList(attemptedAliasPaths)
                            + ", '"
                            + property.Name
                            + "', '"
                            + fallbackKey
                            + "'). Check list-components for a valid property name.");
                    }

                    throw new CommandFailureException(
                        "COMPONENT_VALUE_KEY_INVALID",
                        "Could not find serialized field: "
                        + property.Name
                        + " (tried keys: '"
                        + property.Name
                        + "', '"
                        + fallbackKey
                        + "'). Check list-components for a valid property name.");
                }

                EnsurePatchableProperty(serializedProperty, property.Name);
                ApplyToken(serializedProperty, property.Value, property.Name);
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        public static JObject BuildInspectableValues(Component component)
        {
            var values = new JObject();
            var serializedObject = new SerializedObject(component);
            SerializedProperty iterator = serializedObject.GetIterator();
            if (!iterator.NextVisible(true))
            {
                return values;
            }

            do
            {
                SerializedProperty property = iterator.Copy();
                if (IsPropertySkippable(property))
                {
                    continue;
                }

                if (TrySerializeProperty(property, out JToken token))
                {
                    values[property.propertyPath] = token;
                }
            }
            while (iterator.NextVisible(false));

            return values;
        }

        private static bool IsPropertySkippable(SerializedProperty property)
        {
            return !property.editable
                || _skippedPropertyPaths.Contains(property.propertyPath)
                || property.propertyPath.StartsWith("m_GameObject.", StringComparison.Ordinal);
        }

        private static void EnsurePatchableProperty(SerializedProperty property, string keyOrPath)
        {
            if (!IsPropertySkippable(property))
            {
                return;
            }

            throw new CommandFailureException(
                "COMPONENT_VALUE_KEY_INVALID",
                "Field '"
                + keyOrPath
                + "' is not exposed to inspect, so it cannot be patched (path: "
                + property.propertyPath
                + ").");
        }

        private static string ToPascalCase(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return key;
            }

            if (char.IsUpper(key[0]))
            {
                return key;
            }

            return key.Length == 1
                ? char.ToUpperInvariant(key[0]).ToString()
                : char.ToUpperInvariant(key[0]) + key.Substring(1);
        }

        private static SerializedProperty? FindPropertyWithFallback(SerializedObject serializedObject, Type componentType, string key, out IReadOnlyList<string>? attemptedAliasPaths)
        {
            attemptedAliasPaths = null;

            if (FriendlyKeyAliasCatalog.TryGetCanonicalPaths(componentType, key, out IReadOnlyList<string> aliasPaths))
            {
                attemptedAliasPaths = aliasPaths;
                for (int i = 0; i < aliasPaths.Count; i++)
                {
                    SerializedProperty aliasMatch = serializedObject.FindProperty(aliasPaths[i]);
                    if (aliasMatch != null)
                    {
                        return aliasMatch;
                    }
                }
            }

            SerializedProperty directMatch = serializedObject.FindProperty(key);
            if (directMatch != null)
            {
                return directMatch;
            }

            return serializedObject.FindProperty("m_" + ToPascalCase(key));
        }

        private static string FormatAliasPathList(IReadOnlyList<string> aliasPaths)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append('[');

            for (int i = 0; i < aliasPaths.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append('\'');
                builder.Append(aliasPaths[i]);
                builder.Append('\'');
            }

            builder.Append(']');
            return builder.ToString();
        }

        private static string BuildUnsupportedSerializedPropertyTypeMessage(SerializedProperty property, string? propertyPath = null)
        {
            return string.IsNullOrEmpty(propertyPath)
                ? $"Unsupported SerializedPropertyType: {property.propertyType}"
                : $"Unsupported SerializedPropertyType: {property.propertyType} ({propertyPath})";
        }

        private static void ApplyToken(SerializedProperty property, JToken token, string propertyPath)
        {
            token = CoerceStringEncodedJson(property, token);

            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                ApplyArray(property, token, propertyPath);
                return;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    property.longValue = ReadLong(token, propertyPath);
                    break;
                case SerializedPropertyType.Boolean:
                    property.boolValue = ReadBool(token, propertyPath);
                    break;
                case SerializedPropertyType.Float:
                    property.doubleValue = ReadDouble(token, propertyPath);
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = token.Type == JTokenType.Null ? string.Empty : ReadString(token, propertyPath);
                    break;
                case SerializedPropertyType.Color:
                    property.colorValue = ReadColor(token, propertyPath);
                    break;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = ResolveObjectReference(property, token, propertyPath);
                    break;
                case SerializedPropertyType.LayerMask:
                    property.intValue = ReadInt(token, propertyPath);
                    break;
                case SerializedPropertyType.Enum:
                    property.enumValueIndex = ResolveEnumIndex(property, token, propertyPath);
                    break;
                case SerializedPropertyType.Vector2:
                    property.vector2Value = ReadVector2(token, propertyPath);
                    break;
                case SerializedPropertyType.Vector3:
                    property.vector3Value = ReadVector3(token, propertyPath);
                    break;
                case SerializedPropertyType.Vector4:
                    property.vector4Value = ReadVector4(token, propertyPath);
                    break;
                case SerializedPropertyType.Rect:
                    property.rectValue = ReadRect(token, propertyPath);
                    break;
                case SerializedPropertyType.ArraySize:
                    property.intValue = ReadInt(token, propertyPath);
                    break;
                case SerializedPropertyType.Character:
                    property.intValue = ReadChar(token, propertyPath);
                    break;
                case SerializedPropertyType.AnimationCurve:
                    property.animationCurveValue = ReadAnimationCurve(token, propertyPath);
                    break;
                case SerializedPropertyType.Bounds:
                    property.boundsValue = ReadBounds(token, propertyPath);
                    break;
                case SerializedPropertyType.Gradient:
#if UNITY_2022_1_OR_NEWER
                    property.gradientValue = ReadGradient(token, propertyPath);
                    break;
#else
                    throw new CommandFailureException("UNSUPPORTED_PROPERTY", "Gradient properties require Unity 2022.1 or newer: " + propertyPath);
#endif
                case SerializedPropertyType.Quaternion:
                    property.quaternionValue = ReadQuaternion(token, propertyPath);
                    break;
                case SerializedPropertyType.Vector2Int:
                    property.vector2IntValue = ReadVector2Int(token, propertyPath);
                    break;
                case SerializedPropertyType.Vector3Int:
                    property.vector3IntValue = ReadVector3Int(token, propertyPath);
                    break;
                case SerializedPropertyType.RectInt:
                    property.rectIntValue = ReadRectInt(token, propertyPath);
                    break;
                case SerializedPropertyType.BoundsInt:
                    property.boundsIntValue = ReadBoundsInt(token, propertyPath);
                    break;
                case SerializedPropertyType.ManagedReference:
                    ApplyManagedReference(property, token, propertyPath);
                    break;
                case SerializedPropertyType.Hash128:
                    property.hash128Value = ReadHash128(token, propertyPath);
                    break;
                case SerializedPropertyType.Generic:
                    ApplyObject(property, token, propertyPath);
                    break;
                default:
                    throw new CommandFailureException("PREFAB_FIELD_INVALID", BuildUnsupportedSerializedPropertyTypeMessage(property, propertyPath));
            }
        }

        /// <summary>
        /// Accept a structured value that arrived as a JSON-encoded string.
        ///
        /// Agents routinely send <c>"[1,2,3]"</c> for a Vector3 or <c>"{\"m_Mass\":0.17}"</c> for a
        /// nested object — the value is right, the quoting is not, and strict token typing rejects
        /// it with an error that reads like the field itself was wrong. The guard is deliberately
        /// narrow: only a string token, only where the target cannot hold a raw string, and only
        /// when the trimmed text opens a JSON object or array. Asset paths, object-reference
        /// handles, enum names and char values never start with '{' or '[' and so keep taking the
        /// original path — as does any string that fails to parse, which preserves the existing
        /// validation message.
        /// </summary>
        private static JToken CoerceStringEncodedJson(SerializedProperty property, JToken token)
        {
            if (token == null || token.Type != JTokenType.String || AcceptsRawStringToken(property))
            {
                return token;
            }

            string value = token.Value<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return token;
            }

            string trimmed = value.TrimStart();
            if (trimmed[0] != '{' && trimmed[0] != '[')
            {
                return token;
            }

            try
            {
                return JToken.Parse(value);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return token;
            }
        }

        private static bool AcceptsRawStringToken(SerializedProperty property)
        {
            if (property.isArray && property.propertyType == SerializedPropertyType.String)
            {
                return true;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.String:
                case SerializedPropertyType.Character:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.Hash128:
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Boolean:
                case SerializedPropertyType.Float:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.ArraySize:
                    return true;
                default:
                    return false;
            }
        }

        private static void ApplyArray(SerializedProperty property, JToken token, string propertyPath)
        {
            if (token.Type != JTokenType.Array)
            {
                throw new CommandFailureException("PREFAB_FIELD_INVALID", "Array value must be a JSON array: " + propertyPath);
            }

            JArray array = (JArray)token;
            property.arraySize = array.Count;
            for (int index = 0; index < array.Count; index++)
            {
                SerializedProperty elementProperty = property.GetArrayElementAtIndex(index);
                ApplyToken(elementProperty, array[index], elementProperty.propertyPath);
            }
        }

        private static void ApplyObject(SerializedProperty property, JToken token, string propertyPath)
        {
            if (token.Type != JTokenType.Object)
            {
                throw new CommandFailureException("PREFAB_FIELD_INVALID", "Object value must be a JSON object: " + propertyPath);
            }

            JObject jobject = (JObject)token;
            foreach (JProperty child in jobject.Properties())
            {
                SerializedProperty childProperty = property.FindPropertyRelative(child.Name);
                if (childProperty == null)
                {
                    throw new CommandFailureException("PREFAB_FIELD_INVALID", "Could not find serialized field: " + propertyPath + "." + child.Name);
                }

                EnsurePatchableProperty(childProperty, propertyPath + "." + child.Name);
                ApplyToken(childProperty, child.Value, childProperty.propertyPath);
            }
        }

        private static bool TrySerializeProperty(SerializedProperty property, out JToken token)
        {
            token = null;

            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                var array = new JArray();
                for (int index = 0; index < property.arraySize; index++)
                {
                    if (TrySerializeProperty(property.GetArrayElementAtIndex(index), out JToken itemToken))
                    {
                        array.Add(itemToken);
                    }
                    else
                    {
                        array.Add(JValue.CreateNull());
                    }
                }

                token = array;
                return true;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    token = property.longValue;
                    return true;
                case SerializedPropertyType.Boolean:
                    token = property.boolValue;
                    return true;
                case SerializedPropertyType.Float:
                    token = property.doubleValue;
                    return true;
                case SerializedPropertyType.String:
                    token = property.stringValue;
                    return true;
                case SerializedPropertyType.Color:
                    token = new JObject
                    {
                        ["r"] = property.colorValue.r,
                        ["g"] = property.colorValue.g,
                        ["b"] = property.colorValue.b,
                        ["a"] = property.colorValue.a,
                    };
                    return true;
                case SerializedPropertyType.ObjectReference:
                    return TrySerializeObjectReference(property, out token);
                case SerializedPropertyType.LayerMask:
                    token = property.intValue;
                    return true;
                case SerializedPropertyType.Enum:
                    if (property.enumValueIndex < 0 || property.enumValueIndex >= property.enumNames.Length)
                    {
                        return false;
                    }

                    token = property.enumNames[property.enumValueIndex];
                    return true;
                case SerializedPropertyType.Vector2:
                    token = new JObject
                    {
                        ["x"] = property.vector2Value.x,
                        ["y"] = property.vector2Value.y,
                    };
                    return true;
                case SerializedPropertyType.Vector3:
                    token = new JObject
                    {
                        ["x"] = property.vector3Value.x,
                        ["y"] = property.vector3Value.y,
                        ["z"] = property.vector3Value.z,
                    };
                    return true;
                case SerializedPropertyType.Vector4:
                    token = new JObject
                    {
                        ["x"] = property.vector4Value.x,
                        ["y"] = property.vector4Value.y,
                        ["z"] = property.vector4Value.z,
                        ["w"] = property.vector4Value.w,
                    };
                    return true;
                case SerializedPropertyType.Rect:
                    token = new JObject
                    {
                        ["x"] = property.rectValue.x,
                        ["y"] = property.rectValue.y,
                        ["width"] = property.rectValue.width,
                        ["height"] = property.rectValue.height,
                    };
                    return true;
                case SerializedPropertyType.ArraySize:
                    token = property.intValue;
                    return true;
                case SerializedPropertyType.Character:
                    token = Convert.ToChar(property.intValue).ToString();
                    return true;
                case SerializedPropertyType.AnimationCurve:
                    token = SerializeAnimationCurve(property.animationCurveValue);
                    return true;
                case SerializedPropertyType.Bounds:
                    token = new JObject
                    {
                        ["center"] = new JObject
                        {
                            ["x"] = property.boundsValue.center.x,
                            ["y"] = property.boundsValue.center.y,
                            ["z"] = property.boundsValue.center.z,
                        },
                        ["size"] = new JObject
                        {
                            ["x"] = property.boundsValue.size.x,
                            ["y"] = property.boundsValue.size.y,
                            ["z"] = property.boundsValue.size.z,
                        },
                    };
                    return true;
                case SerializedPropertyType.Gradient:
#if UNITY_2022_1_OR_NEWER
                    token = SerializeGradient(property.gradientValue);
                    return true;
#else
                    token = null;
                    return false;
#endif
                case SerializedPropertyType.Quaternion:
                    token = new JObject
                    {
                        ["x"] = property.quaternionValue.x,
                        ["y"] = property.quaternionValue.y,
                        ["z"] = property.quaternionValue.z,
                        ["w"] = property.quaternionValue.w,
                    };
                    return true;
                case SerializedPropertyType.Vector2Int:
                    token = new JObject
                    {
                        ["x"] = property.vector2IntValue.x,
                        ["y"] = property.vector2IntValue.y,
                    };
                    return true;
                case SerializedPropertyType.Vector3Int:
                    token = new JObject
                    {
                        ["x"] = property.vector3IntValue.x,
                        ["y"] = property.vector3IntValue.y,
                        ["z"] = property.vector3IntValue.z,
                    };
                    return true;
                case SerializedPropertyType.RectInt:
                    token = new JObject
                    {
                        ["x"] = property.rectIntValue.x,
                        ["y"] = property.rectIntValue.y,
                        ["width"] = property.rectIntValue.width,
                        ["height"] = property.rectIntValue.height,
                    };
                    return true;
                case SerializedPropertyType.BoundsInt:
                    token = new JObject
                    {
                        ["position"] = new JObject
                        {
                            ["x"] = property.boundsIntValue.position.x,
                            ["y"] = property.boundsIntValue.position.y,
                            ["z"] = property.boundsIntValue.position.z,
                        },
                        ["size"] = new JObject
                        {
                            ["x"] = property.boundsIntValue.size.x,
                            ["y"] = property.boundsIntValue.size.y,
                            ["z"] = property.boundsIntValue.size.z,
                        },
                    };
                    return true;
                case SerializedPropertyType.ManagedReference:
                    return TrySerializeManagedReference(property, out token);
                case SerializedPropertyType.Hash128:
                    token = property.hash128Value.ToString();
                    return true;
                case SerializedPropertyType.Generic:
                    var jobject = new JObject();
                    foreach (SerializedProperty child in EnumerateDirectChildren(property))
                    {
                        if (IsPropertySkippable(child))
                        {
                            continue;
                        }

                        if (TrySerializeProperty(child, out JToken childToken))
                        {
                            jobject[child.name] = childToken;
                        }
                    }

                    token = jobject;
                    return true;
                default:
                    token = new JValue(BuildUnsupportedSerializedPropertyTypeMessage(property));
                    return false;
            }
        }

        private static IEnumerable<SerializedProperty> EnumerateDirectChildren(SerializedProperty property)
        {
            SerializedProperty iterator = property.Copy();
            SerializedProperty end = iterator.GetEndProperty();
            bool canEnterChildren = true;
            while (iterator.NextVisible(canEnterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                canEnterChildren = false;
                if (iterator.depth == property.depth + 1)
                {
                    yield return iterator.Copy();
                }
            }
        }

        private static bool TrySerializeObjectReference(SerializedProperty property, out JToken token)
        {
            token = null;
            UnityEngine.Object referenced = property.objectReferenceValue;
            if (referenced == null)
            {
                token = JValue.CreateNull();
                return true;
            }

            string assetPath = AssetDatabase.GetAssetPath(referenced);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            token = new JObject
            {
                ["assetPath"] = assetPath,
                ["guid"] = AssetDatabase.AssetPathToGUID(assetPath),
            };
            return true;
        }

        private static UnityEngine.Object ResolveObjectReference(SerializedProperty property, JToken token, string propertyPath)
        {
            if (token.Type == JTokenType.Null)
            {
                return null;
            }

            string assetPath = null;
            string guid = null;
            if (token.Type == JTokenType.String)
            {
                assetPath = token.Value<string>();
            }
            else if (token.Type == JTokenType.Object)
            {
                JObject referenceObject = (JObject)token;
                assetPath = referenceObject.Value<string>("assetPath");
                guid = referenceObject.Value<string>("guid");
            }
            else
            {
                throw new CommandFailureException("PREFAB_REFERENCE_INVALID", "Object reference must be null, a string, or an {assetPath/guid} object: " + propertyPath);
            }

            if (string.IsNullOrWhiteSpace(assetPath) && !string.IsNullOrWhiteSpace(guid))
            {
                assetPath = AssetDatabase.GUIDToAssetPath(guid);
            }

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new CommandFailureException("PREFAB_REFERENCE_INVALID", "Could not find assetPath or guid: " + propertyPath);
            }

            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            if (assets == null || assets.Length == 0)
            {
                throw new CommandFailureException("PREFAB_REFERENCE_INVALID", "Could not find the referenced asset: " + assetPath);
            }

            string expectedTypeName = ExtractObjectReferenceTypeName(property);
            bool hasExpectedTypeName = !string.IsNullOrWhiteSpace(expectedTypeName);
            UnityEngine.Object match = null;
            for (int i = 0; i < assets.Length; i++)
            {
                UnityEngine.Object asset = assets[i];
                if (asset == null)
                {
                    continue;
                }

                if (!hasExpectedTypeName || string.Equals(asset.GetType().Name, expectedTypeName, StringComparison.Ordinal))
                {
                    match = asset;
                    break;
                }
            }

            if (match == null)
            {
                match = AssetDatabase.LoadMainAssetAtPath(assetPath);
            }

            if (match == null)
            {
                throw new CommandFailureException("PREFAB_REFERENCE_INVALID", "Could not load the referenced asset: " + assetPath);
            }

            return match;
        }

        private static string ExtractObjectReferenceTypeName(SerializedProperty property)
        {
            string typeName = property.type ?? string.Empty;
            if (typeName.StartsWith("PPtr<", StringComparison.Ordinal) && typeName.EndsWith(">", StringComparison.Ordinal))
            {
                typeName = typeName.Substring(5, typeName.Length - 6);
            }

            if (typeName.StartsWith("$", StringComparison.Ordinal))
            {
                typeName = typeName.Substring(1);
            }

            return typeName;
        }

        private static int ResolveEnumIndex(SerializedProperty property, JToken token, string propertyPath)
        {
            if (token.Type == JTokenType.Integer)
            {
                int index = token.Value<int>();
                if (index < 0 || index >= property.enumNames.Length)
                {
                    throw new CommandFailureException("PREFAB_FIELD_INVALID", "Enum index is out of range: " + propertyPath);
                }

                return index;
            }

            string enumName = ReadString(token, propertyPath);
            int nameIndex = FindStringIndex(property.enumNames, enumName);
            if (nameIndex >= 0)
            {
                return nameIndex;
            }

            int displayIndex = FindStringIndex(property.enumDisplayNames, enumName);
            if (displayIndex >= 0)
            {
                return displayIndex;
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "Could not find enum value: " + propertyPath + " (" + enumName + ")");
        }

        private static int FindStringIndex(string[] values, string target)
        {
            for (int index = 0; index < values.Length; index++)
            {
                if (string.Equals(values[index], target, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        private static long ReadLong(JToken token, string propertyPath)
        {
            if (token.Type == JTokenType.Integer)
            {
                return token.Value<long>();
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "Integer value required: " + propertyPath);
        }

        private static int ReadInt(JToken token, string propertyPath)
        {
            return checked((int)ReadLong(token, propertyPath));
        }

        private static double ReadDouble(JToken token, string propertyPath)
        {
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                return token.Value<double>();
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "Numeric value required: " + propertyPath);
        }

        private static bool ReadBool(JToken token, string propertyPath)
        {
            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "Bool value required: " + propertyPath);
        }

        private static string ReadString(JToken token, string propertyPath)
        {
            if (token.Type == JTokenType.String)
            {
                return token.Value<string>();
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "String value required: " + propertyPath);
        }

        private static int ReadChar(JToken token, string propertyPath)
        {
            if (token.Type == JTokenType.String)
            {
                string value = token.Value<string>();
                if (!string.IsNullOrEmpty(value))
                {
                    return value[0];
                }
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "Char value required: " + propertyPath);
        }

        private static JObject ReadObject(JToken token, string propertyPath)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                throw new CommandFailureException("PREFAB_FIELD_INVALID", "object value is required: " + propertyPath);
            }

            if (token.Type == JTokenType.Object)
            {
                return (JObject)token;
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "object value is required: " + propertyPath);
        }

        /// <summary>
        /// Read a fixed-arity numeric tuple written as a JSON array — <c>[1,2,3]</c> for a Vector3,
        /// <c>[1,0,0,1]</c> for a Color. The member-object form stays the canonical one (it is what
        /// inspect emits); this is the shorthand agents reach for, and it is unambiguous for the
        /// component types below. Entries past <paramref name="requiredCount"/> are optional and
        /// keep the caller's defaults.
        /// </summary>
        private static float[] ReadFloatTuple(
            JArray array,
            string propertyPath,
            int requiredCount,
            float[] defaults)
        {
            if (array.Count < requiredCount || array.Count > defaults.Length)
            {
                throw new CommandFailureException(
                    "PREFAB_FIELD_INVALID",
                    "Array value must contain "
                    + (requiredCount == defaults.Length
                        ? requiredCount.ToString()
                        : requiredCount + "~" + defaults.Length)
                    + " numbers: " + propertyPath);
            }

            var values = (float[])defaults.Clone();
            for (int index = 0; index < array.Count; index++)
            {
                JToken element = array[index];
                if (element.Type != JTokenType.Integer && element.Type != JTokenType.Float)
                {
                    throw new CommandFailureException(
                        "PREFAB_FIELD_INVALID",
                        "Numeric value required: " + propertyPath + "[" + index + "]");
                }

                values[index] = element.Value<float>();
            }

            return values;
        }

        private static int[] ReadIntTuple(JArray array, string propertyPath, int count)
        {
            if (array.Count != count)
            {
                throw new CommandFailureException(
                    "PREFAB_FIELD_INVALID",
                    "Array value must contain " + count + " integers: " + propertyPath);
            }

            var values = new int[count];
            for (int index = 0; index < count; index++)
            {
                JToken element = array[index];
                if (element.Type != JTokenType.Integer)
                {
                    throw new CommandFailureException(
                        "PREFAB_FIELD_INVALID",
                        "Integer value required: " + propertyPath + "[" + index + "]");
                }

                values[index] = element.Value<int>();
            }

            return values;
        }

        private static bool TryReadTupleArray(JToken token, out JArray array)
        {
            array = token as JArray;
            return array != null;
        }

        private static float ReadFloatMember(JObject obj, string memberName, string propertyPath, float fallback = 0f)
        {
            JToken member = obj[memberName];
            if (member == null)
            {
                return fallback;
            }

            if (member.Type == JTokenType.Integer || member.Type == JTokenType.Float)
            {
                return member.Value<float>();
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "Numeric value required: " + propertyPath + "." + memberName);
        }

        private static int ReadIntMember(JObject obj, string memberName, string propertyPath, int fallback = 0)
        {
            JToken member = obj[memberName];
            if (member == null)
            {
                return fallback;
            }

            if (member.Type == JTokenType.Integer)
            {
                return member.Value<int>();
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "Integer value required: " + propertyPath + "." + memberName);
        }

        private static Color ReadColor(JToken token, string propertyPath)
        {
            if (TryReadTupleArray(token, out JArray array))
            {
                float[] values = ReadFloatTuple(array, propertyPath, 3, new[] { 0f, 0f, 0f, 1f });
                return new Color(values[0], values[1], values[2], values[3]);
            }

            JObject obj = ReadObject(token, propertyPath);
            return new Color(
                ReadFloatMember(obj, "r", propertyPath),
                ReadFloatMember(obj, "g", propertyPath),
                ReadFloatMember(obj, "b", propertyPath),
                ReadFloatMember(obj, "a", propertyPath, 1f));
        }

        private static Vector2 ReadVector2(JToken token, string propertyPath)
        {
            if (TryReadTupleArray(token, out JArray array))
            {
                float[] values = ReadFloatTuple(array, propertyPath, 2, new[] { 0f, 0f });
                return new Vector2(values[0], values[1]);
            }

            JObject obj = ReadObject(token, propertyPath);
            return new Vector2(
                ReadFloatMember(obj, "x", propertyPath),
                ReadFloatMember(obj, "y", propertyPath));
        }

        private static Vector3 ReadVector3(JToken token, string propertyPath)
        {
            if (TryReadTupleArray(token, out JArray array))
            {
                float[] values = ReadFloatTuple(array, propertyPath, 3, new[] { 0f, 0f, 0f });
                return new Vector3(values[0], values[1], values[2]);
            }

            JObject obj = ReadObject(token, propertyPath);
            return new Vector3(
                ReadFloatMember(obj, "x", propertyPath),
                ReadFloatMember(obj, "y", propertyPath),
                ReadFloatMember(obj, "z", propertyPath));
        }

        private static Vector4 ReadVector4(JToken token, string propertyPath)
        {
            if (TryReadTupleArray(token, out JArray array))
            {
                float[] values = ReadFloatTuple(array, propertyPath, 4, new[] { 0f, 0f, 0f, 0f });
                return new Vector4(values[0], values[1], values[2], values[3]);
            }

            JObject obj = ReadObject(token, propertyPath);
            return new Vector4(
                ReadFloatMember(obj, "x", propertyPath),
                ReadFloatMember(obj, "y", propertyPath),
                ReadFloatMember(obj, "z", propertyPath),
                ReadFloatMember(obj, "w", propertyPath));
        }

        private static Rect ReadRect(JToken token, string propertyPath)
        {
            if (TryReadTupleArray(token, out JArray array))
            {
                float[] values = ReadFloatTuple(array, propertyPath, 4, new[] { 0f, 0f, 0f, 0f });
                return new Rect(values[0], values[1], values[2], values[3]);
            }

            JObject obj = ReadObject(token, propertyPath);
            return new Rect(
                ReadFloatMember(obj, "x", propertyPath),
                ReadFloatMember(obj, "y", propertyPath),
                ReadFloatMember(obj, "width", propertyPath),
                ReadFloatMember(obj, "height", propertyPath));
        }

        private static Bounds ReadBounds(JToken token, string propertyPath)
        {
            JObject obj = ReadObject(token, propertyPath);
            return new Bounds(
                ReadVector3(obj["center"], propertyPath + ".center"),
                ReadVector3(obj["size"], propertyPath + ".size"));
        }

        private static Quaternion ReadQuaternion(JToken token, string propertyPath)
        {
            if (TryReadTupleArray(token, out JArray array))
            {
                float[] values = ReadFloatTuple(array, propertyPath, 4, new[] { 0f, 0f, 0f, 0f });
                return new Quaternion(values[0], values[1], values[2], values[3]);
            }

            JObject obj = ReadObject(token, propertyPath);
            return new Quaternion(
                ReadFloatMember(obj, "x", propertyPath),
                ReadFloatMember(obj, "y", propertyPath),
                ReadFloatMember(obj, "z", propertyPath),
                ReadFloatMember(obj, "w", propertyPath));
        }

        private static Vector2Int ReadVector2Int(JToken token, string propertyPath)
        {
            if (TryReadTupleArray(token, out JArray array))
            {
                int[] values = ReadIntTuple(array, propertyPath, 2);
                return new Vector2Int(values[0], values[1]);
            }

            JObject obj = ReadObject(token, propertyPath);
            return new Vector2Int(
                ReadIntMember(obj, "x", propertyPath),
                ReadIntMember(obj, "y", propertyPath));
        }

        private static Vector3Int ReadVector3Int(JToken token, string propertyPath)
        {
            if (TryReadTupleArray(token, out JArray array))
            {
                int[] values = ReadIntTuple(array, propertyPath, 3);
                return new Vector3Int(values[0], values[1], values[2]);
            }

            JObject obj = ReadObject(token, propertyPath);
            return new Vector3Int(
                ReadIntMember(obj, "x", propertyPath),
                ReadIntMember(obj, "y", propertyPath),
                ReadIntMember(obj, "z", propertyPath));
        }

        private static RectInt ReadRectInt(JToken token, string propertyPath)
        {
            if (TryReadTupleArray(token, out JArray array))
            {
                int[] values = ReadIntTuple(array, propertyPath, 4);
                return new RectInt(values[0], values[1], values[2], values[3]);
            }

            JObject obj = ReadObject(token, propertyPath);
            return new RectInt(
                ReadIntMember(obj, "x", propertyPath),
                ReadIntMember(obj, "y", propertyPath),
                ReadIntMember(obj, "width", propertyPath),
                ReadIntMember(obj, "height", propertyPath));
        }

        private static BoundsInt ReadBoundsInt(JToken token, string propertyPath)
        {
            JObject obj = ReadObject(token, propertyPath);
            return new BoundsInt(
                ReadVector3Int(obj["position"], propertyPath + ".position"),
                ReadVector3Int(obj["size"], propertyPath + ".size"));
        }

    }
}
