using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class PrefabCommandHandler
    {
        public bool CanHandle(string command)
        {
            return ProtocolHelpers.IsPrefabCommand(command);
        }

        public string Handle(string command, string argumentsJson)
        {
            switch (command)
            {
                case ProtocolConstants.CommandPrefabInspect:
                    return HandleInspect(argumentsJson);
                case ProtocolConstants.CommandPrefabCreate:
                    return HandleCreate(argumentsJson);
                case ProtocolConstants.CommandPrefabPatch:
                    return HandlePatch(argumentsJson);
                case ProtocolConstants.CommandPrefabListComponents:
                    return HandleListComponents(argumentsJson);
                default:
                    throw new InvalidOperationException("Unsupported prefab command: " + command);
            }
        }

        private static string HandleInspect(string argumentsJson)
        {
            JObject arguments = InspectorMutationReaderUtility.ParseArgumentsObject(argumentsJson);
            PrefabInspectArgs args = InspectorMutationReaderUtility.DeserializeArguments<PrefabInspectArgs>(arguments);
            int? maxDepth = args.maxDepth ?? InspectorMutationReaderUtility.ParseOptionalMaxDepth(arguments, "PREFAB_INSPECT_INVALID");
            string path = RequireInspectablePrefabPath(args.path);
            if (maxDepth.HasValue && maxDepth.Value <= 0)
            {
                throw new CommandFailureException("PREFAB_INSPECT_INVALID", "`--max-depth` must be an integer >= 1.");
            }
            GameObject root = LoadPrefabForInspect(path, out bool unloadContents);

            try
            {
                GameObject? targetRoot = PrefabInspector.ResolveInspectRoot(root, args.node, "prefab-inspect");
                return PrefabInspector.BuildInspectPayload(path, root, args.withValues, maxDepth, args.omitDefaults, targetRoot);
            }
            finally
            {
                if (unloadContents)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        private static string HandleCreate(string argumentsJson)
        {
            PrefabCreateArgs args = ProtocolJson.Deserialize<PrefabCreateArgs>(argumentsJson) ?? new PrefabCreateArgs();
            string path = ResolvePrefabPath(args.path);
            AssetCommandSupport.EnsureParentFolderExists(path);

            PrefabCreateSpec spec = DeserializeSpec<PrefabCreateSpec>(args.specJson, "prefab-create");
            ValidateVersion(spec.Version, "prefab-create");
            if (spec.Root == null)
            {
                throw new CommandFailureException("PREFAB_SPEC_INVALID", "`root` is required.");
            }

            bool isOverwritten = AssetCommandSupport.AssetExists(path);
            if (isOverwritten && !args.force)
            {
                throw new CommandFailureException(ProtocolConstants.ErrorPrefabForceRequired, "Overwriting a prefab requires --force.");
            }

            if (isOverwritten)
            {
                EnsureCurrentPrefabStageCleanForWrite(path);
            }

            // Non-overwrite creates still use the transaction so failed saves remove partial body/.meta artifacts.
            return AssetBackupTransaction.RunWithMovedBackup(path, "prefab-create", () => CreatePrefab(path, spec, isOverwritten));
        }

        private static string CreatePrefab(string path, PrefabCreateSpec spec, bool isOverwritten)
        {
            string rootName = string.IsNullOrWhiteSpace(spec.Root.Name)
                ? Path.GetFileNameWithoutExtension(path)
                : spec.Root.Name.Trim();
            var root = new GameObject(rootName);

            try
            {
                PrefabInspector.ApplyNodeState(root, spec.Root, rootName, allowMissingName: true);
                AddComponents(root, spec.Root.Components, "prefab-create");
                AddChildren(root.transform, spec.Root.Children, "prefab-create");

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
                if (saved == null)
                {
                    throw new CommandFailureException("PREFAB_SAVE_FAILED", "Could not save prefab: " + path);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            // Unity 6+: after SavePrefab, Refresh instead of ImportAsset so the
            // file watcher sees the change and does not show "modified externally".
            AssetDatabase.Refresh();
            return ProtocolJson.Serialize(new PrefabMutationPayload
            {
                asset = AssetCommandSupport.BuildRecordFromPath(path),
                created = true,
                overwritten = isOverwritten,
            });
        }

        private static string HandlePatch(string argumentsJson)
        {
            PrefabPatchArgs args = ProtocolJson.Deserialize<PrefabPatchArgs>(argumentsJson) ?? new PrefabPatchArgs();
            string path = RequireExistingPrefabPath(args.path, "prefab-patch");

            PrefabPatchSpec spec = DeserializeSpec<PrefabPatchSpec>(args.specJson, "prefab-patch");
            ValidateVersion(spec.Version, "prefab-patch");
            if (spec.Operations == null || spec.Operations.Length == 0)
            {
                throw new CommandFailureException("PREFAB_SPEC_INVALID", "`operations` is empty.");
            }

            NormalizeOperationNames(spec.Operations);
            if (HasDestructiveOperation(spec) && !args.force)
            {
                throw new CommandFailureException(ProtocolConstants.ErrorPrefabForceRequired, "--force is required to use `remove-node` or `remove-component`.");
            }

            EnsureCurrentPrefabStageCleanForWrite(path);
            return AssetBackupTransaction.RunWithBackup(path, "prefab-patch", () => PatchPrefab(path, spec));
        }

        private static string PatchPrefab(string path, PrefabPatchSpec spec)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            PrefabPatchApplyResult patchResult;
            try
            {
                patchResult = ApplyPatchOperations(root, spec.Operations);
                if (!patchResult.Patched)
                {
                    return ProtocolJson.Serialize(new PrefabMutationPayload
                    {
                        asset = AssetCommandSupport.BuildRecordFromPath(path),
                        patched = false,
                        warnings = patchResult.Warnings.Count == 0 ? null : patchResult.Warnings.ToArray(),
                    });
                }

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
                if (saved == null)
                {
                    throw new CommandFailureException("PREFAB_SAVE_FAILED", "Could not save prefab: " + path);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            // Unity 6+: after SavePrefab, Refresh instead of ImportAsset so the
            // file watcher sees the change and does not show "modified externally".
            AssetDatabase.Refresh();
            return ProtocolJson.Serialize(new PrefabMutationPayload
            {
                asset = AssetCommandSupport.BuildRecordFromPath(path),
                patched = patchResult.Patched,
                warnings = patchResult.Warnings.Count == 0 ? null : patchResult.Warnings.ToArray(),
            });
        }

        private static string HandleListComponents(string argumentsJson)
        {
            var args = ProtocolJson.Deserialize<PrefabListComponentsArgs>(argumentsJson)
                ?? new PrefabListComponentsArgs();
            string path = RequireExistingPrefabPath(args.path, "prefab-list-components");
            if (string.IsNullOrWhiteSpace(args.node))
            {
                throw new CommandFailureException("PREFAB_LIST_COMPONENTS_INVALID", "`--node` is required.");
            }

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                GameObject target = PrefabInspector.ResolveNode(root, args.node, "prefab-list-components");
                ComponentOperations.ComponentEntry[] entries = ComponentOperations.ListComponentEntries(target);
                return ProtocolJson.Serialize(new PrefabListComponentsPayload
                {
                    node = args.node,
                    components = entries,
                });
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static T DeserializeSpec<T>(string specJson, string commandName) where T : class
        {
            if (string.IsNullOrWhiteSpace(specJson))
            {
                throw new CommandFailureException("PREFAB_SPEC_INVALID", commandName + " spec is empty.");
            }

            try
            {
                T spec = JsonConvert.DeserializeObject<T>(specJson, BridgeJsonSettings.CamelCaseIgnoreNull);

                if (spec == null)
                {
                    throw new CommandFailureException("PREFAB_SPEC_INVALID", commandName + " spec could not be parsed.");
                }

                return spec;
            }
            catch (JsonException exception)
            {
                throw new CommandFailureException("PREFAB_SPEC_INVALID", commandName + " spec JSON is invalid.", exception.Message);
            }
        }

        private static void ValidateVersion(int version, string commandName)
        {
            if (version > 1)
            {
                throw new CommandFailureException("PREFAB_SPEC_INVALID", commandName + " spec version is not supported: " + version);
            }
        }

        private static string RequireInspectablePrefabPath(string? path)
        {
            string normalizedPath = AssetCommandSupport.NormalizeAssetPath(path ?? string.Empty);
            string extension = Path.GetExtension(normalizedPath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                normalizedPath += ".prefab";
            }
            else if (!PrefabInspectPaths.IsInspectableExtension(extension))
            {
                throw new CommandFailureException(
                    "PREFAB_PATH_INVALID",
                    PrefabInspectPaths.UnsupportedExtensionMessage(normalizedPath));
            }

            normalizedPath = AssetCommandSupport.RequireExistingAssetPath(normalizedPath, "prefab-inspect");
            Type mainType = AssetDatabase.GetMainAssetTypeAtPath(normalizedPath);
            if (mainType != typeof(GameObject))
            {
                throw new CommandFailureException("PREFAB_PATH_INVALID", "Not a prefab asset: " + normalizedPath);
            }

            if (!string.Equals(Path.GetExtension(normalizedPath), ".prefab", StringComparison.OrdinalIgnoreCase))
            {
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(normalizedPath);
                if (model == null || PrefabUtility.GetPrefabAssetType(model) != PrefabAssetType.Model)
                {
                    throw new CommandFailureException("PREFAB_PATH_INVALID", "Not a model prefab asset: " + normalizedPath);
                }
            }

            return normalizedPath;
        }

        private static GameObject LoadPrefabForInspect(string path, out bool unloadContents)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset != null && PrefabUtility.GetPrefabAssetType(asset) == PrefabAssetType.Model)
            {
                unloadContents = false;
                return asset;
            }

            unloadContents = true;
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            if (contents == null)
            {
                throw new CommandFailureException("PREFAB_PATH_INVALID", "Could not load prefab contents: " + path);
            }

            return contents;
        }

        private static string ResolvePrefabPath(string? path)
        {
            string normalizedPath = AssetCommandSupport.NormalizeAssetPath(path ?? string.Empty);
            string extension = Path.GetExtension(normalizedPath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return normalizedPath + ".prefab";
            }

            if (!string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandFailureException("PREFAB_PATH_INVALID", "Prefab path must use the `.prefab` extension: " + normalizedPath);
            }

            return normalizedPath;
        }

        private static void EnsureCurrentPrefabStageCleanForWrite(string prefabPath)
        {
            string normalizedPath = AssetCommandSupport.NormalizeAssetPath(prefabPath);
            string targetGuid = AssetDatabase.AssetPathToGUID(normalizedPath);
            if (string.IsNullOrEmpty(targetGuid))
            {
                return;
            }

            foreach (PrefabStage prefabStage in CollectOpenPrefabStages())
            {
                if (prefabStage == null || !prefabStage.scene.isDirty)
                {
                    continue;
                }

                string stageGuid = AssetDatabase.AssetPathToGUID(prefabStage.assetPath);
                bool isSamePrefab = !string.IsNullOrEmpty(stageGuid)
                    && string.Equals(stageGuid, targetGuid, StringComparison.Ordinal);
                if (isSamePrefab)
                {
                    throw new CommandFailureException(
                        ProtocolConstants.ErrorPrefabStageDirty,
                        "'" + normalizedPath + "' is open in Prefab Stage with unsaved changes, so this cannot continue. Save or discard them in Prefab Stage, then try again.");
                }
            }
        }

        private static List<PrefabStage> CollectOpenPrefabStages()
        {
            var stages = new List<PrefabStage>();

            // Stable path: always take the current stage without reflection (keeps #79 behavior if reflection fails).
            PrefabStage current = PrefabStageUtility.GetCurrentPrefabStage();
            if (current != null)
            {
                stages.Add(current);
            }

            // Extended path: parent stages from breadcrumb history (nested editing). Internal API, best-effort.
            try
            {
                Type managerType = typeof(PrefabStage).Assembly
                    .GetType("UnityEditor.SceneManagement.StageNavigationManager");
                PropertyInfo instanceProperty = managerType?.GetProperty(
                    "instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                object manager = instanceProperty?.GetValue(null);
                PropertyInfo historyProperty = managerType?.GetProperty(
                    "stageHistory", BindingFlags.NonPublic | BindingFlags.Instance);
                if (manager != null && historyProperty?.GetValue(manager) is IEnumerable history)
                {
                    foreach (object item in history)
                    {
                        if (item is PrefabStage prefabStage && !stages.Contains(prefabStage))
                        {
                            stages.Add(prefabStage);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // StageNavigationManager is internal API and its shape can change across Unity versions.
                // If it does, degrade to the current-stage check only (#79 behavior) — no crash, no false positive.
            }

            return stages;
        }

        private static string RequireExistingPrefabPath(string? path, string commandName)
        {
            string prefabPath = ResolvePrefabPath(path);
            prefabPath = AssetCommandSupport.RequireExistingAssetPath(prefabPath, commandName);
            Type mainType = AssetDatabase.GetMainAssetTypeAtPath(prefabPath);
            if (mainType != typeof(GameObject))
            {
                throw new CommandFailureException("PREFAB_PATH_INVALID", "Not a prefab asset: " + prefabPath);
            }

            return prefabPath;
        }
    }
}
