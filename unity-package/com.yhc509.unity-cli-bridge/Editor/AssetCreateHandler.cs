using System;
using System.IO;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed class AssetCreateHandler
    {
        public string Handle(string argumentsJson)
        {
            AssetCreateArgs args = ProtocolJson.Deserialize<AssetCreateArgs>(argumentsJson) ?? new AssetCreateArgs();
            IAssetCreateProvider provider = AssetCreateRegistry.Resolve(args.type);
            AssetCreateTypeDescriptor descriptor = provider.Describe();
            string createType = descriptor.typeId;
            string path = ResolveOutputPath(descriptor.defaultExtension, args.path);
            AssetCommandSupport.EnsureParentFolderExists(path);
            if (!args.force && AssetCommandSupport.AssetExists(path))
            {
                throw new CommandFailureException(ProtocolConstants.ErrorAssetForceRequired, "Overwriting or deleting an asset requires --force.");
            }

            var request = new AssetCreateRequest(args, createType, path);
            bool isOverwritten = AssetCommandSupport.AssetExists(path);
            // Non-overwrite creates still use the transaction so failed saves remove partial body/.meta artifacts.
            return AssetBackupTransaction.RunWithMovedBackup(path, "asset-create", () => CreateAsset(provider, request, descriptor, isOverwritten));
        }

        private static string CreateAsset(
            IAssetCreateProvider provider,
            AssetCreateRequest request,
            AssetCreateTypeDescriptor descriptor,
            bool isOverwritten)
        {
            string path = request.AssetPath;
            string createType = request.TypeId;
            AssetCreateArtifact artifact = provider.Create(request);
            ApplyDataPatchIfNeeded(descriptor, artifact, request);
            artifact.SaveAction();

            AssetDatabase.SaveAssets();
            // Unity 6+: after SaveScene/SavePrefab, Refresh instead of ImportAsset so the
            // file watcher sees the change and does not show "modified externally".
            AssetDatabase.Refresh();
            ValidateCreatedAsset(path, artifact.ExpectedMainAssetType);

            return ProtocolJson.Serialize(new AssetCreatePayload
            {
                asset = AssetCommandSupport.BuildRecordFromPath(path),
                createdType = createType,
                overwritten = isOverwritten,
            });
        }

        public string HandleTypes()
        {
            return ProtocolJson.Serialize(new AssetTypesPayload
            {
                types = AssetCreateRegistry.GetDescriptors(),
            });
        }

        private static string ResolveOutputPath(string extension, string path)
        {
            string normalizedPath = AssetCommandSupport.NormalizeAssetPath(path);
            if (string.Equals(normalizedPath, "Assets", StringComparison.Ordinal))
            {
                throw new CommandFailureException("ASSET_CREATE_FAILED", "Asset file path must use the `Assets/...` form.");
            }

            string currentExtension = Path.GetExtension(normalizedPath);
            if (string.IsNullOrWhiteSpace(currentExtension))
            {
                return normalizedPath + extension;
            }

            if (!string.Equals(currentExtension, extension, StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandFailureException(
                    "ASSET_CREATE_FAILED",
                    "This type must use the " + extension + " extension: " + normalizedPath);
            }

            return normalizedPath;
        }

        private static void ApplyDataPatchIfNeeded(AssetCreateTypeDescriptor descriptor, AssetCreateArtifact artifact, AssetCreateRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.DataJson))
            {
                return;
            }

            if (!descriptor.supportsDataPatch || artifact.DataPatchTarget == null)
            {
                throw new CommandFailureException("ASSET_DATA_PATCH_FAILED", request.TypeId + " type does not support `--data-json`.");
            }

            try
            {
                JsonUtility.FromJsonOverwrite(request.DataJson, artifact.DataPatchTarget);
            }
            catch (Exception exception)
            {
                throw new CommandFailureException(
                    "ASSET_DATA_PATCH_FAILED",
                    "Failed to apply the initial JSON values: " + request.TypeId,
                    exception.Message);
            }
        }

        private static void ValidateCreatedAsset(string path, Type expectedMainAssetType)
        {
            UnityEngine.Object mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            if (mainAsset == null)
            {
                throw new CommandFailureException("ASSET_CREATE_FAILED", "Could not read the main asset after creating it: " + path);
            }

            if (!expectedMainAssetType.IsInstanceOfType(mainAsset))
            {
                throw new CommandFailureException(
                    "ASSET_CREATE_FAILED",
                    "Created asset type was not the expected type: " + path,
                    "expected=" + expectedMainAssetType.FullName + ", actual=" + mainAsset.GetType().FullName);
            }
        }
    }
}
