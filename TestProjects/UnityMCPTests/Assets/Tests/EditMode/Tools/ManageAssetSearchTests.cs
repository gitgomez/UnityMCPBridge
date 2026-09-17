using System;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManageAssetSearchTests
    {
        private const string TempFolder = "Assets/MCPForUnityTests_Temp/AssetSearch";
        private string _alphaName;
        private string _betaName;

        [SetUp]
        public void SetUp()
        {
            Directory.CreateDirectory(TempFolder);
            string suffix = Guid.NewGuid().ToString("N");
            _alphaName = $"MCP_Search_Alpha_{suffix}";
            _betaName = $"MCP_Search_Beta_{suffix}";
            File.WriteAllText($"{TempFolder}/{_alphaName}.txt", "alpha");
            File.WriteAllText($"{TempFolder}/{_betaName}.txt", "beta");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(TempFolder);
            string parent = "Assets/MCPForUnityTests_Temp";
            if (AssetDatabase.IsValidFolder(parent)
                && AssetDatabase.FindAssets(string.Empty, new[] { parent }).Length == 0)
            {
                AssetDatabase.DeleteAsset(parent);
            }
        }

        [Test]
        public void SearchPatterns_UsesOrSemanticsAndPaginatesBeforeMaterialization()
        {
            JObject first = ToJObject(ManageAsset.HandleCommand(new JObject
            {
                ["action"] = "search",
                ["path"] = TempFolder,
                ["searchPatterns"] = new JArray(_alphaName, _betaName),
                ["filterType"] = "TextAsset",
                ["pageSize"] = 1,
                ["pageNumber"] = 1,
            }));
            JObject second = ToJObject(ManageAsset.HandleCommand(new JObject
            {
                ["action"] = "search",
                ["path"] = TempFolder,
                ["searchPatterns"] = new JArray(_alphaName, _betaName),
                ["filterType"] = "TextAsset",
                ["pageSize"] = 1,
                ["pageNumber"] = 2,
            }));

            Assert.IsTrue(first.Value<bool>("success"), first.ToString());
            Assert.IsTrue(second.Value<bool>("success"), second.ToString());
            Assert.AreEqual(2, first["data"].Value<int>("totalAssets"));
            Assert.AreEqual("or", first["data"].Value<string>("queryMode"));
            Assert.AreEqual(1, first["data"]["assets"].Count());
            Assert.AreEqual(1, second["data"]["assets"].Count());
            Assert.AreNotEqual(
                first["data"]["assets"][0].Value<string>("path"),
                second["data"]["assets"][0].Value<string>("path"));
        }

        [TestCase(0, 1, "invalid_page_size")]
        [TestCase(501, 1, "invalid_page_size")]
        [TestCase(10, 0, "invalid_page_number")]
        public void Search_RejectsInvalidPagination(
            int pageSize,
            int pageNumber,
            string expectedCode)
        {
            JObject result = ToJObject(ManageAsset.HandleCommand(new JObject
            {
                ["action"] = "search",
                ["path"] = TempFolder,
                ["pageSize"] = pageSize,
                ["pageNumber"] = pageNumber,
            }));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(expectedCode, result.Value<string>("code"));
        }

        private static JObject ToJObject(object value)
        {
            return value as JObject ?? JObject.FromObject(value);
        }
    }
}
