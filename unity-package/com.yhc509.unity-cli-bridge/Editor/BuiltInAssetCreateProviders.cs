using System;
using System.IO;
using Newtonsoft.Json;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCliBridge.Bridge.Editor
{
    internal static partial class BuiltInAssetCreateProviders
    {
        public static IAssetCreateProvider[] CreateAll()
        {
            return new IAssetCreateProvider[]
            {
                new MaterialAssetCreateProvider(),
                new PhysicsMaterialAssetCreateProvider(),
                new PhysicsMaterial2DAssetCreateProvider(),
                new AnimatorControllerAssetCreateProvider(),
                new AnimatorOverrideControllerAssetCreateProvider(),
                new AnimationClipAssetCreateProvider(),
                new InputActionsAssetCreateProvider(),
                new SceneAssetCreateProvider(),
                new PrefabAssetCreateProvider(),
                new RenderTextureAssetCreateProvider(),
                new AvatarMaskAssetCreateProvider(),
                new VolumeProfileAssetCreateProvider(),
                new ScriptableObjectAssetCreateProvider(),
            };
        }

        private sealed class MaterialAssetCreateProvider : IAssetCreateProvider
        {
            public AssetCreateTypeDescriptor Describe()
            {
                return BuiltInAssetCreateCatalog.GetDescriptor("material");
            }

            public AssetCreateArtifact Create(AssetCreateRequest request)
            {
                MaterialCreateOptions options = request.GetOptions<MaterialCreateOptions>();
                Shader shader = ResolveShader(options.Shader);
                var material = new Material(shader)
                {
                    name = Path.GetFileNameWithoutExtension(request.AssetPath),
                };

                return new AssetCreateArtifact(
                    delegate { AssetDatabase.CreateAsset(material, request.AssetPath); },
                    typeof(Material));
            }
        }

        private sealed class PhysicsMaterialAssetCreateProvider : IAssetCreateProvider
        {
            public AssetCreateTypeDescriptor Describe()
            {
                return BuiltInAssetCreateCatalog.GetDescriptor("physics-material");
            }

            public AssetCreateArtifact Create(AssetCreateRequest request)
            {
#if UNITY_6000_0_OR_NEWER
                var asset = new PhysicsMaterial(Path.GetFileNameWithoutExtension(request.AssetPath));
                return new AssetCreateArtifact(
                    delegate { AssetDatabase.CreateAsset(asset, request.AssetPath); },
                    typeof(PhysicsMaterial));
#else
                var asset = new PhysicMaterial(Path.GetFileNameWithoutExtension(request.AssetPath));
                return new AssetCreateArtifact(
                    delegate { AssetDatabase.CreateAsset(asset, request.AssetPath); },
                    typeof(PhysicMaterial));
#endif
            }
        }

        private sealed class PhysicsMaterial2DAssetCreateProvider : IAssetCreateProvider
        {
            public AssetCreateTypeDescriptor Describe()
            {
                return BuiltInAssetCreateCatalog.GetDescriptor("physics-material-2d");
            }

            public AssetCreateArtifact Create(AssetCreateRequest request)
            {
                var asset = new PhysicsMaterial2D(Path.GetFileNameWithoutExtension(request.AssetPath));
                return new AssetCreateArtifact(
                    delegate { AssetDatabase.CreateAsset(asset, request.AssetPath); },
                    typeof(PhysicsMaterial2D));
            }
        }

        private sealed class AnimatorControllerAssetCreateProvider : IAssetCreateProvider
        {
            public AssetCreateTypeDescriptor Describe()
            {
                return BuiltInAssetCreateCatalog.GetDescriptor("animator-controller");
            }

            public AssetCreateArtifact Create(AssetCreateRequest request)
            {
                return new AssetCreateArtifact(
                    delegate { AnimatorController.CreateAnimatorControllerAtPath(request.AssetPath); },
                    typeof(AnimatorController));
            }
        }

        private sealed class AnimationClipAssetCreateProvider : IAssetCreateProvider
        {
            public AssetCreateTypeDescriptor Describe()
            {
                return BuiltInAssetCreateCatalog.GetDescriptor("animation-clip");
            }

            public AssetCreateArtifact Create(AssetCreateRequest request)
            {
                AnimationClipCreateOptions options = request.GetOptions<AnimationClipCreateOptions>();
                var clip = new AnimationClip
                {
                    name = Path.GetFileNameWithoutExtension(request.AssetPath),
                    legacy = options.IsLegacy,
                };

                return new AssetCreateArtifact(
                    delegate { AssetDatabase.CreateAsset(clip, request.AssetPath); },
                    typeof(AnimationClip));
            }
        }

        private sealed class SceneAssetCreateProvider : IAssetCreateProvider
        {
            public AssetCreateTypeDescriptor Describe()
            {
                return BuiltInAssetCreateCatalog.GetDescriptor("scene");
            }

            public AssetCreateArtifact Create(AssetCreateRequest request)
            {
                return new AssetCreateArtifact(
                    delegate
                    {
                        Scene activeScene = SceneManager.GetActiveScene();
                        bool canCreateSingle = SceneManager.sceneCount == 1
                            && string.IsNullOrWhiteSpace(activeScene.path)
                            && !activeScene.isDirty;
                        Scene scene = EditorSceneManager.NewScene(
                            NewSceneSetup.EmptyScene,
                            canCreateSingle ? NewSceneMode.Single : NewSceneMode.Additive);
                        try
                        {
                            if (!EditorSceneManager.SaveScene(scene, request.AssetPath, false))
                            {
                                throw new CommandFailureException("ASSET_CREATE_FAILED", "Could not save scene: " + request.AssetPath);
                            }
                        }
                        finally
                        {
                            if (!canCreateSingle)
                            {
                                EditorSceneManager.CloseScene(scene, true);
                            }
                        }
                    },
                    typeof(SceneAsset));
            }
        }

        private sealed class PrefabAssetCreateProvider : IAssetCreateProvider
        {
            public AssetCreateTypeDescriptor Describe()
            {
                return BuiltInAssetCreateCatalog.GetDescriptor("prefab");
            }

            public AssetCreateArtifact Create(AssetCreateRequest request)
            {
                PrefabCreateOptions options = request.GetOptions<PrefabCreateOptions>();
                string rootName = string.IsNullOrWhiteSpace(options.RootName)
                    ? Path.GetFileNameWithoutExtension(request.AssetPath)
                    : options.RootName.Trim();

                if (string.IsNullOrWhiteSpace(rootName))
                {
                    throw new CommandFailureException("ASSET_OPTION_INVALID", "Prefab root name is empty.");
                }

                return new AssetCreateArtifact(
                    delegate
                    {
                        var root = new GameObject(rootName);
                        try
                        {
                            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, request.AssetPath);
                            if (saved == null)
                            {
                                throw new CommandFailureException("ASSET_CREATE_FAILED", "Could not save prefab: " + request.AssetPath);
                            }
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(root);
                        }
                    },
                    typeof(GameObject));
            }
        }

        private sealed class RenderTextureAssetCreateProvider : IAssetCreateProvider
        {
            public AssetCreateTypeDescriptor Describe()
            {
                return BuiltInAssetCreateCatalog.GetDescriptor("render-texture");
            }

            public AssetCreateArtifact Create(AssetCreateRequest request)
            {
                RenderTextureCreateOptions options = request.GetOptions<RenderTextureCreateOptions>();
                int width = options.Width <= 0 ? 1024 : options.Width;
                int height = options.Height <= 0 ? 1024 : options.Height;
                int depth = options.Depth < 0 ? 24 : options.Depth;

                var asset = new RenderTexture(width, height, depth)
                {
                    name = Path.GetFileNameWithoutExtension(request.AssetPath),
                };

                return new AssetCreateArtifact(
                    delegate { AssetDatabase.CreateAsset(asset, request.AssetPath); },
                    typeof(RenderTexture));
            }
        }

        private sealed class AvatarMaskAssetCreateProvider : IAssetCreateProvider
        {
            public AssetCreateTypeDescriptor Describe()
            {
                return BuiltInAssetCreateCatalog.GetDescriptor("avatar-mask");
            }

            public AssetCreateArtifact Create(AssetCreateRequest request)
            {
                var asset = new AvatarMask
                {
                    name = Path.GetFileNameWithoutExtension(request.AssetPath),
                };

                return new AssetCreateArtifact(
                    delegate { AssetDatabase.CreateAsset(asset, request.AssetPath); },
                    typeof(AvatarMask));
            }
        }

        private static Shader ResolveShader(string shaderPath)
        {
            if (!string.IsNullOrWhiteSpace(shaderPath))
            {
                Shader shader = Shader.Find(shaderPath.Trim());
                if (shader == null)
                {
                    throw new CommandFailureException("ASSET_OPTION_INVALID", "Could not find shader: " + shaderPath.Trim());
                }

                return shader;
            }

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLit != null)
            {
                return urpLit;
            }

            Shader standard = Shader.Find("Standard");
            if (standard != null)
            {
                return standard;
            }

            throw new CommandFailureException("ASSET_DEPENDENCY_MISSING", "Could not find the default material shader.");
        }

        [Serializable]
        private sealed class MaterialCreateOptions
        {
            public string Shader { get; set; } = string.Empty;
        }

        [Serializable]
        private sealed class AnimationClipCreateOptions
        {
            [JsonProperty("legacy")]
            public bool IsLegacy { get; set; }
        }

        [Serializable]
        private sealed class PrefabCreateOptions
        {
            public string RootName { get; set; } = string.Empty;
        }

        [Serializable]
        private sealed class RenderTextureCreateOptions
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public int Depth { get; set; } = 24;
        }
    }
}
