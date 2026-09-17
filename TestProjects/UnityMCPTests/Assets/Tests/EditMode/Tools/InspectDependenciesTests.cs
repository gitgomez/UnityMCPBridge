using MCPForUnity.Editor.Tools.Dependencies;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class InspectDependenciesTests
    {
        private const string TempRoot = "Assets/Temp/InspectDependenciesTests";
        private const string TexturePath = TempRoot + "/Dependency.asset";
        private const string MaterialPath = TempRoot + "/Dependent.mat";

        [SetUp]
        public void SetUp()
        {
            EnsureFolder(TempRoot);
            var texture = new Texture2D(2, 2) { name = "Dependency" };
            AssetDatabase.CreateAsset(texture, TexturePath);

            Shader shader = Shader.Find("Standard")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Unlit/Texture");
            Assert.IsNotNull(shader, "A test shader must be available.");
            var material = new Material(shader) { name = "Dependent" };
            string textureProperty = material.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
            material.SetTexture(textureProperty, texture);
            AssetDatabase.CreateAsset(material, MaterialPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.DeleteAsset(TempRoot);
            }
            CleanupEmptyParentFolders(TempRoot);
            AssetDatabase.Refresh();
        }

        [Test]
        public void Ping_ReportsReadOnlyInspection()
        {
            JObject result = ToJObject(InspectDependencies.HandleCommand(
                new JObject { ["action"] = "ping" }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(result["data"].Value<bool>("readOnly"));
        }

        [Test]
        public void Dependencies_ReturnsReferencedTexture()
        {
            JObject result = ToJObject(InspectDependencies.HandleCommand(new JObject
            {
                ["action"] = "dependencies",
                ["target"] = MaterialPath,
                ["recursive"] = false,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            JArray items = result["data"]?["Items"] as JArray
                ?? result["data"]?["items"] as JArray;
            Assert.IsNotNull(items, result.ToString());
            Assert.That(items.ToString(), Does.Contain(TexturePath));
        }

        [Test]
        public void Dependents_ReturnsMaterialUsingTexture()
        {
            JObject result = ToJObject(InspectDependencies.HandleCommand(new JObject
            {
                ["action"] = "dependents",
                ["target"] = TexturePath,
                ["search_root"] = TempRoot,
                ["recursive"] = false,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.That(result.ToString(), Does.Contain(MaterialPath));
            Assert.GreaterOrEqual(result["data"].Value<int>("scannedCount"), 2);
        }

        [Test]
        public void Impact_ReportsBothDirections()
        {
            JObject result = ToJObject(InspectDependencies.HandleCommand(new JObject
            {
                ["action"] = "impact",
                ["target"] = MaterialPath,
                ["search_root"] = TempRoot,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.GreaterOrEqual(result["data"].Value<int>("dependencyCount"), 1);
            Assert.IsNotNull(result["data"]?["dependents"]);
        }

        [Test]
        public void MissingReferences_CleanMaterialReturnsNoIssues()
        {
            JObject result = ToJObject(InspectDependencies.HandleCommand(new JObject
            {
                ["action"] = "missing_references",
                ["target"] = MaterialPath,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(0, result["data"].Value<int>("issueCount"), result.ToString());
        }

        [Test]
        public void UnknownTarget_ReturnsError()
        {
            JObject result = ToJObject(InspectDependencies.HandleCommand(new JObject
            {
                ["action"] = "dependencies",
                ["target"] = TempRoot + "/Missing.asset",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result.Value<string>("error"), Does.Contain("not found"));
        }

        [Test]
        public void Cycles_CanScanARestrictedRoot()
        {
            JObject result = ToJObject(InspectDependencies.HandleCommand(new JObject
            {
                ["action"] = "cycles",
                ["search_root"] = TempRoot,
                ["scan_limit"] = 20,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsNotNull(result["data"]?["cycles"]);
            Assert.GreaterOrEqual(result["data"].Value<int>("scannedCount"), 2);
        }
    }
}
