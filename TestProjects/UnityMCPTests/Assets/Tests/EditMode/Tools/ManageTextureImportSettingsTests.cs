using System;
using System.IO;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManageTextureImportSettingsTests
    {
        private const string Root = "Assets/Temp/ManageTextureImportSettingsTests";
        private string _path;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(Root))
                AssetDatabase.CreateFolder("Assets/Temp", "ManageTextureImportSettingsTests");
            _path = Root + "/Belt_" + Guid.NewGuid().ToString("N") + ".png";
            var texture = new Texture2D(3, 5, TextureFormat.RGBA32, false);
            try
            {
                File.WriteAllBytes(_path, texture.EncodeToPNG());
                AssetDatabase.ImportAsset(_path, ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Root);
        }

        private JObject Apply(JObject settings)
        {
            return JObject.FromObject(ManageTexture.HandleCommand(new JObject
            {
                ["action"] = "set_import_settings",
                ["path"] = _path,
                ["importSettings"] = settings
            }));
        }

        [Test]
        public void ReportedDictionaryAppliesAllImporterSettings()
        {
            var response = Apply(new JObject
            {
                ["textureType"] = "Default", ["sRGBTexture"] = true,
                ["alphaSource"] = "FromInput", ["alphaIsTransparency"] = true,
                ["wrapMode"] = "Clamp", ["filterMode"] = "Bilinear",
                ["maxTextureSize"] = 2048, ["textureCompression"] = "Uncompressed",
                ["mipmapEnabled"] = false, ["isReadable"] = false, ["npotScale"] = "None"
            });
            Assert.IsTrue(response.Value<bool>("success"), response.ToString());
            var importer = (TextureImporter)AssetImporter.GetAtPath(_path);
            Assert.AreEqual(TextureImporterType.Default, importer.textureType);
            Assert.IsTrue(importer.sRGBTexture);
            Assert.AreEqual(TextureImporterAlphaSource.FromInput, importer.alphaSource);
            Assert.IsTrue(importer.alphaIsTransparency);
            Assert.AreEqual(TextureWrapMode.Clamp, importer.wrapMode);
            Assert.AreEqual(FilterMode.Bilinear, importer.filterMode);
            Assert.AreEqual(2048, importer.maxTextureSize);
            Assert.AreEqual(TextureImporterCompression.Uncompressed, importer.textureCompression);
            Assert.IsFalse(importer.mipmapEnabled);
            Assert.IsFalse(importer.isReadable);
            Assert.AreEqual(TextureImporterNPOTScale.None, importer.npotScale);
        }

        [TestCase("None", TextureImporterNPOTScale.None)]
        [TestCase("ToNearest", TextureImporterNPOTScale.ToNearest)]
        [TestCase("ToLarger", TextureImporterNPOTScale.ToLarger)]
        [TestCase("ToSmaller", TextureImporterNPOTScale.ToSmaller)]
        public void NpotModeIsApplied(string name, TextureImporterNPOTScale expected)
        {
            var response = Apply(new JObject { ["npotScale"] = name });
            Assert.IsTrue(response.Value<bool>("success"), response.ToString());
            Assert.AreEqual(expected, ((TextureImporter)AssetImporter.GetAtPath(_path)).npotScale);
        }

        [TestCase("npotScale", "invalid")]
        [TestCase("unknownSetting", "None")]
        public void InvalidSettingsLeavePersistedImporterUnchanged(string key, string value)
        {
            var before = File.ReadAllText(_path + ".meta");
            var response = Apply(new JObject { ["wrapMode"] = "Clamp", [key] = value });
            Assert.IsFalse(response.Value<bool>("success"), response.ToString());
            Assert.AreEqual(before, File.ReadAllText(_path + ".meta"));
        }

        [Test]
        public void CreateRejectsUnknownImportSettingBeforeWritingImage()
        {
            var path = Root + "/MustNotExist.png";
            var response = JObject.FromObject(ManageTexture.HandleCommand(new JObject
            {
                ["action"] = "create", ["path"] = path,
                ["importSettings"] = new JObject { ["unknownSetting"] = true }
            }));
            Assert.IsFalse(response.Value<bool>("success"), response.ToString());
            Assert.IsFalse(File.Exists(path));
        }
    }
}
