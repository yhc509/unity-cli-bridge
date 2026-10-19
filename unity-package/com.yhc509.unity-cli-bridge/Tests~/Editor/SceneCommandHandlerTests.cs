#nullable enable
using System;
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

        private static void EnsureTestFolder()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder))
            {
                AssetDatabase.CreateFolder("Assets", "UnityCliBridgeSceneCommandHandlerTests");
            }
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
