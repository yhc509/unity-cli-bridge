# Prefab Spec

## Summary

For real prefab authoring, use `prefab create`, `prefab inspect`, and `prefab patch` instead of `asset create --type prefab`.

## `prefab create`

```json
{
  "version": 1,
  "root": {
    "name": "EnemyRoot",
    "children": [
      {
        "name": "Visual"
      },
      {
        "name": "Hitbox",
        "transform": {
          "localPosition": { "x": 0, "y": 1, "z": 0 }
        }
      }
    ]
  }
}
```

Rules:

- `version` is currently `1`
- `root` is required
- A node can contain `name`, `active`, `tag`, `layer`, `transform`, `components`, and `children`
- `transform` currently supports `localPosition`, `localRotationEuler`, and `localScale`

## `prefab inspect`

Use `--with-values` when you need data for authoring a patch.

- Node paths look like `/`, `/Visual[0]`, `/Visual[0]/MeshRoot[0]`
- Component types are reported by full name
- Component values are reported by writable `SerializedProperty.propertyPath`

Do not guess field names when writing `prefab patch`. Use the output of `prefab inspect --with-values` as the source of truth.

## `prefab patch`

Supported operations:

- `add-child`
- `remove-node`
- `set-node`
- `add-component`
- `remove-component`
- `set-component-values`

Patch saves are wrapped in a `Library/com.yhc509.unity-cli-bridge/backups/` backup for the prefab file and `.meta`. If backup restore itself fails, the bridge returns `BACKUP_RESTORE_FAILED` with backup paths for manual recovery.

Example:

```json
{
  "version": 1,
  "operations": [
    {
      "op": "add-component",
      "target": "/Hitbox[0]",
      "component": {
        "type": "UnityEngine.BoxCollider"
      }
    },
    {
      "op": "set-component-values",
      "target": "/Hitbox[0]",
      "componentType": "UnityEngine.BoxCollider",
      "values": {
        "m_IsTrigger": true,
        "m_Size": { "x": 2, "y": 3, "z": 4 },
        "m_Material": {
          "assetPath": "Assets/Physics/EnemyHit.physicMaterial"
        }
      }
    }
  ]
}
```

## Value Rules

- Vector, Color, Rect, and similar structs are passed as JSON objects
- Asset references are written as `{ "assetPath": "Assets/..." }` or `{ "guid": "..." }`
- Component type lookup prefers the full name. A short name is only allowed when it is unique.
- The same JSON spec format is used by the live CLI and bridge command handlers

## Limits

- The root prefab name is normalized to the prefab file name after save
- Prefab-internal object references and scene object references are not generalized yet
- Advanced nested prefab variant editing is out of scope for now
