using System;
using System.Collections.Generic;
using UnityCli.Protocol;
using UnityEditor;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed class AssetCommandHandler
    {
        private readonly AssetCreateHandler _assetCreateHandler = new AssetCreateHandler();

        public bool CanHandle(string command)
        {
            return ProtocolHelpers.IsAssetCommand(command);
        }

        public string Handle(string command, string argumentsJson)
        {
            switch (command)
            {
                case ProtocolConstants.CommandAssetFind:
                    return HandleFind(argumentsJson);
                case ProtocolConstants.CommandAssetTypes:
                    return _assetCreateHandler.HandleTypes();
                case ProtocolConstants.CommandAssetInfo:
                    return HandleInfo(argumentsJson);
                case ProtocolConstants.CommandAssetReimport:
                    return HandleReimport(argumentsJson);
                case ProtocolConstants.CommandAssetMkdir:
                    return HandleMkdir(argumentsJson);
                case ProtocolConstants.CommandAssetMove:
                    return HandleMove(argumentsJson);
                case ProtocolConstants.CommandAssetRename:
                    return HandleRename(argumentsJson);
                case ProtocolConstants.CommandAssetDelete:
                    return HandleDelete(argumentsJson);
                case ProtocolConstants.CommandAssetCreate:
                    return _assetCreateHandler.Handle(argumentsJson);
                default:
                    throw new InvalidOperationException("Unsupported asset command: " + command);
            }
        }

        private static string HandleFind(string argumentsJson)
        {
            AssetFindArgs args = ProtocolJson.Deserialize<AssetFindArgs>(argumentsJson) ?? new AssetFindArgs();
            string? nameTerm = string.IsNullOrWhiteSpace(args.name) ? null : args.name.Trim();
            string? typeTerm = string.IsNullOrWhiteSpace(args.type) ? null : args.type.Trim();
            bool hasNameTerm = !string.IsNullOrWhiteSpace(nameTerm);
            bool hasTypeTerm = !string.IsNullOrWhiteSpace(typeTerm);
            if (!hasNameTerm && !hasTypeTerm)
            {
                throw new InvalidOperationException("asset-find requires a name or a type.");
            }

            int limit = args.limit <= 0 ? ProtocolConstants.DefaultAssetFindLimit : args.limit;
            string filter;
            if (hasNameTerm && hasTypeTerm)
            {
                filter = nameTerm! + " t:" + typeTerm;
            }
            else if (hasNameTerm)
            {
                filter = nameTerm!;
            }
            else
            {
                filter = "t:" + typeTerm!;
            }

            string[] guids = string.IsNullOrWhiteSpace(args.folder)
                ? AssetDatabase.FindAssets(filter)
                : AssetDatabase.FindAssets(filter, new[] { AssetCommandSupport.NormalizeAssetPath(args.folder) });

            var records = new List<AssetRecord>(guids.Length);
            for (int index = 0; index < guids.Length; index++)
            {
                AssetRecord record = AssetCommandSupport.BuildRecordFromGuid(guids[index]);
                if (!record.exists)
                {
                    continue;
                }

                records.Add(record);
            }

            records.Sort(CompareAssetRecordsByPath);

            int resultCount = records.Count;
            if (resultCount > limit)
            {
                resultCount = limit;
            }

            var results = new AssetRecord[resultCount];
            for (int index = 0; index < resultCount; index++)
            {
                results[index] = records[index];
            }

            return ProtocolJson.Serialize(new AssetFindPayload { results = results });
        }

        private static string HandleInfo(string argumentsJson)
        {
            AssetInfoArgs args = ProtocolJson.Deserialize<AssetInfoArgs>(argumentsJson) ?? new AssetInfoArgs();
            bool hasPath = !string.IsNullOrWhiteSpace(args.path);
            bool hasGuid = !string.IsNullOrWhiteSpace(args.guid);
            if (hasPath == hasGuid)
            {
                throw new InvalidOperationException("asset-info requires exactly one of path or guid.");
            }

            AssetRecord payload = hasPath
                ? AssetCommandSupport.BuildRecordFromPath(args.path, allowPackages: true)
                : AssetCommandSupport.BuildRecordFromGuid(args.guid.Trim());

            return ProtocolJson.Serialize(payload);
        }

        private static string HandleReimport(string argumentsJson)
        {
            AssetPathArgs args = ProtocolJson.Deserialize<AssetPathArgs>(argumentsJson) ?? new AssetPathArgs();
            string path = AssetCommandSupport.RequireExistingAssetPath(args.path, "asset-reimport");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return ProtocolJson.Serialize(new AssetMutationPayload
            {
                asset = AssetCommandSupport.BuildRecordFromPath(path),
                reimported = true,
            });
        }

        private static string HandleMkdir(string argumentsJson)
        {
            AssetPathArgs args = ProtocolJson.Deserialize<AssetPathArgs>(argumentsJson) ?? new AssetPathArgs();
            string path = AssetCommandSupport.NormalizeAssetPath(args.path);
            if (string.Equals(path, "Assets", StringComparison.Ordinal))
            {
                return ProtocolJson.Serialize(new AssetMutationPayload
                {
                    asset = AssetCommandSupport.BuildRecordFromPath(path),
                    created = false,
                });
            }

            if (AssetCommandSupport.AssetExists(path))
            {
                if (!AssetDatabase.IsValidFolder(path))
                {
                    throw new InvalidOperationException("A non-folder asset already exists at this path: " + path);
                }

                return ProtocolJson.Serialize(new AssetMutationPayload
                {
                    asset = AssetCommandSupport.BuildRecordFromPath(path),
                    created = false,
                });
            }

            bool isCreated = false;
            string current = "Assets";
            string[] segments = path.Split('/');
            for (int index = 1; index < segments.Length; index++)
            {
                string segment = segments[index];
                if (string.IsNullOrWhiteSpace(segment))
                {
                    throw new InvalidOperationException("A folder name cannot be empty.");
                }

                string next = string.Concat(current, "/", segment);
                if (AssetCommandSupport.AssetExists(next))
                {
                    if (!AssetDatabase.IsValidFolder(next))
                    {
                        throw new InvalidOperationException("An asset file is in the middle of the folder path: " + next);
                    }
                }
                else
                {
                    string guid = AssetDatabase.CreateFolder(current, segment);
                    if (string.IsNullOrWhiteSpace(guid))
                    {
                        throw new InvalidOperationException("Could not create folder: " + next);
                    }

                    isCreated = true;
                }

                current = next;
            }

            AssetDatabase.SaveAssets();
            return ProtocolJson.Serialize(new AssetMutationPayload
            {
                asset = AssetCommandSupport.BuildRecordFromPath(path),
                created = isCreated,
            });
        }

        private static string HandleMove(string argumentsJson)
        {
            AssetMoveArgs args = ProtocolJson.Deserialize<AssetMoveArgs>(argumentsJson) ?? new AssetMoveArgs();
            string from = AssetCommandSupport.RequireExistingAssetPath(args.from, "asset-move");
            string to = AssetCommandSupport.NormalizeAssetPath(args.to);

            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                return ProtocolJson.Serialize(new AssetMutationPayload
                {
                    asset = AssetCommandSupport.BuildRecordFromPath(from),
                    previousPath = from,
                });
            }

            AssetCommandSupport.EnsureParentFolderExists(to);
            if (!args.force && AssetCommandSupport.AssetExists(to))
            {
                throw new CommandFailureException(ProtocolConstants.ErrorAssetForceRequired, "Overwriting or deleting an asset requires --force.");
            }

            bool isOverwritten = AssetCommandSupport.AssetExists(to);
            return AssetBackupTransaction.RunWithBackup(from, "asset-move", () =>
                AssetBackupTransaction.RunWithMovedBackup(to, "asset-move", () => MoveAsset(from, to, isOverwritten)));
        }

        private static string MoveAsset(string from, string to, bool isOverwritten)
        {
            string error = AssetDatabase.MoveAsset(from, to);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(error);
            }

            AssetDatabase.SaveAssets();
            return ProtocolJson.Serialize(new AssetMutationPayload
            {
                asset = AssetCommandSupport.BuildRecordFromPath(to),
                previousPath = from,
                overwritten = isOverwritten,
            });
        }

        private static string HandleRename(string argumentsJson)
        {
            AssetRenameArgs args = ProtocolJson.Deserialize<AssetRenameArgs>(argumentsJson) ?? new AssetRenameArgs();
            string path = AssetCommandSupport.RequireExistingAssetPath(args.path, "asset-rename");
            string newName = args.name == null ? string.Empty : args.name.Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new InvalidOperationException("asset-rename requires a name.");
            }

            if (newName.Contains("/") || newName.Contains("\\"))
            {
                throw new InvalidOperationException("asset-rename name cannot contain a path separator.");
            }

            string destination = AssetCommandSupport.BuildRenamedPath(path, newName);
            if (string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
            {
                return ProtocolJson.Serialize(new AssetMutationPayload
                {
                    asset = AssetCommandSupport.BuildRecordFromPath(path),
                    previousPath = path,
                });
            }

            if (!args.force && AssetCommandSupport.AssetExists(destination))
            {
                throw new CommandFailureException(ProtocolConstants.ErrorAssetForceRequired, "Overwriting or deleting an asset requires --force.");
            }

            bool isOverwritten = AssetCommandSupport.AssetExists(destination);
            return AssetBackupTransaction.RunWithBackup(path, "asset-rename", () =>
                AssetBackupTransaction.RunWithMovedBackup(destination, "asset-rename", () => RenameAsset(path, destination, isOverwritten)));
        }

        private static string RenameAsset(string path, string destination, bool isOverwritten)
        {
            string error = AssetDatabase.MoveAsset(path, destination);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(error);
            }

            AssetDatabase.SaveAssets();
            return ProtocolJson.Serialize(new AssetMutationPayload
            {
                asset = AssetCommandSupport.BuildRecordFromPath(destination),
                previousPath = path,
                overwritten = isOverwritten,
            });
        }

        private static string HandleDelete(string argumentsJson)
        {
            AssetPathArgs args = ProtocolJson.Deserialize<AssetPathArgs>(argumentsJson) ?? new AssetPathArgs();
            if (!args.force)
            {
                throw new CommandFailureException(ProtocolConstants.ErrorAssetForceRequired, "Overwriting or deleting an asset requires --force.");
            }

            string path = AssetCommandSupport.RequireExistingAssetPath(args.path, "asset-delete");
            AssetRecord beforeDelete = AssetCommandSupport.BuildRecordFromPath(path);
            if (!AssetDatabase.DeleteAsset(path))
            {
                throw new InvalidOperationException("Could not delete asset: " + path);
            }

            AssetDatabase.SaveAssets();
            beforeDelete.exists = false;

            return ProtocolJson.Serialize(new AssetMutationPayload
            {
                asset = beforeDelete,
                deleted = true,
            });
        }

        private static int CompareAssetRecordsByPath(AssetRecord left, AssetRecord right)
        {
            return string.Compare(left.path, right.path, StringComparison.OrdinalIgnoreCase);
        }
    }
}
