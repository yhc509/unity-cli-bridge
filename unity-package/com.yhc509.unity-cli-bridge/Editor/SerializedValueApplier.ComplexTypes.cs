using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    internal static partial class SerializedValueApplier
    {
        private static System.Reflection.Assembly[] _managedReferenceAssemblies = Array.Empty<System.Reflection.Assembly>();

        private static JToken ReadRequiredMember(JObject obj, string memberName, string propertyPath)
        {
            JToken member = obj[memberName];
            if (member != null)
            {
                return member;
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "Could not find required field: " + propertyPath + "." + memberName);
        }

        private static JArray ReadArray(JToken token, string propertyPath)
        {
            if (token.Type == JTokenType.Array)
            {
                return (JArray)token;
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "Array value required: " + propertyPath);
        }

        private static JArray ReadRequiredArrayMember(JObject obj, string memberName, string propertyPath)
        {
            return ReadArray(ReadRequiredMember(obj, memberName, propertyPath), propertyPath + "." + memberName);
        }

        private static float ReadRequiredFloatMember(JObject obj, string memberName, string propertyPath)
        {
            if (obj[memberName] == null)
            {
                throw new CommandFailureException("PREFAB_FIELD_INVALID", "Could not find required numeric field: " + propertyPath + "." + memberName);
            }

            return ReadFloatMember(obj, memberName, propertyPath);
        }

        private static AnimationCurve ReadAnimationCurve(JToken token, string propertyPath)
        {
            JObject obj = ReadObject(token, propertyPath);
            JArray keys = ReadRequiredArrayMember(obj, "keys", propertyPath);
            var keyframes = new Keyframe[keys.Count];
            for (int index = 0; index < keys.Count; index++)
            {
                string keyPath = propertyPath + ".keys[" + index + "]";
                JObject keyObject = ReadObject(keys[index], keyPath);
                var keyframe = new Keyframe(
                    ReadRequiredFloatMember(keyObject, "time", keyPath),
                    ReadRequiredFloatMember(keyObject, "value", keyPath),
                    ReadRequiredFloatMember(keyObject, "inTangent", keyPath),
                    ReadRequiredFloatMember(keyObject, "outTangent", keyPath));

                if (keyObject["inWeight"] != null)
                {
                    keyframe.inWeight = ReadFloatMember(keyObject, "inWeight", keyPath);
                }

                if (keyObject["outWeight"] != null)
                {
                    keyframe.outWeight = ReadFloatMember(keyObject, "outWeight", keyPath);
                }

                if (keyObject["weightedMode"] != null)
                {
                    keyframe.weightedMode = (WeightedMode)ReadIntMember(keyObject, "weightedMode", keyPath);
                }

                keyframes[index] = keyframe;
            }

            var curve = new AnimationCurve(keyframes)
            {
                preWrapMode = ReadWrapModeMember(obj, "preWrapMode", propertyPath, WrapMode.ClampForever),
                postWrapMode = ReadWrapModeMember(obj, "postWrapMode", propertyPath, WrapMode.ClampForever),
            };
            return curve;
        }

        private static Gradient ReadGradient(JToken token, string propertyPath)
        {
            JObject obj = ReadObject(token, propertyPath);
            JArray colorKeys = ReadRequiredArrayMember(obj, "colorKeys", propertyPath);
            JArray alphaKeys = ReadRequiredArrayMember(obj, "alphaKeys", propertyPath);
            JToken modeToken = ReadRequiredMember(obj, "mode", propertyPath);
            var gradient = new Gradient();
            var serializedColorKeys = new GradientColorKey[colorKeys.Count];
            for (int index = 0; index < colorKeys.Count; index++)
            {
                string colorKeyPath = propertyPath + ".colorKeys[" + index + "]";
                JObject colorKeyObject = ReadObject(colorKeys[index], colorKeyPath);
                serializedColorKeys[index] = new GradientColorKey(
                    ReadColor(ReadRequiredMember(colorKeyObject, "color", colorKeyPath), colorKeyPath + ".color"),
                    ReadRequiredFloatMember(colorKeyObject, "time", colorKeyPath));
            }

            var serializedAlphaKeys = new GradientAlphaKey[alphaKeys.Count];
            for (int index = 0; index < alphaKeys.Count; index++)
            {
                string alphaKeyPath = propertyPath + ".alphaKeys[" + index + "]";
                JObject alphaKeyObject = ReadObject(alphaKeys[index], alphaKeyPath);
                serializedAlphaKeys[index] = new GradientAlphaKey(
                    ReadRequiredFloatMember(alphaKeyObject, "alpha", alphaKeyPath),
                    ReadRequiredFloatMember(alphaKeyObject, "time", alphaKeyPath));
            }

            if (!Enum.TryParse(ReadString(modeToken, propertyPath + ".mode"), true, out GradientMode mode))
            {
                throw new CommandFailureException("PREFAB_FIELD_INVALID", "Unsupported Gradient mode: " + propertyPath);
            }

            gradient.SetKeys(serializedColorKeys, serializedAlphaKeys);
            gradient.mode = mode;
            return gradient;
        }

        private static Hash128 ReadHash128(JToken token, string propertyPath)
        {
            try
            {
                return Hash128.Parse(ReadString(token, propertyPath));
            }
            catch (FormatException ex)
            {
                throw new CommandFailureException(
                    "PREFAB_FIELD_INVALID",
                    "Hash128 hex string is invalid: " + propertyPath + " (" + ex.Message + ")",
                    ex.Message);
            }
        }

        private static void ApplyManagedReference(SerializedProperty property, JToken token, string propertyPath)
        {
            if (token.Type == JTokenType.Null)
            {
                property.managedReferenceValue = null;
                return;
            }

            JObject obj = ReadObject(token, propertyPath);
            JToken typeToken = obj["$type"];
            if (typeToken != null)
            {
                string typeName = ReadString(typeToken, propertyPath + ".$type");
                System.Reflection.Assembly[] assemblies = GetManagedReferenceAssemblies();
                Type managedReferenceType = ResolveManagedReferenceType(typeName, propertyPath, assemblies);
                ValidateManagedReferenceAssignment(property, managedReferenceType, typeName, propertyPath, assemblies);

                bool reuseExistingInstance = false;
                if (property.managedReferenceValue != null)
                {
                    Type currentManagedReferenceType = ResolveCurrentManagedReferenceType(property, assemblies);
                    reuseExistingInstance = currentManagedReferenceType == managedReferenceType;
                }

                if (!reuseExistingInstance)
                {
                    object instance;
                    try
                    {
                        instance = Activator.CreateInstance(managedReferenceType);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        throw new CommandFailureException(
                            "PREFAB_FIELD_INVALID",
                            "Could not create managed reference instance: " + propertyPath + " (" + typeName + ", " + ex.Message + ")",
                            ex.Message);
                    }

                    try
                    {
                        property.managedReferenceValue = instance;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        throw new CommandFailureException(
                            "PREFAB_FIELD_INVALID",
                            "Could not assign managed reference type: " + propertyPath + " (" + typeName + ", " + ex.Message + ")",
                            ex.Message);
                    }
                }
            }
            else if (property.managedReferenceValue == null || string.IsNullOrWhiteSpace(property.managedReferenceFullTypename))
            {
                throw new CommandFailureException("PREFAB_FIELD_INVALID", "Managed reference value is empty, so it cannot be patched without $type: " + propertyPath);
            }

            SerializedProperty refreshedProperty = property.serializedObject.FindProperty(property.propertyPath);
            if (refreshedProperty != null)
            {
                property = refreshedProperty;
            }

            ApplyObjectSkippingMetadata(property, obj, propertyPath);
        }

        private static void ApplyObjectSkippingMetadata(SerializedProperty property, JObject obj, string propertyPath)
        {
            foreach (JProperty child in obj.Properties())
            {
                if (string.Equals(child.Name, "$type", StringComparison.Ordinal))
                {
                    continue;
                }

                SerializedProperty childProperty = property.FindPropertyRelative(child.Name);
                if (childProperty == null)
                {
                    throw new CommandFailureException("PREFAB_FIELD_INVALID", "Could not find serialized field: " + propertyPath + "." + child.Name);
                }

                EnsurePatchableProperty(childProperty, propertyPath + "." + child.Name);
                ApplyToken(childProperty, child.Value, childProperty.propertyPath);
            }
        }

        // NOTE: SerializeReference shared references and cycles are not preserved.
        // If several fields share one instance, the patch splits them into distinct objects.
        private static bool TrySerializeManagedReference(SerializedProperty property, out JToken token)
        {
            token = null;

            object managedReferenceValue = property.managedReferenceValue;
            if (managedReferenceValue == null)
            {
                token = JValue.CreateNull();
                return true;
            }

            Type runtimeType = managedReferenceValue.GetType();
            var jobject = new JObject
            {
                ["$type"] = BuildManagedReferenceTypeName(runtimeType),
            };

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
        }

        private static JObject SerializeAnimationCurve(AnimationCurve curve)
        {
            if (curve == null)
            {
                return new JObject
                {
                    ["keys"] = new JArray(),
                    ["preWrapMode"] = WrapMode.ClampForever.ToString(),
                    ["postWrapMode"] = WrapMode.ClampForever.ToString(),
                };
            }

            var keys = new JArray();
            Keyframe[] curveKeys = curve.keys;
            for (int index = 0; index < curveKeys.Length; index++)
            {
                Keyframe key = curveKeys[index];
                keys.Add(new JObject
                {
                    ["time"] = key.time,
                    ["value"] = key.value,
                    ["inTangent"] = key.inTangent,
                    ["outTangent"] = key.outTangent,
                    ["inWeight"] = key.inWeight,
                    ["outWeight"] = key.outWeight,
                    ["weightedMode"] = (int)key.weightedMode,
                });
            }

            return new JObject
            {
                ["keys"] = keys,
                ["preWrapMode"] = curve.preWrapMode.ToString(),
                ["postWrapMode"] = curve.postWrapMode.ToString(),
            };
        }

        private static JObject SerializeGradient(Gradient gradient)
        {
            if (gradient == null)
            {
                return new JObject
                {
                    ["colorKeys"] = new JArray(),
                    ["alphaKeys"] = new JArray(),
                    ["mode"] = GradientMode.Blend.ToString(),
                };
            }

            var colorKeys = new JArray();
            GradientColorKey[] serializedColorKeys = gradient.colorKeys;
            for (int index = 0; index < serializedColorKeys.Length; index++)
            {
                GradientColorKey colorKey = serializedColorKeys[index];
                colorKeys.Add(new JObject
                {
                    ["color"] = new JObject
                    {
                        ["r"] = colorKey.color.r,
                        ["g"] = colorKey.color.g,
                        ["b"] = colorKey.color.b,
                        ["a"] = colorKey.color.a,
                    },
                    ["time"] = colorKey.time,
                });
            }

            var alphaKeys = new JArray();
            GradientAlphaKey[] serializedAlphaKeys = gradient.alphaKeys;
            for (int index = 0; index < serializedAlphaKeys.Length; index++)
            {
                GradientAlphaKey alphaKey = serializedAlphaKeys[index];
                alphaKeys.Add(new JObject
                {
                    ["alpha"] = alphaKey.alpha,
                    ["time"] = alphaKey.time,
                });
            }

            return new JObject
            {
                ["colorKeys"] = colorKeys,
                ["alphaKeys"] = alphaKeys,
                ["mode"] = gradient.mode.ToString(),
            };
        }

        private static string BuildManagedReferenceTypeName(Type type)
        {
            string fullName = type.FullName;
            if (string.IsNullOrWhiteSpace(fullName))
            {
                throw new CommandFailureException("PREFAB_FIELD_INVALID", "Could not build the managed reference type name: " + type.Name);
            }

            return fullName + ", " + type.Assembly.GetName().Name;
        }

        private static Type ResolveManagedReferenceType(string typeName, string propertyPath)
        {
            return ResolveManagedReferenceType(typeName, propertyPath, GetManagedReferenceAssemblies());
        }

        private static Type ResolveManagedReferenceType(string typeName, string propertyPath, System.Reflection.Assembly[] assemblies)
        {
            Type resolvedType = TryResolveManagedReferenceType(typeName, assemblies);
            if (resolvedType == null)
            {
                resolvedType = TryResolveManagedReferenceType(typeName, GetManagedReferenceAssemblies(refresh: true));
            }

            if (resolvedType != null)
            {
                ValidateManagedReferenceType(resolvedType, typeName, propertyPath);
                return resolvedType;
            }

            throw new CommandFailureException("PREFAB_FIELD_INVALID", "Could not find managed reference type: " + propertyPath + " (" + typeName + ")");
        }

        private static Type ResolveCurrentManagedReferenceType(SerializedProperty property, System.Reflection.Assembly[] assemblies)
        {
            Type resolvedType = TryResolveManagedReferenceType(property.managedReferenceFullTypename, assemblies);
            if (resolvedType != null)
            {
                return resolvedType;
            }

            object managedReferenceValue = property.managedReferenceValue;
            return managedReferenceValue == null ? null : managedReferenceValue.GetType();
        }

        private static Type TryResolveManagedReferenceType(string typeName, System.Reflection.Assembly[] assemblies)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            string normalizedTypeName = ProtocolHelpers.NormalizeManagedReferenceTypeNameForClrLookup(typeName);
            Type resolvedType = Type.GetType(normalizedTypeName, false);
            if (resolvedType != null)
            {
                return resolvedType;
            }

            if (!TryParseManagedReferenceTypeName(normalizedTypeName, out string assemblyName, out string fullTypeName))
            {
                return null;
            }

            for (int index = 0; index < assemblies.Length; index++)
            {
                System.Reflection.Assembly assembly = assemblies[index];
                if (!string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal))
                {
                    continue;
                }

                resolvedType = assembly.GetType(fullTypeName, false);
                if (resolvedType != null)
                {
                    return resolvedType;
                }
            }

            return null;
        }

        private static bool TryParseManagedReferenceTypeName(string typeName, out string assemblyName, out string fullTypeName)
        {
            assemblyName = null;
            fullTypeName = null;

            int commaIndex = -1;
            int bracketDepth = 0;
            for (int i = 0; i < typeName.Length; i++)
            {
                char currentCharacter = typeName[i];
                if (currentCharacter == '[')
                {
                    bracketDepth++;
                }
                else if (currentCharacter == ']')
                {
                    bracketDepth--;
                }
                else if (currentCharacter == ',' && bracketDepth == 0)
                {
                    commaIndex = i;
                    break;
                }
            }

            if (commaIndex >= 0)
            {
                fullTypeName = typeName.Substring(0, commaIndex).Trim();
                assemblyName = typeName.Substring(commaIndex + 1).Trim();
                return !string.IsNullOrWhiteSpace(assemblyName) && !string.IsNullOrWhiteSpace(fullTypeName);
            }

            int spaceIndex = typeName.IndexOf(' ');
            if (spaceIndex > 0)
            {
                assemblyName = typeName.Substring(0, spaceIndex).Trim();
                fullTypeName = typeName.Substring(spaceIndex + 1).Trim();
                return !string.IsNullOrWhiteSpace(assemblyName) && !string.IsNullOrWhiteSpace(fullTypeName);
            }

            return false;
        }

        private static System.Reflection.Assembly[] GetManagedReferenceAssemblies(bool refresh = false)
        {
            if (refresh || _managedReferenceAssemblies.Length == 0)
            {
                _managedReferenceAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            }

            return _managedReferenceAssemblies;
        }

        private static void ValidateManagedReferenceType(Type type, string typeName, string propertyPath)
        {
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                throw new CommandFailureException(
                    "PREFAB_FIELD_INVALID",
                    "SerializeReference does not support types derived from UnityEngine.Object: " + propertyPath + " (" + typeName + ")");
            }

            if (type.IsAbstract || type.IsInterface)
            {
                throw new CommandFailureException(
                    "PREFAB_FIELD_INVALID",
                    "Managed reference type cannot be an abstract class or interface: " + propertyPath + " (" + typeName + ")");
            }

            if (!type.IsValueType && type.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new CommandFailureException(
                    "PREFAB_FIELD_INVALID",
                    "Managed reference type needs a public parameterless constructor: " + propertyPath + " (" + typeName + ")");
            }
        }

        private static void ValidateManagedReferenceAssignment(
            SerializedProperty property,
            Type type,
            string typeName,
            string propertyPath,
            System.Reflection.Assembly[] assemblies)
        {
            string declaredFieldTypeName = property.managedReferenceFieldTypename;
            if (string.IsNullOrWhiteSpace(declaredFieldTypeName))
            {
                return;
            }

            Type declaredFieldType = TryResolveManagedReferenceType(declaredFieldTypeName, assemblies);
            if (declaredFieldType == null)
            {
                declaredFieldType = TryResolveManagedReferenceType(declaredFieldTypeName, GetManagedReferenceAssemblies(refresh: true));
            }

            if (declaredFieldType == null)
            {
                throw new CommandFailureException(
                    "PREFAB_FIELD_INVALID",
                    "Could not find the managed reference declared type: " + propertyPath + " (" + declaredFieldTypeName + ")");
            }

            if (!declaredFieldType.IsAssignableFrom(type))
            {
                throw new CommandFailureException(
                    "PREFAB_FIELD_INVALID",
                    "Managed reference type is not compatible with the field's declared type: "
                    + propertyPath
                    + " ($type: "
                    + typeName
                    + ", declared: "
                    + declaredFieldTypeName
                    + ")");
            }
        }

        private static WrapMode ReadWrapModeMember(JObject obj, string memberName, string propertyPath, WrapMode fallback)
        {
            JToken member = obj[memberName];
            if (member == null)
            {
                return fallback;
            }

            string wrapModeName = ReadString(member, propertyPath + "." + memberName);
            if (Enum.TryParse(wrapModeName, true, out WrapMode wrapMode))
            {
                return wrapMode;
            }

            throw new CommandFailureException(
                "PREFAB_FIELD_INVALID",
                "Unsupported AnimationCurve wrap mode: " + propertyPath + "." + memberName + " (" + wrapModeName + ")");
        }
    }
}
