# Prefab Workflows

## Defaults

- For a simple empty prefab, prefer `prefab create` over `asset create --type prefab`.
- Before a structural edit, run `prefab inspect --with-values`.
- `prefab inspect` also reads model assets (`.fbx`, `.obj`, `.dae`, `.dxf`, `.3ds`, `.blend`, `.skp`) as read-only. `prefab patch` still requires a `.prefab`.
- Node paths in a patch spec use `/`, `/Visual[0]`, `/Visual[0]/MeshRoot[0]`.
- Component field values follow `SerializedProperty.propertyPath`.

## Create

```bash
ucli prefab create --project "$PROJECT" --path Assets/Prefabs/Enemy.prefab --spec-file /tmp/enemy-prefab.json --force --output compact
```

To start quickly, copy the installed skill's `assets/prefab-create-basic.json` and edit it.

> **The spec must wrap a `root`.** Write `{"root": {"name": "Enemy", "children": [...]}}`. A bare `{"name": "Enemy"}` fails to parse.

## Inspect

```bash
# Structure only (cap depth and drop defaults to save tokens)
ucli prefab inspect --project "$PROJECT" --path Assets/Prefabs/Enemy.prefab --max-depth 2 --omit-defaults --output compact

# Include component values (required before a patch)
ucli prefab inspect --project "$PROJECT" --path Assets/Prefabs/Enemy.prefab --with-values --output compact
```

Read these fields from the inspect result:

- `root.children[].path`
- `components[].type`
- `components[].values`

`--omit-defaults` output is read-only. Feeding it back as a patch input does not restore the omitted fields.

## Patch

```bash
ucli prefab patch --project "$PROJECT" --path Assets/Prefabs/Enemy.prefab --spec-file /tmp/enemy-patch.json --output compact
```

Supported ops:

- `add-child`
- `remove-node`
- `set-node`
- `add-component`
- `remove-component`
- `set-component-values`

Start from the installed skill's `assets/prefab-patch-boxcollider.json`.

### set-node vs set-component-values

Both can edit the same component (for example Transform), but the path schemes differ:

| op | Path scheme | Example | Use |
|---|---|---|---|
| `set-node` | High-level spec keys | `transform.localPosition`, `transform.localScale` | Frequent Transform edits |
| `set-component-values` | `SerializedProperty.propertyPath` | `m_LocalPosition.x`, `m_LocalScale.y` | Any field on any component |

- `set-node` only accepts predefined high-level keys such as Transform and name. **Unknown keys come back as warnings.**
- `set-component-values` uses `SerializedProperty.propertyPath` as-is, so take keys from the inspect `values`.
- Both ops can target the same node. A common pattern is `set-node` for the Transform, then `set-component-values` for the remaining fields.

## Common value shapes

Vector3:

```json
{ "x": 0, "y": 1, "z": 0 }
```

Asset reference:

```json
{ "assetPath": "Assets/Physics/EnemyHit.physicMaterial" }
```

or

```json
{ "guid": "..." }
```

## Limits

- After save, the root prefab name is normalized to the file name.
- Object references inside a prefab, and scene object references, are not generalized yet.
- Do not guess field names. Use the inspect result.
