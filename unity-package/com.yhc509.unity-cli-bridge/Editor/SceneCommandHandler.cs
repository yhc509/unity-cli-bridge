#nullable enable
using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class SceneCommandHandler
    {
        public bool CanHandle(string command)
        {
            return ProtocolHelpers.IsSceneCommand(command);
        }

        public string Handle(string command, string argumentsJson)
        {
            switch (command)
            {
                case ProtocolConstants.CommandSceneOpen:
                    return HandleOpen(argumentsJson);
                case ProtocolConstants.CommandSceneInspect:
                    return HandleInspect(argumentsJson);
                case ProtocolConstants.CommandScenePatch:
                    return HandlePatch(argumentsJson);
                case ProtocolConstants.CommandSceneSetTransform:
                    return HandleSetTransform(argumentsJson);
                case ProtocolConstants.CommandSceneAssignMaterial:
                    return HandleAssignMaterial(argumentsJson);
                case ProtocolConstants.CommandSceneListComponents:
                    return HandleListComponents(argumentsJson);
                default:
                    throw new InvalidOperationException("Unsupported scene command: " + command);
            }
        }

        private static string HandleOpen(string argumentsJson)
        {
            SceneOpenArgs args = ProtocolJson.Deserialize<SceneOpenArgs>(argumentsJson) ?? new SceneOpenArgs();
            string path = RequireExistingScenePath(args.path, "scene-open");

            PrepareForOpen(args.force);

            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new CommandFailureException("SCENE_OPEN_FAILED", "Could not open scene: " + path);
            }

            SceneManager.SetActiveScene(scene);
            return ProtocolJson.Serialize(new SceneOpenPayload
            {
                asset = AssetCommandSupport.BuildRecordFromPath(path),
                activeScenePath = scene.path,
                opened = true,
            });
        }

        private static string HandleInspect(string argumentsJson)
        {
            JObject arguments = InspectorMutationReaderUtility.ParseArgumentsObject(argumentsJson);
            SceneInspectArgs args = InspectorMutationReaderUtility.DeserializeArguments<SceneInspectArgs>(arguments);
            int? maxDepth = args.maxDepth ?? InspectorMutationReaderUtility.ParseOptionalMaxDepth(arguments, "SCENE_INSPECT_INVALID");
            string path = RequireExistingScenePath(args.path, "scene-inspect");
            if (maxDepth.HasValue && maxDepth.Value <= 0)
            {
                throw new CommandFailureException("SCENE_INSPECT_INVALID", "`--max-depth` must be an integer >= 1.");
            }

            return WithLoadedScene(path, "scene-inspect", scene =>
            {
                GameObject? targetRoot = SceneInspector.ResolveInspectRoot(scene, args.node, "scene-inspect");
                return SceneInspector.BuildInspectPayload(path, scene, args.withValues, maxDepth, args.omitDefaults, EditorSceneManager.GetActiveScene().path, targetRoot);
            });
        }

        private static string HandlePatch(string argumentsJson)
        {
            ScenePatchArgs args = ProtocolJson.Deserialize<ScenePatchArgs>(argumentsJson) ?? new ScenePatchArgs();
            string path = RequireExistingScenePath(args.path, "scene-patch");
            EnsureScenePatchTargetClean(path, "scene patch");

            ScenePatchSpec spec = DeserializeSpec<ScenePatchSpec>(args.specJson, "scene-patch");
            ValidateVersion(spec.Version, "scene-patch");
            if (spec.Operations == null || spec.Operations.Length == 0)
            {
                throw new CommandFailureException("SCENE_SPEC_INVALID", "`operations` is empty.");
            }

            NormalizeOperationNames(spec.Operations);
            if (HasDestructiveOperation(spec) && !args.force)
            {
                throw new CommandFailureException(ProtocolConstants.ErrorSceneForceRequired, "--force is required to use `delete-gameobject` or `remove-component`.");
            }

            LoadedSceneSnapshot loadedSceneSnapshot = CaptureLoadedSceneSnapshot(path);
            try
            {
                return AssetBackupTransaction.RunWithBackup(path, "scene-patch", () => PatchScene(path, spec));
            }
            catch (Exception exception) when (ShouldReloadSceneAfterPatchFailure(exception))
            {
                ReloadSceneAfterFailedPatch(path, loadedSceneSnapshot);
                throw;
            }
        }

        private static string PatchScene(string path, ScenePatchSpec spec)
        {
            return WithLoadedScene(path, "scene-patch", delegate(Scene scene)
            {
                ScenePatchApplyResult patchResult = ApplyPatchOperations(scene, spec.Operations);
                if (!patchResult.Patched)
                {
                    return ProtocolJson.Serialize(new SceneMutationPayload
                    {
                        asset = AssetCommandSupport.BuildRecordFromPath(path),
                        activeScenePath = EditorSceneManager.GetActiveScene().path,
                        patched = false,
                        createdPath = patchResult.CreatedPath,
                        warnings = patchResult.Warnings.Count == 0 ? null : patchResult.Warnings.ToArray(),
                    });
                }

                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene))
                {
                    throw new CommandFailureException("SCENE_SAVE_FAILED", "Could not save scene: " + path);
                }
                // Unity 6+: after SaveScene, Refresh instead of ImportAsset so the
                // file watcher sees the change and does not show "modified externally".
                AssetDatabase.Refresh();
                return ProtocolJson.Serialize(new SceneMutationPayload
                {
                    asset = AssetCommandSupport.BuildRecordFromPath(path),
                    activeScenePath = EditorSceneManager.GetActiveScene().path,
                    patched = patchResult.Patched,
                    createdPath = patchResult.CreatedPath,
                    warnings = patchResult.Warnings.Count == 0 ? null : patchResult.Warnings.ToArray(),
                });
            });
        }

        private static string HandleAssignMaterial(string argumentsJson)
        {
            SceneAssignMaterialArgs args = ProtocolJson.Deserialize<SceneAssignMaterialArgs>(argumentsJson) ?? new SceneAssignMaterialArgs();
            Scene scene = RequireActiveSavedScene("scene assign-material", requireClean: true);
            GameObject node = SceneInspector.ResolveNode(scene, args.node, "scene assign-material");

            MeshRenderer meshRenderer = node.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
            {
                throw new CommandFailureException("SCENE_COMPONENT_NOT_FOUND", "scene assign-material target node has no MeshRenderer: " + SceneInspector.BuildNodePath(node));
            }

            (Material material, string materialPath) = LoadMaterialAsset(args.material, "scene assign-material");
            Material[] sharedMaterials = meshRenderer.sharedMaterials ?? Array.Empty<Material>();
            string? previousMaterialPath = sharedMaterials.Length > 0 && sharedMaterials[0] != null
                ? AssetDatabase.GetAssetPath(sharedMaterials[0])
                : null;

            if (sharedMaterials.Length == 0)
            {
                sharedMaterials = new Material[1];
            }

            sharedMaterials[0] = material;
            meshRenderer.sharedMaterials = sharedMaterials;

            EditorUtility.SetDirty(meshRenderer);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                throw new CommandFailureException("SCENE_SAVE_FAILED", "Could not save scene: " + scene.path);
            }

            AssetDatabase.Refresh();
            return ProtocolJson.Serialize(new SceneAssignMaterialPayload
            {
                asset = AssetCommandSupport.BuildRecordFromPath(scene.path),
                activeScenePath = scene.path,
                node = SceneInspector.BuildNodePath(node),
                material = materialPath,
                previousMaterial = previousMaterialPath,
            });
        }

        private static string HandleListComponents(string argumentsJson)
        {
            SceneListComponentsArgs args = ProtocolJson.Deserialize<SceneListComponentsArgs>(argumentsJson)
                ?? new SceneListComponentsArgs();
            if (string.IsNullOrWhiteSpace(args.node))
            {
                throw new CommandFailureException("SCENE_LIST_COMPONENTS_INVALID", "`--node` is required.");
            }

            Scene scene = RequireActiveSavedScene("scene-list-components");
            GameObject target = SceneInspector.ResolveNode(scene, args.node, "scene-list-components");
            ComponentOperations.ComponentEntry[] entries = ComponentOperations.ListComponentEntries(target);

            return ProtocolJson.Serialize(new SceneListComponentsPayload
            {
                node = args.node,
                components = entries,
            });
        }

        private static string HandleSetTransform(string argumentsJson)
        {
            SceneSetTransformArgs args = ProtocolJson.Deserialize<SceneSetTransformArgs>(argumentsJson) ?? new SceneSetTransformArgs();
            if (args.position == null && args.rotation == null && args.scale == null)
            {
                throw new CommandFailureException("SCENE_TRANSFORM_INVALID", "scene set-transform requires at least one of position, rotation, or scale.");
            }

            Scene scene = RequireActiveSavedScene("scene set-transform", requireClean: true);
            GameObject node = SceneInspector.ResolveNode(scene, args.node, "scene set-transform");
            ApplySerializedTransform(node.transform, args.position, args.rotation, args.scale);

            EditorUtility.SetDirty(node.transform);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                throw new CommandFailureException("SCENE_SAVE_FAILED", "Could not save scene: " + scene.path);
            }

            AssetDatabase.Refresh();
            return ProtocolJson.Serialize(new SceneSetTransformPayload
            {
                asset = AssetCommandSupport.BuildRecordFromPath(scene.path),
                activeScenePath = scene.path,
                node = SceneInspector.BuildNodePath(node),
                position = args.position,
                rotation = args.rotation,
                scale = args.scale,
            });
        }

        private static T WithLoadedScene<T>(string path, string commandName, Func<Scene, T> action)
        {
            Scene scene = SceneManager.GetSceneByPath(path);
            bool isOpenedHere = false;

            if (scene.IsValid() && scene.isLoaded)
            {
                if (scene.isDirty)
                {
                    throw new CommandFailureException(
                        ProtocolConstants.ErrorSceneDirty,
                        commandName + " target scene has unsaved changes. Save or discard them first: " + path);
                }
            }
            else
            {
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                isOpenedHere = true;
            }

            try
            {
                return action(scene);
            }
            finally
            {
                if (isOpenedHere && scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static void PrepareForOpen(bool isForced)
        {
            bool hasDirtyScene = false;
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.isLoaded && scene.isDirty)
                {
                    hasDirtyScene = true;
                    if (!isForced)
                    {
                        string sceneName = string.IsNullOrWhiteSpace(scene.path) ? scene.name : scene.path;
                        throw new CommandFailureException(
                            ProtocolConstants.ErrorSceneDirty,
                            "There are unsaved scene changes. Pass --force to discard them and open: " + sceneName);
                    }
                }
            }

            if (isForced && hasDirtyScene)
            {
                // Reset to a fresh scene first so `scene open --force` discards dirty state deterministically.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        private static void EnsureScenePatchTargetClean(string path, string commandName)
        {
            Scene scene = SceneManager.GetSceneByPath(path);
            if (scene.IsValid() && scene.isLoaded && scene.isDirty)
            {
                throw new CommandFailureException(
                    ProtocolConstants.ErrorSceneDirty,
                    commandName + " cannot continue because '" + path + "' has unsaved changes. Save or discard them, then try again.");
            }
        }

        private static bool ShouldReloadSceneAfterPatchFailure(Exception exception)
        {
            if (exception is CommandFailureException failure
                && (failure.ErrorCode == ProtocolConstants.ErrorBackupFailed
                    || failure.ErrorCode == ProtocolConstants.ErrorBackupRestoreFailed
                    || failure.ErrorCode == ProtocolConstants.ErrorSceneDirty))
            {
                return false;
            }

            return true;
        }

        private static LoadedSceneSnapshot CaptureLoadedSceneSnapshot(string targetPath)
        {
            Scene activeScene = EditorSceneManager.GetActiveScene();
            bool targetWasActive = string.Equals(activeScene.path, targetPath, StringComparison.Ordinal);
            bool targetWasLoaded = false;

            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid()
                    && scene.isLoaded
                    && string.Equals(scene.path, targetPath, StringComparison.Ordinal))
                {
                    targetWasLoaded = true;
                    break;
                }
            }

            return new LoadedSceneSnapshot(targetWasLoaded, targetWasActive);
        }

        private static void ReloadSceneAfterFailedPatch(string path, LoadedSceneSnapshot snapshot)
        {
            if (!ScenePatchRecoveryPolicy.ShouldReloadAfterFailedPatch(snapshot.TargetWasLoaded))
            {
                // WithLoadedScene's finally already unloads the temp scene; reopening here would surprise the user with an extra loaded scene.
                return;
            }

            try
            {
                Scene scene = SceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded && !EditorSceneManager.CloseScene(scene, removeScene: true))
                {
                    Debug.LogWarning("After a failed scene patch, could not close the scene to reload it: " + path);
                    return;
                }

                Scene reloadedScene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                if (!reloadedScene.IsValid() || !reloadedScene.isLoaded)
                {
                    Debug.LogWarning("After a failed scene patch, reloading the target scene failed: " + path);
                    return;
                }

                if (snapshot.TargetWasLoaded && snapshot.TargetWasActive)
                {
                    SceneManager.SetActiveScene(reloadedScene);
                }
            }
            catch (Exception reloadException)
            {
                Debug.LogWarning("After a failed scene patch, the reload that discards dirty state failed: " + path + " (" + reloadException.Message + ")");
            }
        }

        private readonly struct LoadedSceneSnapshot
        {
            public LoadedSceneSnapshot(bool targetWasLoaded, bool targetWasActive)
            {
                TargetWasLoaded = targetWasLoaded;
                TargetWasActive = targetWasActive;
            }

            public bool TargetWasLoaded { get; }
            public bool TargetWasActive { get; }
        }

        private static T DeserializeSpec<T>(string? specJson, string commandName) where T : class
        {
            if (string.IsNullOrWhiteSpace(specJson))
            {
                throw new CommandFailureException("SCENE_SPEC_INVALID", commandName + " spec is empty.");
            }

            try
            {
                T spec = JsonConvert.DeserializeObject<T>(specJson, BridgeJsonSettings.CamelCaseIgnoreNull);

                if (spec == null)
                {
                    throw new CommandFailureException("SCENE_SPEC_INVALID", commandName + " spec could not be parsed.");
                }

                return spec;
            }
            catch (JsonException exception)
            {
                throw new CommandFailureException("SCENE_SPEC_INVALID", commandName + " spec JSON is invalid.", exception.Message);
            }
        }

        private static void ValidateVersion(int version, string commandName)
        {
            if (version > 1)
            {
                throw new CommandFailureException("SCENE_SPEC_INVALID", commandName + " spec version is not supported: " + version);
            }
        }

        private static Scene RequireActiveSavedScene(string commandName, bool requireClean = false)
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new CommandFailureException("SCENE_NOT_OPEN", commandName + " target has no active scene.");
            }

            if (string.IsNullOrWhiteSpace(scene.path))
            {
                throw new CommandFailureException("SCENE_PATH_INVALID", commandName + " target active scene has not been saved. Save it as a scene asset first.");
            }

            if (requireClean)
            {
                EnsureScenePatchTargetClean(scene.path, commandName);
            }

            return scene;
        }

        private static (Material material, string path) LoadMaterialAsset(string? path, string commandName)
        {
            string normalizedPath;
            try
            {
                normalizedPath = AssetCommandSupport.NormalizeAssetPath(path);
            }
            catch (Exception exception)
            {
                throw new CommandFailureException("SCENE_MATERIAL_INVALID", exception.Message, exception.ToString());
            }

            Material material = AssetDatabase.LoadAssetAtPath<Material>(normalizedPath);
            if (material == null)
            {
                throw new CommandFailureException("MATERIAL_NOT_FOUND", commandName + " could not find material: " + normalizedPath);
            }

            return (material, normalizedPath);
        }

        private static void ApplySerializedTransform(Transform transform, SceneVector3Value? position, SceneVector3Value? rotation, SceneVector3Value? scale)
        {
            var serializedObject = new SerializedObject(transform);
            serializedObject.Update();

            if (position != null)
            {
                RequireSerializedProperty(serializedObject, "m_LocalPosition").vector3Value = ToVector3(position);
            }

            if (rotation != null)
            {
                Vector3 euler = ToVector3(rotation);
                RequireSerializedProperty(serializedObject, "m_LocalRotation").quaternionValue = Quaternion.Euler(euler);

                SerializedProperty eulerHint = serializedObject.FindProperty("m_LocalEulerAnglesHint");
                if (eulerHint != null)
                {
                    eulerHint.vector3Value = euler;
                }
            }

            if (scale != null)
            {
                RequireSerializedProperty(serializedObject, "m_LocalScale").vector3Value = ToVector3(scale);
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static SerializedProperty RequireSerializedProperty(SerializedObject serializedObject, string propertyPath)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyPath);
            if (property == null)
            {
                throw new CommandFailureException("SCENE_COMPONENT_INVALID", "Could not find Transform serialized field: " + propertyPath);
            }

            return property;
        }

        private static Vector3 ToVector3(SceneVector3Value value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        private static string ResolveScenePath(string? path)
        {
            string normalizedPath;
            try
            {
                normalizedPath = AssetCommandSupport.NormalizeAssetPath(path);
            }
            catch (Exception exception)
            {
                throw new CommandFailureException("SCENE_PATH_INVALID", exception.Message, exception.ToString());
            }

            string extension = Path.GetExtension(normalizedPath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return normalizedPath + ".unity";
            }

            if (!string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandFailureException("SCENE_PATH_INVALID", "Scene path must use the `.unity` extension: " + normalizedPath);
            }

            return normalizedPath;
        }

        private static string RequireExistingScenePath(string? path, string commandName)
        {
            string scenePath = ResolveScenePath(path);
            if (!AssetCommandSupport.AssetExists(scenePath))
            {
                throw new CommandFailureException("SCENE_PATH_INVALID", commandName + " target scene does not exist: " + scenePath);
            }

            Type? mainType = AssetDatabase.GetMainAssetTypeAtPath(scenePath);
            if (mainType != typeof(SceneAsset))
            {
                throw new CommandFailureException("SCENE_PATH_INVALID", "Not a scene asset: " + scenePath);
            }

            return scenePath;
        }
    }

}
