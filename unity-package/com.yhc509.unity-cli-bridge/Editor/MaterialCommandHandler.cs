#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;
using ShaderPropertyType = UnityEngine.Rendering.ShaderPropertyType;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed class MaterialCommandHandler
    {
        public bool CanHandle(string command)
        {
            return string.Equals(command, ProtocolConstants.CommandMaterialInfo, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandMaterialSet, StringComparison.Ordinal);
        }

        public string Handle(string command, string argumentsJson)
        {
            if (string.Equals(command, ProtocolConstants.CommandMaterialInfo, StringComparison.Ordinal))
            {
                return HandleInfo(argumentsJson);
            }

            if (string.Equals(command, ProtocolConstants.CommandMaterialSet, StringComparison.Ordinal))
            {
                return HandleSet(argumentsJson);
            }

            throw new InvalidOperationException("Unsupported material command: " + command);
        }

        private string HandleInfo(string argumentsJson)
        {
            MaterialInfoArgs args = ProtocolJson.Deserialize<MaterialInfoArgs>(argumentsJson) ?? new MaterialInfoArgs();
            Material material = LoadMaterial(args.path);

            var properties = new List<MaterialPropertyRecord>();
            Shader shader = material.shader;
            int propertyCount = shader.GetPropertyCount();

            for (int i = 0; i < propertyCount; i++)
            {
                string propertyName = shader.GetPropertyName(i);
                ShaderPropertyType propertyType = shader.GetPropertyType(i);
                if (args.omitDefaults && ShouldOmitInfoProperty(material, shader, i, propertyName, propertyType))
                {
                    continue;
                }

                string typeLabel;
                string valueLabel;

                switch (propertyType)
                {
                    case ShaderPropertyType.Color:
                        typeLabel = "Color";
                        Color color = material.GetColor(propertyName);
                        valueLabel = $"{color.r},{color.g},{color.b},{color.a}";
                        break;
                    case ShaderPropertyType.Vector:
                        typeLabel = "Vector";
                        Vector4 vector = material.GetVector(propertyName);
                        valueLabel = $"{vector.x},{vector.y},{vector.z},{vector.w}";
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        typeLabel = "Float";
                        valueLabel = material.GetFloat(propertyName).ToString(CultureInfo.InvariantCulture);
                        break;
                    case ShaderPropertyType.Texture:
                        typeLabel = "Texture";
                        Texture? texture = material.GetTexture(propertyName);
                        valueLabel = texture != null ? AssetDatabase.GetAssetPath(texture) : "(none)";
                        break;
                    case ShaderPropertyType.Int:
                        typeLabel = "Int";
                        valueLabel = material.GetInt(propertyName).ToString(CultureInfo.InvariantCulture);
                        break;
                    default:
                        typeLabel = propertyType.ToString();
                        valueLabel = "(unknown)";
                        break;
                }

                properties.Add(new MaterialPropertyRecord
                {
                    name = propertyName,
                    type = typeLabel,
                    value = valueLabel,
                });
            }

            return ProtocolJson.Serialize(new MaterialInfoPayload
            {
                path = args.path,
                shader = shader.name,
                properties = properties.ToArray(),
            });
        }

        private static bool ShouldOmitInfoProperty(
            Material material,
            Shader shader,
            int propertyIndex,
            string propertyName,
            ShaderPropertyType propertyType)
        {
            switch (propertyType)
            {
                case ShaderPropertyType.Color:
                {
                    Color color = material.GetColor(propertyName);
                    Vector4 defaultValue = shader.GetPropertyDefaultVectorValue(propertyIndex);
                    return color.r == defaultValue.x
                        && color.g == defaultValue.y
                        && color.b == defaultValue.z
                        && color.a == defaultValue.w;
                }
                case ShaderPropertyType.Vector:
                {
                    Vector4 vector = material.GetVector(propertyName);
                    Vector4 defaultValue = shader.GetPropertyDefaultVectorValue(propertyIndex);
                    return vector.x == defaultValue.x
                        && vector.y == defaultValue.y
                        && vector.z == defaultValue.z
                        && vector.w == defaultValue.w;
                }
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    return material.GetFloat(propertyName) == shader.GetPropertyDefaultFloatValue(propertyIndex);
                case ShaderPropertyType.Texture:
                    return IsDefaultTexture(material.GetTexture(propertyName), shader.GetPropertyTextureDefaultName(propertyIndex));
                case ShaderPropertyType.Int:
#if UNITY_2022_1_OR_NEWER
                    return material.GetInt(propertyName) == shader.GetPropertyDefaultIntValue(propertyIndex);
#else
                    return false;
#endif
                default:
                    return false;
            }
        }

        private static bool IsDefaultTexture(Texture? texture, string defaultTextureName)
        {
            if (string.IsNullOrWhiteSpace(defaultTextureName))
            {
                return texture == null;
            }

            if (texture == null)
            {
                return false;
            }

            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            return string.Equals(NormalizeBuiltinTextureName(texture.name), NormalizeBuiltinTextureName(defaultTextureName), StringComparison.Ordinal);
        }

        private static string NormalizeBuiltinTextureName(string value)
        {
            string normalized = value
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .Trim();

            if (normalized.StartsWith("default", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("default".Length);
            }

            return normalized.ToLowerInvariant();
        }

        private string HandleSet(string argumentsJson)
        {
            MaterialSetArgs args = ProtocolJson.Deserialize<MaterialSetArgs>(argumentsJson) ?? new MaterialSetArgs();
            Material material = LoadMaterial(args.path);

            string propertyName;
            string previousValue;
            string newValue;

            if (!string.IsNullOrWhiteSpace(args.texture) && !string.IsNullOrWhiteSpace(args.textureAsset))
            {
                propertyName = args.texture!;
                ShaderPropertyType propertyType = RequirePropertyType(material.shader, propertyName, "texture");
                if (propertyType != ShaderPropertyType.Texture)
                {
                    throw new CommandFailureException("UNSUPPORTED_TYPE", $"Property type cannot be set as a texture: {propertyType}", false, null);
                }

                Texture? oldTexture = material.GetTexture(propertyName);
                previousValue = oldTexture != null ? AssetDatabase.GetAssetPath(oldTexture) : "(none)";

                Texture? newTexture = AssetDatabase.LoadAssetAtPath<Texture>(args.textureAsset!);
                if (newTexture == null)
                {
                    throw new CommandFailureException("TEXTURE_NOT_FOUND", $"Could not find texture asset: {args.textureAsset}", false, null);
                }

                material.SetTexture(propertyName, newTexture);
                newValue = args.textureAsset!;
            }
            else if (!string.IsNullOrWhiteSpace(args.property) && !string.IsNullOrWhiteSpace(args.value))
            {
                propertyName = args.property!;
                ShaderPropertyType propertyType = RequirePropertyType(material.shader, propertyName, "property");

                switch (propertyType)
                {
                    case ShaderPropertyType.Color:
                    {
                        Color oldColor = material.GetColor(propertyName);
                        previousValue = $"{oldColor.r},{oldColor.g},{oldColor.b},{oldColor.a}";
                        Color newColor = ParseColor(args.value!);
                        material.SetColor(propertyName, newColor);
                        newValue = args.value!;
                        break;
                    }
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    {
                        float oldFloat = material.GetFloat(propertyName);
                        previousValue = oldFloat.ToString(CultureInfo.InvariantCulture);
                        if (!float.TryParse(args.value!, NumberStyles.Float, CultureInfo.InvariantCulture, out float newFloat))
                        {
                            throw new CommandFailureException("INVALID_VALUE", $"Could not parse float value: {args.value}", false, null);
                        }

                        material.SetFloat(propertyName, newFloat);
                        newValue = args.value!;
                        break;
                    }
                    case ShaderPropertyType.Vector:
                    {
                        Vector4 oldVector = material.GetVector(propertyName);
                        previousValue = $"{oldVector.x},{oldVector.y},{oldVector.z},{oldVector.w}";
                        Vector4 newVector = ParseVector4(args.value!);
                        material.SetVector(propertyName, newVector);
                        newValue = args.value!;
                        break;
                    }
                    case ShaderPropertyType.Int:
                    {
                        int oldInt = material.GetInt(propertyName);
                        previousValue = oldInt.ToString(CultureInfo.InvariantCulture);
                        if (!int.TryParse(args.value!, NumberStyles.Integer, CultureInfo.InvariantCulture, out int newInt))
                        {
                            throw new CommandFailureException("INVALID_VALUE", $"Could not parse int value: {args.value}", false, null);
                        }

                        material.SetInt(propertyName, newInt);
                        newValue = args.value!;
                        break;
                    }
                    default:
                        throw new CommandFailureException("UNSUPPORTED_TYPE", $"This property type cannot be set directly: {propertyType}", false, null);
                }
            }
            else
            {
                throw new CommandFailureException("INVALID_ARGS", "--property+--value or --texture+--asset is required.", false, null);
            }

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            return ProtocolJson.Serialize(new MaterialSetPayload
            {
                path = args.path,
                property = propertyName,
                previousValue = previousValue,
                newValue = newValue,
            });
        }

        private static Material LoadMaterial(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new CommandFailureException("INVALID_ARGS", "Material path is empty.", false, null);
            }

            Material? material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                throw new CommandFailureException("MATERIAL_NOT_FOUND", $"Could not find material: {path}", false, null);
            }

            return material;
        }

        private static ShaderPropertyType RequirePropertyType(Shader shader, string propertyName, string propertyLabel)
        {
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (string.Equals(shader.GetPropertyName(i), propertyName, StringComparison.Ordinal))
                {
                    return shader.GetPropertyType(i);
                }
            }

            throw new CommandFailureException("PROPERTY_NOT_FOUND", $"Could not find shader {propertyLabel}: {propertyName}", false, null);
        }

        private static Color ParseColor(string csv)
        {
            string[] parts = csv.Split(',');
            if (parts.Length < 3 || parts.Length > 4)
            {
                throw new CommandFailureException("INVALID_VALUE", "Color must be `r,g,b` or `r,g,b,a`.", false, null);
            }

            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
                || !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
                || !float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
            {
                throw new CommandFailureException("INVALID_VALUE", "Could not parse color value.", false, null);
            }

            float a = 1f;
            if (parts.Length == 4 && !float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a))
            {
                throw new CommandFailureException("INVALID_VALUE", "Could not parse color alpha.", false, null);
            }

            return new Color(r, g, b, a);
        }

        private static Vector4 ParseVector4(string csv)
        {
            string[] parts = csv.Split(',');
            if (parts.Length < 2 || parts.Length > 4)
            {
                throw new CommandFailureException("INVALID_VALUE", "Vector must be `x,y[,z[,w]]`.", false, null);
            }

            float[] values = new float[4];
            for (int i = 0; i < parts.Length; i++)
            {
                string component = parts[i].Trim();
                if (!float.TryParse(component, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    throw new CommandFailureException("INVALID_VALUE", $"Could not parse vector component: {component}", false, null);
                }
            }

            return new Vector4(values[0], values[1], values[2], values[3]);
        }
    }
}
