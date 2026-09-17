using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Tools.Addressables;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManageAddressablesTests
    {
        private const string TempRoot = "Assets/Temp/ManageAddressablesTests";
        private const string AssetPath = TempRoot + "/Payload.txt";
        private const string SettingsRoot = "Assets/AddressableAssetsData";
        private const string ConfigObjectName = "com.unity.addressableassets";

        [SetUp]
        public void SetUp()
        {
            CleanupAddressablesSettings();
            EnsureFolder(TempRoot);
            File.WriteAllText(
                Path.Combine(Directory.GetCurrentDirectory(), AssetPath),
                "addressables test payload");
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        [TearDown]
        public void TearDown()
        {
            CleanupAddressablesSettings();
            if (AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.DeleteAsset(TempRoot);
            }
            CleanupEmptyParentFolders(TempRoot);
            AssetDatabase.Refresh();
        }

        [Test]
        public void Ping_ReportsInstalledPackage()
        {
            JObject result = Execute("ping");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(result["data"].Value<bool>("installed"), result.ToString());
            Assert.IsNotEmpty(result["data"].Value<string>("version"));
        }

        [Test]
        public void Initialize_CreatesSettingsAndDefaultGroups()
        {
            JObject result = Initialize();

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.That(result["data"].Value<string>("settingsPath"),
                Does.StartWith(SettingsRoot));
            Assert.Greater(result["data"].Value<int>("groupCount"), 0);
            Assert.IsTrue(AssetDatabase.IsValidFolder(SettingsRoot));
        }

        [Test]
        public void Groups_CanBeCreatedSelectedAndRemoved()
        {
            AssertSuccess(Initialize());
            AssertSuccess(Execute("create_group", new JObject
            {
                ["group_name"] = "MCP Test Group",
            }));
            AssertSuccess(Execute("create_group", new JObject
            {
                ["group_name"] = "MCP Default Group",
                ["set_default"] = true,
            }));

            JObject list = Execute("list_groups");
            Assert.IsTrue(list.Value<bool>("success"), list.ToString());
            Assert.That(list.ToString(), Does.Contain("MCP Test Group"));
            Assert.That(list.ToString(), Does.Contain("MCP Default Group"));

            AssertSuccess(Execute("set_default_group", new JObject
            {
                ["group_name"] = "MCP Default Group",
            }));
            JObject removed = Execute("remove_group", new JObject
            {
                ["group_name"] = "MCP Test Group",
            });
            Assert.IsTrue(removed.Value<bool>("success"), removed.ToString());
        }

        [Test]
        public void Entries_CanBeAddedAddressedListedAndRemoved()
        {
            CreateGroupAndEntry();

            JObject list = Execute("list_entries", new JObject
            {
                ["group_name"] = "MCP Test Group",
            });
            Assert.IsTrue(list.Value<bool>("success"), list.ToString());
            Assert.That(list.ToString(), Does.Contain("payload/original"));
            Assert.That(list.ToString(), Does.Contain(AssetPath));

            JObject updated = Execute("set_address", new JObject
            {
                ["asset_path"] = AssetPath,
                ["address"] = "payload/updated",
            });
            Assert.IsTrue(updated.Value<bool>("success"), updated.ToString());
            Assert.That(updated.ToString(), Does.Contain("payload/updated"));

            JObject removed = Execute("remove_entry", new JObject
            {
                ["asset_path"] = AssetPath,
            });
            Assert.IsTrue(removed.Value<bool>("success"), removed.ToString());
            JObject empty = Execute("list_entries", new JObject
            {
                ["group_name"] = "MCP Test Group",
            });
            Assert.AreEqual(0, empty["data"].Value<int>("totalCount"));
        }

        [Test]
        public void Labels_RequireForceWhenTheyAreInUse()
        {
            CreateGroupAndEntry();
            AssertSuccess(Execute("add_label", new JObject { ["label"] = "preload" }));
            AssertSuccess(Execute("set_label", new JObject
            {
                ["asset_path"] = AssetPath,
                ["label"] = "preload",
                ["enabled"] = true,
            }));

            JObject refused = Execute("remove_label", new JObject { ["label"] = "preload" });
            Assert.IsFalse(refused.Value<bool>("success"));
            Assert.That(refused.Value<string>("error"), Does.Contain("force=true"));

            JObject removed = Execute("remove_label", new JObject
            {
                ["label"] = "preload",
                ["force"] = true,
            });
            Assert.IsTrue(removed.Value<bool>("success"), removed.ToString());
            Assert.That(Execute("list_labels").ToString(), Does.Not.Contain("preload"));
        }

        [Test]
        public void Profiles_CanCreateAndSetVariable()
        {
            AssertSuccess(Initialize());
            JObject before = Execute("get_profiles");
            Assert.IsTrue(before.Value<bool>("success"), before.ToString());
            Assert.Greater(before["data"].Value<int>("count"), 0);

            JObject updated = Execute("set_profile_value", new JObject
            {
                ["variable_name"] = "MCP.TestRoot",
                ["value"] = "https://cdn.example.test/content",
                ["create_variable"] = true,
            });
            Assert.IsTrue(updated.Value<bool>("success"), updated.ToString());

            JObject after = Execute("get_profiles");
            Assert.That(after.ToString(), Does.Contain("MCP.TestRoot"));
            Assert.That(after.ToString(), Does.Contain("https://cdn.example.test/content"));
        }

        [Test]
        public void Validate_ReportsCleanInitializedConfiguration()
        {
            CreateGroupAndEntry();

            JObject result = Execute("validate");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(result["data"].Value<bool>("initialized"));
            Assert.IsTrue(result["data"].Value<bool>("valid"), result.ToString());
            Assert.AreEqual(0, result["data"].Value<int>("errorCount"));
        }

        [Test]
        public void InvalidRequests_ReturnClearErrors()
        {
            AssertSuccess(Initialize());
            JObject missingGroup = Execute("add_entry", new JObject
            {
                ["group_name"] = "Does Not Exist",
                ["asset_path"] = AssetPath,
            });
            Assert.IsFalse(missingGroup.Value<bool>("success"));
            Assert.That(missingGroup.Value<string>("error"), Does.Contain("does not exist"));

            JObject badPath = Execute("add_entry", new JObject
            {
                ["asset_path"] = "Packages/not-an-asset.txt",
            });
            Assert.IsFalse(badPath.Value<bool>("success"));
            Assert.That(badPath.Value<string>("error"), Does.Contain("Assets"));
        }

        [Test]
        public void Build_WithDirtyScene_ReturnsClearErrorWithoutOpeningDialog()
        {
            AssertSuccess(Initialize());
            Scene dirtyScene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.MarkSceneDirty(dirtyScene);
            try
            {
                JObject result = Execute("build");

                Assert.IsFalse(result.Value<bool>("success"), result.ToString());
                Assert.That(result.Value<string>("error"), Does.Contain("unsaved changes"));
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void BuildStatus_WithoutQueuedBuild_ReturnsClearError()
        {
            JObject result = Execute("build_status");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.That(result.Value<string>("error"), Does.Contain("No Addressables content build"));
        }

        private static JObject Initialize()
        {
            return Execute("initialize");
        }

        private static void CreateGroupAndEntry()
        {
            AssertSuccess(Initialize());
            AssertSuccess(Execute("create_group", new JObject
            {
                ["group_name"] = "MCP Test Group",
            }));
            AssertSuccess(Execute("add_entry", new JObject
            {
                ["group_name"] = "MCP Test Group",
                ["asset_path"] = AssetPath,
                ["address"] = "payload/original",
            }));
        }

        private static JObject Execute(string action, JObject parameters = null)
        {
            JObject request = parameters ?? new JObject();
            request["action"] = action;
            return ToJObject(ManageAddressables.HandleCommand(request));
        }

        private static void AssertSuccess(JObject result)
        {
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
        }

        private static void CleanupAddressablesSettings()
        {
            EditorBuildSettings.RemoveConfigObject(ConfigObjectName);
            Type defaultObjectType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(
                    "UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject", false))
                .FirstOrDefault(type => type != null);
            FieldInfo cachedSettings = defaultObjectType?.GetField(
                "s_DefaultSettingsObject",
                BindingFlags.Static | BindingFlags.NonPublic);
            cachedSettings?.SetValue(null, null);
            if (AssetDatabase.IsValidFolder(SettingsRoot))
            {
                AssetDatabase.DeleteAsset(SettingsRoot);
            }
            AssetDatabase.Refresh();
        }
    }
}
