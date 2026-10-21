#nullable enable
using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    public sealed class SceneCommandHandlerTests
    {
        private const string TestFolder = "Assets/UnityCliBridgeSceneCommandHandlerTests";

        private SceneSetup[]? _previousSceneSetup;
        private string? _scenePath;
        private string? _materialPath;

        [SetUp]
        public void SetUp()
        {
            _previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            if (!string.IsNullOrWhiteSpace(_materialPath))
            {
                AssetDatabase.DeleteAsset(_materialPath);
            }

            if (!string.IsNullOrWhiteSpace(_scenePath))
            {
                AssetDatabase.DeleteAsset(_scenePath);
            }

            if (AssetDatabase.IsValidFolder(TestFolder))
            {
                AssetDatabase.DeleteAsset(TestFolder);
            }

            if (_previousSceneSetup != null && _previousSceneSetup.Length > 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(_previousSceneSetup);
            }
        }

        [Test]
        public void SetTransform_WhenActiveSceneAlreadyDirty_RefusesBeforeMutation()
        {
            Scene scene = CreateSavedSceneWithTarget("Target", out GameObject target);
            new GameObject("UnrelatedDirty");
            EditorSceneManager.MarkSceneDirty(scene);

            Vector3 beforePosition = target.transform.localPosition;
            var args = new SceneSetTransformArgs
            {
                node = "/Target[0]",
                position = new SceneVector3Value { x = 1, y = 2, z = 3 },
            };

            CommandFailureException failure = Assert.Throws<CommandFailureException>(() =>
                new SceneCommandHandler().Handle(ProtocolConstants.CommandSceneSetTransform, ProtocolJson.Serialize(args)))!;

            AssertDirtySceneFailure(failure);
            Assert.That(target.transform.localPosition, Is.EqualTo(beforePosition));
        }

        [Test]
        public void SetTransform_RotationOnly_LeavesPositionAndScaleUntouched()
        {
            const string argumentsJson = "{\"node\":\"/Target[0]\",\"rotation\":{\"x\":0,\"y\":30,\"z\":0}}";
            GameObject target = PrepareTransformTarget(new Vector3(1f, 2f, 3f), Quaternion.identity, Vector3.one);

            string json = new SceneCommandHandler().Handle(ProtocolConstants.CommandSceneSetTransform, argumentsJson);

            AssertVector3(target.transform.localPosition, 1f, 2f, 3f);
            AssertVector3(target.transform.localScale, 1f, 1f, 1f);
            AssertEuler(target.transform.localEulerAngles, 0f, 30f, 0f);
            JObject payload = JObject.Parse(json);
            Assert.That(payload["position"], Is.Null);
            Assert.That(payload["scale"], Is.Null);
            AssertVectorToken(payload["rotation"], 0f, 30f, 0f);
        }

        [Test]
        public void SetTransform_ScaleOnly_LeavesPositionAndRotationUntouched()
        {
            const string argumentsJson = "{\"node\":\"/Target[0]\",\"scale\":{\"x\":2,\"y\":3,\"z\":4}}";
            Quaternion rotation = Quaternion.Euler(0f, 30f, 0f);
            GameObject target = PrepareTransformTarget(new Vector3(1f, 2f, 3f), rotation, Vector3.one);

            string json = new SceneCommandHandler().Handle(ProtocolConstants.CommandSceneSetTransform, argumentsJson);

            AssertVector3(target.transform.localPosition, 1f, 2f, 3f);
            AssertEuler(target.transform.localEulerAngles, 0f, 30f, 0f);
            AssertVector3(target.transform.localScale, 2f, 3f, 4f);
            JObject payload = JObject.Parse(json);
            Assert.That(payload["position"], Is.Null);
            Assert.That(payload["rotation"], Is.Null);
            AssertVectorToken(payload["scale"], 2f, 3f, 4f);
        }

        [Test]
        public void AssignMaterial_WhenActiveSceneAlreadyDirty_RefusesBeforeMutation()
        {
            Scene scene = CreateSavedSceneWithTarget("Target", out GameObject target, typeof(MeshFilter), typeof(MeshRenderer));
            MeshRenderer meshRenderer = target.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = Array.Empty<Material>();
            Assert.That(EditorSceneManager.SaveScene(scene, _scenePath!), Is.True);

            string materialPath = CreateMaterialAsset();
            new GameObject("UnrelatedDirty");
            EditorSceneManager.MarkSceneDirty(scene);

            int beforeMaterialCount = meshRenderer.sharedMaterials.Length;
            var args = new SceneAssignMaterialArgs
            {
                node = "/Target[0]",
                material = materialPath,
            };

            CommandFailureException failure = Assert.Throws<CommandFailureException>(() =>
                new SceneCommandHandler().Handle(ProtocolConstants.CommandSceneAssignMaterial, ProtocolJson.Serialize(args)))!;

            AssertDirtySceneFailure(failure);
            Assert.That(meshRenderer.sharedMaterials.Length, Is.EqualTo(beforeMaterialCount));
        }

        [Test]
        public void Inspect_OmitDefaults_KeepsDisabledRendererInsideNestedPrefab()
        {
            EnsureTestFolder();
            string innerPath = CreatePrimitivePrefab("NestedInner", "NestedInner", rendererEnabled: true);
            string outerPath = CreateOuterPrefab("NestedOuter", innerPath);
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject outerAsset = AssetDatabase.LoadAssetAtPath<GameObject>(outerPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(outerAsset);
            Assert.That(instance, Is.Not.Null);
            MeshRenderer renderer = instance.GetComponentInChildren<MeshRenderer>();
            Assert.That(renderer, Is.Not.Null);
            renderer.enabled = false;
            _scenePath = AssetDatabase.GenerateUniqueAssetPath(TestFolder + "/NestedInspect.unity");
            Assert.That(EditorSceneManager.SaveScene(scene, _scenePath), Is.True);

            string json = SceneInspector.BuildInspectPayload(_scenePath, scene, withValues: true, maxDepth: null, omitDefaults: true, _scenePath);

            Assert.That(json, Does.Contain("\"enabled\":false"));
            Assert.That(json, Does.Contain("\"m_Enabled\":false"));
            Assert.That(json, Does.Contain(innerPath));
            Assert.That(json, Does.Contain("\"nested\":true"));
            Assert.That(json, Does.Not.Contain("\"enabled\":true"));
        }

        private static string CreatePrimitivePrefab(string fileName, string rootName, bool rendererEnabled)
        {
            EnsureTestFolder();
            string prefabPath = AssetDatabase.GenerateUniqueAssetPath(TestFolder + "/" + fileName + ".prefab");
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.name = rootName;
            try
            {
                MeshRenderer renderer = root.GetComponent<MeshRenderer>();
                Assert.That(renderer, Is.Not.Null);
                renderer.enabled = rendererEnabled;
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                Assert.That(saved, Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return prefabPath;
        }

        private static string CreateOuterPrefab(string fileName, string innerPath)
        {
            EnsureTestFolder();
            string prefabPath = AssetDatabase.GenerateUniqueAssetPath(TestFolder + "/" + fileName + ".prefab");
            var parentRoot = new GameObject(fileName);
            try
            {
                GameObject childAsset = AssetDatabase.LoadAssetAtPath<GameObject>(innerPath);
                Assert.That(childAsset, Is.Not.Null);
                var childInstance = (GameObject)PrefabUtility.InstantiatePrefab(childAsset);
                Assert.That(childInstance, Is.Not.Null);
                childInstance.transform.SetParent(parentRoot.transform, false);
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(parentRoot, prefabPath);
                Assert.That(saved, Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parentRoot);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return prefabPath;
        }

        private static void EnsureTestFolder()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder))
            {
                AssetDatabase.CreateFolder("Assets", "UnityCliBridgeSceneCommandHandlerTests");
            }
        }

        private GameObject PrepareTransformTarget(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Scene scene = CreateSavedSceneWithTarget("Target", out GameObject target);
            target.transform.localPosition = position;
            target.transform.localRotation = rotation;
            target.transform.localScale = scale;
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True);
            Assert.That(scene.isDirty, Is.False);
            return target;
        }

        private static void AssertVector3(Vector3 actual, float x, float y, float z)
        {
            Assert.That(actual.x, Is.EqualTo(x).Within(0.001f));
            Assert.That(actual.y, Is.EqualTo(y).Within(0.001f));
            Assert.That(actual.z, Is.EqualTo(z).Within(0.001f));
        }

        private static void AssertEuler(Vector3 actual, float x, float y, float z)
        {
            Assert.That(Mathf.DeltaAngle(x, actual.x), Is.EqualTo(0f).Within(0.05f));
            Assert.That(Mathf.DeltaAngle(y, actual.y), Is.EqualTo(0f).Within(0.05f));
            Assert.That(Mathf.DeltaAngle(z, actual.z), Is.EqualTo(0f).Within(0.05f));
        }

        private static void AssertVectorToken(JToken? token, float x, float y, float z)
        {
            Assert.That(token, Is.Not.Null);
            Assert.That(token!.Value<float>("x"), Is.EqualTo(x).Within(0.001f));
            Assert.That(token.Value<float>("y"), Is.EqualTo(y).Within(0.001f));
            Assert.That(token.Value<float>("z"), Is.EqualTo(z).Within(0.001f));
        }

        private Scene CreateSavedSceneWithTarget(string targetName, out GameObject target, params Type[] components)
        {
            EnsureTestFolder();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            target = new GameObject(targetName, components);
            _scenePath = AssetDatabase.GenerateUniqueAssetPath(TestFolder + "/DirtyShortcut.unity");

            Assert.That(EditorSceneManager.SaveScene(scene, _scenePath!), Is.True);
            Assert.That(scene.isDirty, Is.False);
            return scene;
        }

        private string CreateMaterialAsset()
        {
            EnsureTestFolder();

            _materialPath = AssetDatabase.GenerateUniqueAssetPath(TestFolder + "/ShortcutMaterial.mat");
            Shader? shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader!);
            AssetDatabase.CreateAsset(material, _materialPath!);
            return _materialPath!;
        }

        private void AssertDirtySceneFailure(CommandFailureException failure)
        {
            Assert.That(failure.ErrorCode, Is.EqualTo(ProtocolConstants.ErrorSceneDirty));
            Assert.That(failure.Message, Does.Contain(_scenePath!));
            Assert.That(failure.Message, Does.Contain("unsaved changes"));
        }
    }
}
