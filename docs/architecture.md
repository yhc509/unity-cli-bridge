# Architecture

## Summary

`unity-cli-bridge` is a mono-repo for controlling the Unity Editor from the command line without manual server startup. Its public surface has three parts.

- `cli/`: the .NET CLI that receives user commands and routes them to live IPC
- `unity-package/com.yhc509.unity-cli-bridge/`: the bridge package that starts automatically inside the Unity Editor
- `tools/skills/unity-cli-operator/`: the AI-agent skill that keeps `unity-cli` usage consistent

## Runtime Flow

### Live

1. When the Unity Editor opens, the bridge starts automatically.
2. The bridge registers the project real-path hash and instance information in the registry.
3. The CLI finds the Unity project by walking up from the working directory, then searching a few descendant levels (skipping heavy and hidden folders, in canonical-path order), or from `--project <path|name>`. An existing directory path wins over a registry name. Several on-disk matches fail with `AMBIGUOUS_PROJECT` unless a live Editor or an explicit pin uniquely selects one. The CLI then attaches to that instance through the registry.
4. Requests are executed on the Editor main thread over local IPC.

### Live Target Unavailable

1. If there is no selected live instance, the CLI returns a live-unavailable error.
2. If the registry points at an instance but the bridge is still importing or compiling, the CLI returns a live-unavailable error with guidance to retry.

## Core Behaviors

- Multi-project selection is based on the project root hash and the active instance in the registry.
- On macOS, hashes are computed from the real path instead of a symlink path.
- When no running Editor with an active bridge is reachable, commands fail instead of trying any editor-off fallback.
- Destructive operations and overwrite behavior require `--force`.
- Patch and overwrite mutations use `Library/com.yhc509.unity-cli-bridge/backups/` backups for asset files and `.meta` files so rollback stays outside `Assets/` while remaining on the project volume.
- `scene patch` refuses already-loaded dirty target scenes because disk backups cannot preserve in-memory edits.
- Scene editing is split into `scene open`, `scene inspect`, and `scene patch`, and scene node paths use `/Root[0]/Child[0]` with `/` as the virtual scene root.
- Prefab editing is split into `prefab create`, `prefab inspect`, and `prefab patch`, and field patching is based on `SerializedProperty.propertyPath`.

## Repository Layout

- `UnityCliBridge.sln`: solution root for the CLI, protocol project, DocGen, and tests
- `cli/UnityCli.Cli`: user-facing CLI
- `cli/UnityCli.Protocol`: protocol models shared by the CLI and Unity package
- `unity-package/com.yhc509.unity-cli-bridge`: UPM package
- `tools/skills/unity-cli-operator`: AI-agent skill (maintainer copy)

## Editor Bridge Layout

Inside `unity-package/com.yhc509.unity-cli-bridge/Editor`, the bridge is split by responsibility.

- `BridgeHost.cs` bootstraps the bridge, registers the live instance, opens the IPC listener, and wires command handlers together
- `AssetCommandHandler.cs` covers asset CRUD and metadata flows
- `AssetBackupTransaction.cs` wraps Editor asset mutations in backup/restore transactions backed by shared protocol file logic
- `BuiltInAssetCreateProviders.cs` and `BuiltInAssetCreateProviders.Advanced.cs` split built-in asset creation into basic and dependency-aware providers
- `SceneCommandHandler.cs`, `SceneCommandHandler.Patching.cs`, `SceneInspector.cs`, and `SceneSpecModels.cs` split scene entry points, patch logic, graph traversal, and DTO/spec models
- `PrefabCommandHandler.cs`, `PrefabCommandHandler.Patching.cs`, `PrefabInspector.cs`, and `PrefabSpecModels.cs` split prefab entry points, patch logic, inspection, and DTO/spec models
- `SerializedValueApplier.cs` applies serialized values by `SerializedProperty.propertyPath`
- `TypeDiscoveryUtility.cs` provides shared type scanning for component and asset type resolution
- `BridgeJsonSettings.cs` centralizes the shared camelCase + ignore-null JSON serializer settings

## Shared Protocol Layout

`cli/UnityCli.Protocol` compiles linked source files from `unity-package/com.yhc509.unity-cli-bridge/Runtime/Protocol`, so the CLI and the Unity package stay on the same request/response contracts.

- `CliCommandCatalog.cs`: command descriptors
- `CommandModels.cs`: request/response envelopes
- `ProtocolConstants.cs`: command names, timeouts, and registry constants
- `FileBackupTransaction.cs`: file backup/restore transaction core shared by the Editor bridge and .NET tests
- `ProtocolHelpers.cs` and `ProtocolJson.cs`: shared command/serialization helpers
- `Registry*.cs`: registry persistence and path models
- `TransportModels.cs`: IPC transport payloads

## Current Limits

- CLI release validation is currently centered on `macOS arm64`.
- Scene editing is currently limited to saved `Assets/...unity` assets; multi-scene orchestration and generalized scene object references are not covered yet.
- Advanced editing of prefab-internal object references and nested prefab variants is not generalized yet.
- The root prefab object name is normalized to the prefab file name after save.
