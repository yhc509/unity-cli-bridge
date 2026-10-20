#nullable enable
using NUnit.Framework;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    public sealed class PrefabCommandHandlerTests
    {
        private const string TestFolder = "Assets/UnityCliBridgePrefabCommandHandlerTests";

        private SceneSetup[]? _previousSceneSetup;

        [SetUp]
        public void SetUp()
        {
            _previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            CloseCurrentPrefabStage(saveIfDirty: true);
            EnsureTestFolder();
        }

        [TearDown]
        public void TearDown()
        {
            CloseCurrentPrefabStage(saveIfDirty: true);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

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
        public void Patch_WhenTargetPrefabStageIsDirty_RefusesBeforeMutation()
        {
            string prefabPath = CreatePrefabAsset("DirtyPatchTarget", "DirtyPatchTarget");
            PrefabStage stage = OpenDirtyPrefabStage(prefabPath);

            CommandFailureException failure = Assert.Throws<CommandFailureException>(() =>
                PatchAddChild(prefabPath, "DirtyPatchTarget", "CliChild"))!;

            AssertPrefabStageDirtyFailure(failure, prefabPath);
            Assert.That(stage.scene.isDirty, Is.True);
            AssertPrefabDoesNotHaveChild(prefabPath, "CliChild");
        }

        [Test]
        public void Inspect_FbxPath_IsNotRejectedForExtension()
        {
            var handler = new PrefabCommandHandler();
            string fbxArgs = ProtocolJson.Serialize(new PrefabInspectArgs { path = "Assets/MissingModel.fbx" });
            var missing = Assert.Throws<InvalidOperationException>(() =>
                handler.Handle(ProtocolConstants.CommandPrefabInspect, fbxArgs))!;
            Assert.That(missing.Message, Does.Not.Contain("must use the `.prefab` extension"));

            string pngArgs = ProtocolJson.Serialize(new PrefabInspectArgs { path = "Assets/Missing.png" });
            CommandFailureException failure = Assert.Throws<CommandFailureException>(() =>
                handler.Handle(ProtocolConstants.CommandPrefabInspect, pngArgs))!;
            Assert.That(failure.ErrorCode, Is.EqualTo("PREFAB_PATH_INVALID"));
            Assert.That(failure.Message, Does.Contain(".fbx"));
            Assert.That(failure.Message, Does.Contain("read-only"));
        }

        [Test]
        public void CreateOverwrite_WhenTargetPrefabStageIsDirty_RefusesEvenWithForce()
        {
            string prefabPath = CreatePrefabAsset("DirtyCreateTarget", "DirtyCreateTarget");
            OpenDirtyPrefabStage(prefabPath);

            var args = new PrefabCreateArgs
            {
                path = prefabPath,
                force = true,
                specJson = "{\"version\":1,\"root\":{\"name\":\"ReplacementRoot\"}}",
            };

            CommandFailureException failure = Assert.Throws<CommandFailureException>(() =>
                new PrefabCommandHandler().Handle(ProtocolConstants.CommandPrefabCreate, ProtocolJson.Serialize(args)))!;

            AssertPrefabStageDirtyFailure(failure, prefabPath);
            AssertPrefabRootName(prefabPath, "DirtyCreateTarget");
        }

        [Test]
        public void Patch_WhenNoPrefabStage_AllowsMutation()
        {
            string prefabPath = CreatePrefabAsset("NoStageTarget", "NoStageTarget");
            CloseCurrentPrefabStage(saveIfDirty: true);

            PatchAddChild(prefabPath, "NoStageTarget", "CliChild");

            AssertPrefabHasChild(prefabPath, "CliChild");
        }

        [Test]
        public void Patch_WhenDifferentPrefabStageIsDirty_AllowsMutation()
        {
            string targetPath = CreatePrefabAsset("OtherStageTarget", "OtherStageTarget");
            string otherPath = CreatePrefabAsset("OtherStageOpen", "OtherStageOpen");
            PrefabStage otherStage = OpenDirtyPrefabStage(otherPath);

            PatchAddChild(targetPath, "OtherStageTarget", "CliChild");

            Assert.That(otherStage.scene.isDirty, Is.True);
            AssertPrefabHasChild(targetPath, "CliChild");
        }

        [Test]
        public void Patch_WhenTargetPrefabStageIsClean_AllowsMutation()
        {
            string prefabPath = CreatePrefabAsset("CleanStageTarget", "CleanStageTarget");
            PrefabStage stage = PrefabStageUtility.OpenPrefab(prefabPath);
            Assert.That(stage.scene.isDirty, Is.False);

            PatchAddChild(prefabPath, "CleanStageTarget", "CliChild");

            AssertPrefabHasChild(prefabPath, "CliChild");
        }

        [Test]
        public void Patch_WhenNestedParentPrefabStageIsDirtyAndChildIsCurrent_RefusesBeforeMutation()
        {
            CreateNestedPrefabAssets(out string parentPath, out string childPath);
            PrefabStage parentStage = OpenDirtyPrefabStage(parentPath);
            PrefabStage? childStage = null;
            try
            {
                Transform nestedInstanceTransform = parentStage.prefabContentsRoot.transform.Find("NestedInner");
                Assert.That(nestedInstanceTransform, Is.Not.Null);
                GameObject nestedInstanceInStage = nestedInstanceTransform.gameObject;
                childStage = PrefabStageUtility.OpenPrefab(childPath, nestedInstanceInStage);

                Assert.That(PrefabStageUtility.GetCurrentPrefabStage(), Is.EqualTo(childStage));
                Assert.That(parentStage.scene.isDirty, Is.True);

                CommandFailureException failure = Assert.Throws<CommandFailureException>(() =>
                    PatchAddChild(parentPath, "NestedOuter", "CliChild"))!;

                AssertPrefabStageDirtyFailure(failure, parentPath);
                AssertPrefabDoesNotHaveChild(parentPath, "CliChild");
            }
            finally
            {
                // Unwind the nested dirty stack we built on purpose, even if an assertion failed.
                // Shared teardown's CloseCurrentPrefabStage only closes the current stage,
                // so clear dirty on the child and parent stages and return to main instead of
                // relying on GoToMainStage's undocumented save-prompt behavior.
                if (childStage != null)
                {
                    childStage.ClearDirtiness();
                }

                parentStage.ClearDirtiness();
                StageUtility.GoToMainStage();
            }

            Assert.That(PrefabStageUtility.GetCurrentPrefabStage(), Is.Null);
        }

        private static void PatchAddChild(string prefabPath, string rootName, string childName)
        {
            var args = new PrefabPatchArgs
            {
                path = prefabPath,
                specJson = "{\"version\":1,\"operations\":[{\"op\":\"add-child\",\"parent\":\"/"
                    + rootName
                    + "[0]\",\"node\":{\"name\":\""
                    + childName
                    + "\"}}]}",
            };

            new PrefabCommandHandler().Handle(ProtocolConstants.CommandPrefabPatch, ProtocolJson.Serialize(args));
        }

        private static PrefabStage OpenDirtyPrefabStage(string prefabPath)
        {
            PrefabStage stage = PrefabStageUtility.OpenPrefab(prefabPath);
            var child = new GameObject("UnsavedStageChild");
            child.transform.SetParent(stage.prefabContentsRoot.transform, false);
            EditorUtility.SetDirty(stage.prefabContentsRoot);
            EditorSceneManager.MarkSceneDirty(stage.scene);
            Assert.That(stage.scene.isDirty, Is.True);
            return stage;
        }

        private static string CreatePrefabAsset(string fileName, string rootName)
        {
            EnsureTestFolder();
            string prefabPath = AssetDatabase.GenerateUniqueAssetPath(TestFolder + "/" + fileName + ".prefab");
            var root = new GameObject(rootName);
            try
            {
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

        private static void CreateNestedPrefabAssets(out string parentPath, out string childPath)
        {
            childPath = CreatePrefabAsset("NestedInner", "NestedInner");
            parentPath = AssetDatabase.GenerateUniqueAssetPath(TestFolder + "/NestedOuter.prefab");
            var parentRoot = new GameObject("NestedOuter");
            try
            {
                GameObject childAsset = AssetDatabase.LoadAssetAtPath<GameObject>(childPath);
                Assert.That(childAsset, Is.Not.Null);
                var childInstance = (GameObject)PrefabUtility.InstantiatePrefab(childAsset);
                Assert.That(childInstance, Is.Not.Null);
                childInstance.transform.SetParent(parentRoot.transform, false);

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(parentRoot, parentPath);
                Assert.That(saved, Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parentRoot);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static void AssertPrefabStageDirtyFailure(CommandFailureException failure, string prefabPath)
        {
            Assert.That(failure.ErrorCode, Is.EqualTo(ProtocolConstants.ErrorPrefabStageDirty));
            Assert.That(failure.Message, Does.Contain(prefabPath));
            Assert.That(failure.Message, Does.Contain("Prefab Stage"));
            Assert.That(failure.Message, Does.Contain("unsaved changes"));
        }

        private static void AssertPrefabHasChild(string prefabPath, string childName)
        {
            Assert.That(PrefabHasChild(prefabPath, childName), Is.True);
        }

        private static void AssertPrefabDoesNotHaveChild(string prefabPath, string childName)
        {
            Assert.That(PrefabHasChild(prefabPath, childName), Is.False);
        }

        private static bool PrefabHasChild(string prefabPath, string childName)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                return root.transform.Find(childName) != null;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void AssertPrefabRootName(string prefabPath, string rootName)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Assert.That(root.name, Is.EqualTo(rootName));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void EnsureTestFolder()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder))
            {
                AssetDatabase.CreateFolder("Assets", "UnityCliBridgePrefabCommandHandlerTests");
            }
        }

        private static void CloseCurrentPrefabStage(bool saveIfDirty)
        {
            PrefabStage? stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                return;
            }

            if (saveIfDirty && stage.scene.isDirty)
            {
                PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath);
                stage.ClearDirtiness();
            }

            StageUtility.GoToMainStage();
        }
    }
}
