#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityCli.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class SceneCommandHandler
    {
        // Reused to avoid GC allocations. Safe because all callers run on the
        // main thread and no method using this buffer calls another that also uses it.
        private static readonly List<Component> _componentBuffer = new List<Component>(8);

        private static bool HasDestructiveOperation(ScenePatchSpec spec)
        {
            for (int index = 0; index < spec.Operations.Length; index++)
            {
                ScenePatchOperationSpec operation = spec.Operations[index];
                if (operation == null || string.IsNullOrWhiteSpace(operation.Operation))
                {
                    continue;
                }

                if (string.Equals(operation.Operation, "delete-gameobject", StringComparison.Ordinal)
                    || string.Equals(operation.Operation, "remove-component", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static ScenePatchApplyResult ApplyPatchOperations(Scene scene, ScenePatchOperationSpec[] operations)
        {
            var result = new ScenePatchApplyResult();
            var createdRoots = new List<GameObject>();
            foreach (ScenePatchOperationSpec operation in operations)
            {
                if (operation == null || string.IsNullOrWhiteSpace(operation.Operation))
                {
                    throw new CommandFailureException("SCENE_SPEC_INVALID", "Patch operation `op` is empty.");
                }

                switch (operation.Operation)
                {
                    case "add-gameobject":
                    {
                        Transform? parent = SceneInspector.ResolveParent(scene, operation.Parent, "add-gameobject");
                        if (operation.Node == null)
                        {
                            throw new CommandFailureException("SCENE_SPEC_INVALID", "`add-gameobject` requires `node`.");
                        }

                        createdRoots.Add(AddNode(scene, parent, operation.Node, "add-gameobject"));
                        result.Patched = true;
                        break;
                    }
                    case "modify-gameobject":
                    {
                        GameObject target = SceneInspector.ResolveNode(scene, operation.Target, "modify-gameobject");
                        if (operation.Values is not JObject rawValues)
                        {
                            throw new CommandFailureException("SCENE_SPEC_INVALID", "`modify-gameobject` requires `values`.");
                        }

                        NodeMutationAnalysis analysis = InspectorMutationReaderUtility.AnalyzeNodeMutationValues(rawValues);
                        result.Warnings.AddRange(analysis.Warnings);
                        if (!analysis.HasRecognizedKeys)
                        {
                            break;
                        }

                        SceneInspector.ApplyNodeState(target, rawValues, "modify-gameobject");
                        result.Patched = true;
                        break;
                    }
                    case "delete-gameobject":
                    {
                        GameObject target = SceneInspector.ResolveNode(scene, operation.Target, "delete-gameobject");
                        UnityEngine.Object.DestroyImmediate(target);
                        result.Patched = true;
                        break;
                    }
                    case "add-component":
                    {
                        GameObject target = SceneInspector.ResolveNode(scene, operation.Target, "add-component");
                        if (operation.Component == null)
                        {
                            throw new CommandFailureException("SCENE_SPEC_INVALID", "`add-component` requires `component`.");
                        }

                        AddComponent(target, operation.Component, "add-component");
                        result.Patched = true;
                        break;
                    }
                    case "modify-component":
                    {
                        GameObject target = SceneInspector.ResolveNode(scene, operation.Target, "modify-component");
                        Component component = ResolveComponent(target, operation.ComponentType, operation.ComponentIndex, "modify-component");
                        if (operation.Values == null || operation.Values.Type != JTokenType.Object)
                        {
                            throw new CommandFailureException("SCENE_SPEC_INVALID", "`modify-component` requires `values` as an object.");
                        }

                        SerializedValueApplier.Apply(component, (JObject)operation.Values);
                        result.Patched = true;
                        break;
                    }
                    case "remove-component":
                    {
                        GameObject target = SceneInspector.ResolveNode(scene, operation.Target, "remove-component");
                        Component component = ResolveComponent(target, operation.ComponentType, operation.ComponentIndex, "remove-component");
                        if (component is Transform)
                        {
                            throw new CommandFailureException("SCENE_COMPONENT_INVALID", "Transform cannot be removed.");
                        }

                        UnityEngine.Object.DestroyImmediate(component, true);
                        result.Patched = true;
                        break;
                    }
                    default:
                        throw new CommandFailureException("SCENE_SPEC_INVALID", "Unsupported patch operation: " + operation.Operation);
                }
            }

            GameObject? survivingCreatedRoot = null;
            int survivingCreatedRootCount = 0;
            for (int index = 0; index < createdRoots.Count; index++)
            {
                GameObject candidate = createdRoots[index];
                if (candidate == null)
                {
                    continue;
                }

                survivingCreatedRoot = candidate;
                survivingCreatedRootCount++;
                if (survivingCreatedRootCount > 1)
                {
                    break;
                }
            }

            result.CreatedPath = survivingCreatedRootCount == 1 && survivingCreatedRoot != null
                ? SceneInspector.BuildNodePath(survivingCreatedRoot)
                : null;

            return result;
        }

        private static void NormalizeOperationNames(ScenePatchOperationSpec[] operations)
        {
            for (int index = 0; index < operations.Length; index++)
            {
                ScenePatchOperationSpec operation = operations[index];
                if (operation == null)
                {
                    continue;
                }

                operation.Operation = PatchUtility.NormalizeOperationName(operation.Operation);
            }
        }

        private static void AddNodes(Scene scene, Transform? parent, SceneNodeSpec[]? children, string commandName)
        {
            if (children == null)
            {
                return;
            }

            foreach (SceneNodeSpec childSpec in children)
            {
                AddNode(scene, parent, childSpec, commandName);
            }
        }

        private static GameObject AddNode(Scene scene, Transform? parent, SceneNodeSpec? childSpec, string commandName)
        {
            if (childSpec == null)
            {
                throw new CommandFailureException("SCENE_SPEC_INVALID", commandName + " node spec is empty.");
            }

            string childName = InspectorPathParserUtility.RequireNodeName(childSpec.Name, commandName, "SCENE");
            GameObject child = CreateSceneObject(childName, childSpec.Primitive, commandName);
            SceneManager.MoveGameObjectToScene(child, scene);
            if (parent != null)
            {
                child.transform.SetParent(parent, false);
            }

            SceneInspector.ApplyNodeState(child, childSpec, childName, allowMissingName: false);
            AddComponents(child, childSpec.Components, commandName);
            AddNodes(scene, child.transform, childSpec.Children, commandName);
            return child;
        }

        private static void AddComponents(GameObject target, SceneComponentSpec[]? components, string commandName)
        {
            if (components == null)
            {
                return;
            }

            foreach (SceneComponentSpec componentSpec in components)
            {
                AddComponent(target, componentSpec, commandName);
            }
        }

        private static Component AddComponent(GameObject target, SceneComponentSpec? componentSpec, string commandName)
        {
            if (componentSpec == null || string.IsNullOrWhiteSpace(componentSpec.Type))
            {
                throw new CommandFailureException("SCENE_COMPONENT_INVALID", commandName + " component type is empty.");
            }

            Type componentType = ResolveComponentType(componentSpec.Type, commandName);
            if (componentType == typeof(Transform))
            {
                throw new CommandFailureException("SCENE_COMPONENT_INVALID", "Transform cannot be added directly.");
            }

            Component? component;
            try
            {
                component = target.AddComponent(componentType);
            }
            catch (Exception exception)
            {
                throw new CommandFailureException("SCENE_COMPONENT_INVALID", "Could not add component: " + componentSpec.Type, exception.Message);
            }

            if (component == null)
            {
                throw new CommandFailureException(
                    "SCENE_COMPONENT_ADD_FAILED",
                    "Could not add component: " + componentSpec.Type + " @ " + SceneInspector.BuildNodePath(target));
            }

            if (componentSpec.Values != null)
            {
                SerializedValueApplier.Apply(component, componentSpec.Values);
            }

            return component;
        }

        private static GameObject CreateSceneObject(string name, string? primitive, string commandName)
        {
            if (string.IsNullOrWhiteSpace(primitive))
            {
                return new GameObject(name);
            }

            string normalizedPrimitive = ProtocolConstants.NormalizeScenePrimitive(primitive);
            if (string.IsNullOrWhiteSpace(normalizedPrimitive))
            {
                throw new CommandFailureException(
                    "SCENE_SPEC_INVALID",
                    commandName + " primitive type is not supported: " + primitive + " (supported: " + string.Join(", ", ProtocolConstants.SupportedScenePrimitiveNames) + ")");
            }

            GameObject gameObject = GameObject.CreatePrimitive(ResolvePrimitiveType(normalizedPrimitive));
            gameObject.name = name;
            return gameObject;
        }

        private static PrimitiveType ResolvePrimitiveType(string normalizedPrimitive)
        {
            switch (normalizedPrimitive)
            {
                case "Cube":
                    return PrimitiveType.Cube;
                case "Sphere":
                    return PrimitiveType.Sphere;
                case "Capsule":
                    return PrimitiveType.Capsule;
                case "Cylinder":
                    return PrimitiveType.Cylinder;
                case "Plane":
                    return PrimitiveType.Plane;
                case "Quad":
                    return PrimitiveType.Quad;
                default:
                    throw new InvalidOperationException("Unsupported primitive type: " + normalizedPrimitive);
            }
        }

        private static Component ResolveComponent(GameObject target, string? componentTypeName, int? componentIndex, string commandName)
        {
            Type componentType = ResolveComponentType(componentTypeName, commandName);
            int requestedIndex = componentIndex ?? 0;
            if (requestedIndex < 0)
            {
                throw new CommandFailureException("SCENE_COMPONENT_NOT_FOUND", commandName + " component index is out of range: " + requestedIndex);
            }

            _componentBuffer.Clear();
            target.GetComponents(_componentBuffer);

            int matchedCount = 0;
            for (int index = 0; index < _componentBuffer.Count; index++)
            {
                Component component = _componentBuffer[index];
                if (component == null || !componentType.IsAssignableFrom(component.GetType()))
                {
                    continue;
                }

                if (matchedCount == requestedIndex)
                {
                    return component;
                }

                matchedCount++;
            }

            if (matchedCount == 0)
            {
                throw new CommandFailureException("SCENE_COMPONENT_NOT_FOUND", commandName + " could not find target component: " + componentTypeName);
            }

            throw new CommandFailureException("SCENE_COMPONENT_NOT_FOUND", commandName + " component index is out of range: " + requestedIndex);
        }

        private static Type ResolveComponentType(string? typeName, string commandName)
        {
            string normalized = string.IsNullOrWhiteSpace(typeName) ? string.Empty : typeName.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new CommandFailureException("SCENE_COMPONENT_INVALID", commandName + " component type is empty.");
            }

            Type? exactMatch = null;
            int exactMatchCount = 0;
            IReadOnlyList<Type> exactCandidates = TypeDiscoveryUtility.FindTypesByFullName(normalized);
            for (int index = 0; index < exactCandidates.Count; index++)
            {
                Type candidate = exactCandidates[index];
                if (!IsResolvableComponentType(candidate))
                {
                    continue;
                }

                exactMatch = candidate;
                exactMatchCount++;
                if (exactMatchCount > 1)
                {
                    break;
                }
            }

            if (exactMatchCount == 1 && exactMatch != null)
            {
                return exactMatch;
            }

            if (exactMatchCount > 1)
            {
                throw new CommandFailureException("SCENE_COMPONENT_INVALID", "More than one component type has this full name: " + normalized);
            }

            Type? shortMatch = null;
            int shortMatchCount = 0;
            IReadOnlyList<Type> shortCandidates = TypeDiscoveryUtility.FindTypesByShortName(normalized);
            for (int index = 0; index < shortCandidates.Count; index++)
            {
                Type candidate = shortCandidates[index];
                if (!IsResolvableComponentType(candidate))
                {
                    continue;
                }

                shortMatch = candidate;
                shortMatchCount++;
                if (shortMatchCount > 1)
                {
                    break;
                }
            }

            if (shortMatchCount == 1 && shortMatch != null)
            {
                return shortMatch;
            }

            if (shortMatchCount > 1)
            {
                throw new CommandFailureException("SCENE_COMPONENT_INVALID", "More than one component type shares this short name. Use the full name: " + normalized);
            }

            throw new CommandFailureException("SCENE_COMPONENT_INVALID", "Could not find component type: " + normalized);
        }

        private static bool IsResolvableComponentType(Type type)
        {
            return typeof(Component).IsAssignableFrom(type)
                && !type.IsAbstract
                && !type.ContainsGenericParameters;
        }

        private sealed class ScenePatchApplyResult
        {
            internal string? CreatedPath { get; set; }

            internal bool Patched { get; set; }

            internal List<string> Warnings { get; } = new List<string>();
        }
    }
}
