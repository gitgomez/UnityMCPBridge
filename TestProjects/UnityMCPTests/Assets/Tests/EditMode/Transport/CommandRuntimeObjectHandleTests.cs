using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnityTests.Editor.Transport
{
    [TestFixture]
    public class CommandRuntimeObjectHandleTests
    {
        private GameObject _gameObject;
        private JObject _originalState;
        private JArray _originalEditorSaves;

        [SetUp]
        public void SetUp()
        {
            _originalState = CommandRuntimeState.Snapshot();
            _originalEditorSaves = CommandRuntimeState.EditorSaveReceiptsSnapshot();
            CommandRuntimeState.ResetForTests("handle-test-epoch", 20, 8, 12);
            _gameObject = new GameObject("RuntimeHandle_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_gameObject);
            }
            CommandRuntimeState.ResetForTests(
                _originalState.Value<string>("epoch"),
                _originalState.Value<long>("revision"),
                _originalState.Value<long>("scene_revision"),
                _originalState.Value<long>("asset_revision"),
                _originalEditorSaves);
        }

        [Test]
        public void SessionLocalHandle_ResolvesOnlyWithinItsEpoch()
        {
            JObject handle = CommandRuntimeObjectHandle.BuildSceneObjectHandle(_gameObject);
            handle["global_object_id"] = null;
            handle["scene_guid"] = null;

            Assert.IsTrue(CommandRuntimeObjectHandle.TryResolveSceneObject(
                handle,
                out GameObject resolved));
            Assert.AreSame(_gameObject, resolved);

            handle["epoch"] = "different-epoch";
            Assert.IsFalse(CommandRuntimeObjectHandle.TryResolveSceneObject(
                handle,
                out _));
        }

        [Test]
        public void GameObjectSerializer_AddsHandleWithoutRemovingInstanceId()
        {
            JObject serialized = JObject.FromObject(
                GameObjectSerializer.GetGameObjectData(_gameObject));

            Assert.AreEqual(_gameObject.GetInstanceID(), serialized.Value<int>("instanceID"));
            Assert.AreEqual("scene_object", serialized["handle"]?.Value<string>("kind"));
            Assert.AreEqual(
                "handle-test-epoch",
                serialized["handle"]?.Value<string>("epoch"));
        }

        [Test]
        public void FindGameObjects_ReturnsHandlesBesideLegacyInstanceIds()
        {
            JObject response = JObject.FromObject(FindGameObjects.HandleCommand(
                new JObject
                {
                    ["searchMethod"] = "by_name",
                    ["searchTerm"] = _gameObject.name,
                    ["includeInactive"] = true
                }));
            JObject data = (JObject)response["data"];

            Assert.IsTrue(response.Value<bool>("success"));
            Assert.AreEqual(1, ((JArray)data["instanceIDs"]).Count);
            Assert.AreEqual(1, ((JArray)data["handles"]).Count);
            Assert.AreEqual(
                _gameObject.GetInstanceID(),
                data["handles"]?[0]?.Value<int>("instance_id"));
        }

        [Test]
        public void SavedSceneHandle_ResolvesAfterSceneReload()
        {
            string folderName = $"StableHandle_{Guid.NewGuid():N}";
            string folderPath = AssetDatabase.GUIDToAssetPath(
                AssetDatabase.CreateFolder("Assets", folderName));
            string scenePath = $"{folderPath}/Scene.unity";

            try
            {
                UnityEngine.Object.DestroyImmediate(_gameObject);
                _gameObject = null;
                Scene savedScene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene,
                    NewSceneMode.Single);
                var persistedObject = new GameObject("PersistedHandleTarget");

                Assert.IsTrue(EditorSceneManager.SaveScene(savedScene, scenePath));
                JObject handle = CommandRuntimeObjectHandle.BuildSceneObjectHandle(
                    persistedObject);
                StringAssert.StartsWith(
                    "GlobalObjectId_V1-",
                    handle.Value<string>("global_object_id"));

                // Prove that resolution does not accidentally use the old session-local ID.
                handle["instance_id"] = int.MaxValue;
                handle["epoch"] = "epoch-before-reload";

                Scene reopenedScene = EditorSceneManager.OpenScene(
                    scenePath,
                    OpenSceneMode.Single);

                Assert.IsTrue(CommandRuntimeObjectHandle.TryResolveSceneObject(
                    handle,
                    out GameObject resolved));
                Assert.AreEqual("PersistedHandleTarget", resolved.name);
                Assert.AreEqual(scenePath, resolved.scene.path);
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(folderPath);
            }
        }
    }
}
