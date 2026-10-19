#nullable enable
using System;
using System.Collections.Generic;

namespace UnityCli.Protocol
{
    public static class BuiltInAssetCreateCatalog
    {
        private static readonly AssetCreateTypeDescriptor[] _descriptors =
        {
            new AssetCreateTypeDescriptor
            {
                typeId = "material",
                displayName = "Material",
                defaultExtension = ".mat",
                origin = "builtin",
                optionalOptions = new[] { "--shader" },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "physics-material",
                displayName = "Physics Material",
                defaultExtension = ".physicMaterial",
                origin = "builtin",
                aliases = new[] { "physic-material" },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "physics-material-2d",
                displayName = "Physics Material 2D",
                defaultExtension = ".physicsMaterial2D",
                origin = "builtin",
                aliases = new[] { "physic-material-2d" },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "animator-controller",
                displayName = "Animator Controller",
                defaultExtension = ".controller",
                origin = "builtin",
                aliases = new[] { "controller" },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "animator-override-controller",
                displayName = "Animator Override Controller",
                defaultExtension = ".overrideController",
                origin = "builtin",
                aliases = new[] { "override-controller" },
                requiredOptions = new[] { "--base-controller" },
                notes = new[] { "Requires an existing RuntimeAnimatorController asset path via --base-controller." },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "animation-clip",
                displayName = "Animation Clip",
                defaultExtension = ".anim",
                origin = "builtin",
                aliases = new[] { "clip" },
                optionalOptions = new[] { "--legacy" },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "input-actions",
                displayName = "Input Actions",
                defaultExtension = ".inputactions",
                origin = "builtin",
                aliases = new[] { "inputactions" },
                optionalOptions = new[] { "--initial-map" },
                notes = new[] { "Requires the Unity Input System package in the target project." },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "scene",
                displayName = "Scene",
                defaultExtension = ".unity",
                origin = "builtin",
                notes = new[] { "Creates a saved empty scene asset." },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "prefab",
                displayName = "Prefab",
                defaultExtension = ".prefab",
                origin = "builtin",
                optionalOptions = new[] { "--root-name" },
                notes = new[] { "Creates an empty prefab root. Use prefab create for structured authoring." },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "render-texture",
                displayName = "Render Texture",
                defaultExtension = ".renderTexture",
                origin = "builtin",
                aliases = new[] { "rendertexture" },
                optionalOptions = new[] { "--width", "--height", "--depth" },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "avatar-mask",
                displayName = "Avatar Mask",
                defaultExtension = ".mask",
                origin = "builtin",
                aliases = new[] { "avatarmask" },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "volume-profile",
                displayName = "Volume Profile",
                defaultExtension = ".asset",
                origin = "builtin",
                aliases = new[] { "volumeprofile" },
                notes = new[] { "Requires a render pipeline package that exposes UnityEngine.Rendering.VolumeProfile." },
            },
            new AssetCreateTypeDescriptor
            {
                typeId = "scriptable-object",
                displayName = "ScriptableObject",
                defaultExtension = ".asset",
                origin = "builtin",
                aliases = new[] { "scriptableobject" },
                supportsDataPatch = true,
                optionalOptions = new[] { "--script", "--type-name", "--data-json" },
                notes = new[] { "Requires either --script or --type-name to resolve the ScriptableObject type." },
            },
        };

        private static readonly AssetCreateTypeDescriptor[] _descriptorCache = CloneDescriptors(_descriptors);
        private static readonly Dictionary<string, string> _typeMap = BuildTypeMap();

        public static AssetCreateTypeDescriptor[] GetDescriptors()
        {
            return CloneDescriptors(_descriptorCache);
        }

        public static AssetCreateTypeDescriptor GetDescriptor(string typeId)
        {
            if (!TryGetDescriptor(typeId, out AssetCreateTypeDescriptor? descriptor) || descriptor is null)
            {
                throw new InvalidOperationException("Unsupported built-in asset create type: " + typeId);
            }

            return descriptor;
        }

        public static bool TryGetDescriptor(string typeId, out AssetCreateTypeDescriptor? descriptor)
        {
            descriptor = null;
            if (!TryNormalizeTypeId(typeId, out string normalizedType))
            {
                return false;
            }

            foreach (AssetCreateTypeDescriptor candidate in _descriptors)
            {
                if (string.Equals(candidate.typeId, normalizedType, StringComparison.Ordinal))
                {
                    descriptor = CloneDescriptor(candidate);
                    return true;
                }
            }

            return false;
        }

        public static bool TryNormalizeTypeId(string rawValue, out string normalizedType)
        {
            normalizedType = string.Empty;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            if (!_typeMap.TryGetValue(rawValue.Trim().ToLowerInvariant(), out string? mappedType)
                || string.IsNullOrWhiteSpace(mappedType))
            {
                return false;
            }

            normalizedType = mappedType;
            return true;
        }

        public static string NormalizeTypeId(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return string.Empty;
            }

            return TryNormalizeTypeId(rawValue, out string normalizedType)
                ? normalizedType
                : rawValue.Trim().ToLowerInvariant();
        }

        public static bool IsBuiltInType(string? typeId)
        {
            return !string.IsNullOrWhiteSpace(typeId) && TryNormalizeTypeId(typeId, out _);
        }

        private static Dictionary<string, string> BuildTypeMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetCreateTypeDescriptor descriptor in _descriptors)
            {
                string normalizedType = descriptor.typeId.Trim().ToLowerInvariant();
                map[normalizedType] = normalizedType;
                foreach (string alias in descriptor.aliases)
                {
                    string normalizedAlias = alias.Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(normalizedAlias))
                    {
                        map[normalizedAlias] = normalizedType;
                    }
                }
            }

            return map;
        }

        private static AssetCreateTypeDescriptor CloneDescriptor(AssetCreateTypeDescriptor descriptor)
        {
            return new AssetCreateTypeDescriptor
            {
                typeId = descriptor.typeId,
                displayName = descriptor.displayName,
                defaultExtension = descriptor.defaultExtension,
                origin = descriptor.origin,
                supportsDataPatch = descriptor.supportsDataPatch,
                requiredOptions = (string[])descriptor.requiredOptions.Clone(),
                optionalOptions = (string[])descriptor.optionalOptions.Clone(),
                aliases = (string[])descriptor.aliases.Clone(),
                notes = (string[])descriptor.notes.Clone(),
            };
        }

        private static AssetCreateTypeDescriptor[] CloneDescriptors(AssetCreateTypeDescriptor[] descriptors)
        {
            var clonedDescriptors = new AssetCreateTypeDescriptor[descriptors.Length];
            for (int index = 0; index < descriptors.Length; index++)
            {
                clonedDescriptors[index] = CloneDescriptor(descriptors[index]);
            }

            return clonedDescriptors;
        }
    }
}
