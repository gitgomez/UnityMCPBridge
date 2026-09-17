using System.IO;
using System.Linq;
using MCPForUnity.Editor.Tools.Audit;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class AuditProjectTests
    {
        private const string TempRoot = "Assets/Temp/AuditProjectTests";
        private const string GhostMetaPath = TempRoot + "/Ghost.asset.meta";
        private EditorBuildSettingsScene[] _originalBuildScenes;

        [SetUp]
        public void SetUp()
        {
            EnsureFolder(TempRoot);
            _originalBuildScenes = EditorBuildSettings.scenes;
        }

        [TearDown]
        public void TearDown()
        {
            EditorBuildSettings.scenes = _originalBuildScenes;
            string ghostFullPath = Path.Combine(Directory.GetCurrentDirectory(), GhostMetaPath);
            if (File.Exists(ghostFullPath))
            {
                File.Delete(ghostFullPath);
            }
            if (AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.DeleteAsset(TempRoot);
            }
            CleanupEmptyParentFolders(TempRoot);
            AssetDatabase.Refresh();
        }

        [Test]
        public void Ping_ReportsAvailableChecks()
        {
            JObject result = ToJObject(AuditProject.HandleCommand(
                new JObject { ["action"] = "ping" }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.GreaterOrEqual(result["data"].Value<int>("checkCount"), 7);
            Assert.IsTrue(result["data"].Value<bool>("readOnly"));
        }

        [Test]
        public void ListChecks_ReturnsNamedChecks()
        {
            JObject result = ToJObject(AuditProject.HandleCommand(
                new JObject { ["action"] = "list_checks" }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.That(result.ToString(), Does.Contain("missing_references"));
            Assert.That(result.ToString(), Does.Contain("package_health"));
        }

        [Test]
        public void RunCompilationCheck_ReturnsSummary()
        {
            JObject result = ToJObject(AuditProject.HandleCommand(new JObject
            {
                ["action"] = "run",
                ["checks"] = new JArray("compilation"),
                ["minimum_severity"] = "info",
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(1, result["data"]?["checksRun"]?.Count());
            Assert.IsNotNull(result["data"]?["errorCount"]);
            Assert.IsNotNull(result["data"]?["items"]);
        }

        [Test]
        public void BuildScenes_MissingSceneIsReportedAsError()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(TempRoot + "/Missing.unity", true),
            };

            JObject result = ToJObject(AuditProject.HandleCommand(new JObject
            {
                ["action"] = "run",
                ["checks"] = new JArray("build_scenes"),
                ["minimum_severity"] = "error",
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.GreaterOrEqual(result["data"].Value<int>("errorCount"), 1);
            Assert.That(result.ToString(), Does.Contain("BUILD_SCENE_MISSING"));
        }

        [Test]
        public void OrphanedMetaFile_IsReported()
        {
            string ghostFullPath = Path.Combine(Directory.GetCurrentDirectory(), GhostMetaPath);
            File.WriteAllText(ghostFullPath, "fileFormatVersion: 2\nguid: 1234567890abcdef1234567890abcdef\n");

            JObject result = ToJObject(AuditProject.HandleCommand(new JObject
            {
                ["action"] = "run",
                ["checks"] = new JArray("orphaned_meta_files"),
                ["search_root"] = TempRoot,
                ["minimum_severity"] = "warning",
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.That(result.ToString(), Does.Contain("ORPHANED_META_FILE"));
            Assert.That(result.ToString(), Does.Contain("Ghost.asset"));
        }

        [Test]
        public void UnknownCheck_ReturnsError()
        {
            JObject result = ToJObject(AuditProject.HandleCommand(new JObject
            {
                ["action"] = "run",
                ["checks"] = new JArray("not_a_check"),
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result.Value<string>("error"), Does.Contain("Unknown audit check"));
        }

        [Test]
        public void InvalidMinimumSeverity_ReturnsError()
        {
            JObject result = ToJObject(AuditProject.HandleCommand(new JObject
            {
                ["action"] = "run",
                ["checks"] = new JArray("compilation"),
                ["minimum_severity"] = "critical",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result.Value<string>("error"), Does.Contain("minimum_severity"));
        }
    }
}
