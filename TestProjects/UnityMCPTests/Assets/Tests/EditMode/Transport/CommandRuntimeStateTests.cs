using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Transport
{
    [TestFixture]
    public class CommandRuntimeStateTests
    {
        private JObject _original;
        private JArray _originalEditorSaves;

        [SetUp]
        public void SetUp()
        {
            _original = CommandRuntimeState.Snapshot();
            _originalEditorSaves = CommandRuntimeState.EditorSaveReceiptsSnapshot();
            CommandRuntimeState.ResetForTests("state-test-epoch", 10, 4, 6);
        }

        [TearDown]
        public void TearDown()
        {
            CommandRuntimeState.ResetForTests(
                _original.Value<string>("epoch"),
                _original.Value<long>("revision"),
                _original.Value<long>("scene_revision"),
                _original.Value<long>("asset_revision"),
                _originalEditorSaves);
        }

        [Test]
        public void SceneMutation_AdvancesGlobalAndSceneRevisions()
        {
            CommandRuntimeState.MarkCommandMutation(
                CommandRuntimeContract.ToolPolicies["manage_gameobject"]);

            JObject current = CommandRuntimeState.Snapshot();
            Assert.AreEqual(11, current.Value<long>("revision"));
            Assert.AreEqual(5, current.Value<long>("scene_revision"));
            Assert.AreEqual(6, current.Value<long>("asset_revision"));
        }

        [Test]
        public void MatchingPrecondition_PassesAndStaleRevisionFailsStructurally()
        {
            var expected = new JObject
            {
                ["epoch"] = "state-test-epoch",
                ["after_revision"] = 10,
                ["scene_revision"] = 4
            };

            Assert.DoesNotThrow(() => CommandRuntimeState.ValidateIfMatch(
                expected,
                CommandRuntimeContract.ToolPolicies["manage_gameobject"]));

            expected["after_revision"] = 9;
            CommandStateConflictException error = Assert.Throws<CommandStateConflictException>(
                () => CommandRuntimeState.ValidateIfMatch(
                    expected,
                    CommandRuntimeContract.ToolPolicies["manage_gameobject"]));
            Assert.AreEqual("STATE_CONFLICT", error.Code);
            Assert.IsTrue(error.Data.Value<bool>("refresh_required"));
            Assert.AreEqual(10, error.Data["current"]?.Value<long>("revision"));
        }

        [Test]
        public void UnsupportedTool_RejectsIfMatchBeforeExecution()
        {
            CommandStateConflictException error = Assert.Throws<CommandStateConflictException>(
                () => CommandRuntimeState.ValidateIfMatch(
                    new JObject { ["revision"] = 10 },
                    CommandRuntimeContract.ToolPolicies["find_gameobjects"]));

            Assert.AreEqual("PRECONDITION_NOT_SUPPORTED", error.Code);
        }

        [Test]
        public void ResponseState_ReportsBeforeAndAfterRevisions()
        {
            JObject before = CommandRuntimeState.Snapshot();
            CommandRuntimeState.MarkCommandMutation(
                CommandRuntimeContract.ToolPolicies["manage_gameobject"]);
            JObject after = CommandRuntimeState.Snapshot();

            JObject state = CommandRuntimeState.BuildResponseState(before, after);

            Assert.AreEqual("state-test-epoch", state.Value<string>("epoch"));
            Assert.AreEqual(10, state.Value<long>("before_revision"));
            Assert.AreEqual(11, state.Value<long>("after_revision"));
            Assert.AreEqual(5, state.Value<long>("scene_revision"));
        }

        [Test]
        public void RecordEditorSave_AdvancesSceneAndPublishesExactReceipt()
        {
            CommandRuntimeState.RecordEditorSave(
                "Assets/Scenes/GameScene.unity",
                "scene",
                17_500_000_001_234_567L,
                4096L,
                1_750_000_000_123L);

            JObject current = CommandRuntimeState.Snapshot();
            Assert.AreEqual(11, current.Value<long>("revision"));
            Assert.AreEqual(5, current.Value<long>("scene_revision"));

            JObject receipt = CommandRuntimeState.LastEditorSaveSnapshot();
            Assert.NotNull(receipt);
            Assert.AreEqual("state-test-epoch", receipt.Value<string>("epoch"));
            Assert.AreEqual(1, receipt.Value<long>("sequence"));
            Assert.AreEqual(
                "Assets/Scenes/GameScene.unity",
                receipt.Value<string>("path"));
            Assert.AreEqual(
                1_750_000_000_123_456_700L,
                receipt.Value<long>("mtime_unix_ns"));
            Assert.AreEqual(
                17_500_000_001_234_567L,
                receipt.Value<long>("mtime_unix_100ns"));
            Assert.AreEqual(4096L, receipt.Value<long>("file_size_bytes"));
            Assert.AreEqual("scene", receipt.Value<string>("kind"));
            Assert.AreEqual(1_750_000_000_123L, receipt.Value<long>("saved_unix_ms"));
        }

        [Test]
        public void RecordEditorSave_QueuesSceneAndPrefabReceiptsAndCapsHistory()
        {
            CommandRuntimeState.RecordEditorSave(
                "Assets/Scenes/One.unity", "scene", 100L, 10L, 1L);
            CommandRuntimeState.RecordEditorSave(
                "Assets/Prefabs/One.prefab", "prefab", 200L, 20L, 2L);
            CommandRuntimeState.RecordEditorSave(
                "Assets/Prefabs/One.prefab", "prefab", 200L, 20L, 3L);

            JObject current = CommandRuntimeState.Snapshot();
            Assert.AreEqual(12, current.Value<long>("revision"));
            Assert.AreEqual(5, current.Value<long>("scene_revision"));
            Assert.AreEqual(7, current.Value<long>("asset_revision"));
            JArray initial = CommandRuntimeState.EditorSaveReceiptsSnapshot();
            Assert.AreEqual(2, initial.Count);
            Assert.AreEqual("scene", initial[0]?.Value<string>("kind"));
            Assert.AreEqual("prefab", initial[1]?.Value<string>("kind"));

            for (int index = 0; index < 64; index++)
            {
                CommandRuntimeState.RecordEditorSave(
                    $"Assets/Scenes/Queued{index}.unity",
                    "scene",
                    300L + index,
                    index,
                    3L + index);
            }

            JArray capped = CommandRuntimeState.EditorSaveReceiptsSnapshot();
            Assert.AreEqual(64, capped.Count);
            Assert.AreEqual(3, capped[0]?.Value<long>("sequence"));
            Assert.AreEqual(66, capped[63]?.Value<long>("sequence"));
        }
    }
}
