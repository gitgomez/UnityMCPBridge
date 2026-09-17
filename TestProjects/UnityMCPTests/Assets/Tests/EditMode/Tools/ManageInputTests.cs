using System;
using System.Linq;
using MCPForUnity.Editor.Tools.Input;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManageInputTests
    {
        private const string TempRoot = "Assets/Temp/ManageInputTests";
        private const string AssetPath = TempRoot + "/TestControls.inputactions";
        private const string PlayerName = "ManageInputTests_Player";

        [SetUp]
        public void SetUp()
        {
            EnsureFolder(TempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            GameObject player = GameObject.Find(PlayerName);
            if (player != null)
            {
                UnityEngine.Object.DestroyImmediate(player);
            }
            if (AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.DeleteAsset(TempRoot);
            }
            CleanupEmptyParentFolders(TempRoot);
            AssetDatabase.Refresh();
        }

        [Test]
        public void Ping_ReportsInstalledInputSystem()
        {
            JObject result = Execute("ping");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(result["data"].Value<bool>("installed"), result.ToString());
            Assert.IsNotEmpty(result["data"].Value<string>("version"));
        }

        [Test]
        public void CreateGetListAndDelete_ManageAssetLifecycle()
        {
            JObject created = CreateAsset();
            Assert.IsTrue(created.Value<bool>("success"), created.ToString());

            JObject get = Execute("get", new JObject
            {
                ["path"] = AssetPath,
                ["include_json"] = true,
            });
            Assert.IsTrue(get.Value<bool>("success"), get.ToString());
            Assert.AreEqual("TestControls", get["data"].Value<string>("name"));
            Assert.IsNotNull(get["data"]?["json"]);

            JObject list = Execute("list_assets");
            Assert.IsTrue(list.Value<bool>("success"), list.ToString());
            Assert.That(list.ToString(), Does.Contain(AssetPath));

            JObject deleted = Execute("delete", new JObject { ["path"] = AssetPath });
            Assert.IsTrue(deleted.Value<bool>("success"), deleted.ToString());
            Assert.IsNull(AssetDatabase.LoadMainAssetAtPath(AssetPath));
        }

        [Test]
        public void AuthoringActions_CreateMapActionBindingAndScheme()
        {
            Assert.IsTrue(CreateAsset().Value<bool>("success"));
            AssertSuccess(Execute("add_action_map", new JObject
            {
                ["path"] = AssetPath,
                ["map_name"] = "Player",
            }));
            AssertSuccess(Execute("add_action", new JObject
            {
                ["path"] = AssetPath,
                ["map_name"] = "Player",
                ["action_name"] = "Move",
                ["action_type"] = "Value",
                ["expected_control_type"] = "Vector2",
            }));
            AssertSuccess(Execute("add_binding", new JObject
            {
                ["path"] = AssetPath,
                ["map_name"] = "Player",
                ["action_name"] = "Move",
                ["binding_path"] = "<Gamepad>/leftStick",
                ["groups"] = "Gamepad",
            }));
            AssertSuccess(Execute("add_control_scheme", new JObject
            {
                ["path"] = AssetPath,
                ["scheme_name"] = "Gamepad",
                ["devices"] = new JArray(new JObject
                {
                    ["device_path"] = "<Gamepad>",
                    ["optional"] = false,
                }),
            }));

            JObject get = Execute("get", new JObject { ["path"] = AssetPath });
            Assert.IsTrue(get.Value<bool>("success"), get.ToString());
            Assert.AreEqual(1, get["data"].Value<int>("mapCount"));
            Assert.AreEqual(1, get["data"].Value<int>("controlSchemeCount"));
            Assert.AreEqual(1, get["data"]?["maps"]?[0]?.Value<int>("actionCount"));
            Assert.AreEqual(1, get["data"]?["maps"]?[0]?.Value<int>("bindingCount"));
            Assert.That(get.ToString(), Does.Contain("Vector2"));
        }

        [Test]
        public void RemoveActions_RemoveAuthoredItems()
        {
            BuildMinimalAsset();
            AssertSuccess(Execute("remove_binding", new JObject
            {
                ["path"] = AssetPath,
                ["map_name"] = "Player",
                ["action_name"] = "Jump",
                ["binding_index"] = 0,
            }));
            AssertSuccess(Execute("remove_action", new JObject
            {
                ["path"] = AssetPath,
                ["map_name"] = "Player",
                ["action_name"] = "Jump",
            }));
            AssertSuccess(Execute("remove_control_scheme", new JObject
            {
                ["path"] = AssetPath,
                ["scheme_name"] = "Keyboard",
            }));
            AssertSuccess(Execute("remove_action_map", new JObject
            {
                ["path"] = AssetPath,
                ["map_name"] = "Player",
            }));

            JObject get = Execute("get", new JObject { ["path"] = AssetPath });
            Assert.AreEqual(0, get["data"].Value<int>("mapCount"));
            Assert.AreEqual(0, get["data"].Value<int>("controlSchemeCount"));
        }

        [Test]
        public void Validate_ReportsImportedAssetAsValid()
        {
            BuildMinimalAsset();

            JObject result = Execute("validate", new JObject { ["path"] = AssetPath });

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(result["data"].Value<bool>("valid"), result.ToString());
            Assert.AreEqual(0, result["data"].Value<int>("errorCount"));
            Assert.That(result["data"].Value<string>("importedType"), Does.Contain("InputActionAsset"));
        }

        [Test]
        public void AssignPlayerInput_AddsAndConfiguresComponent()
        {
            BuildMinimalAsset();
            var player = new GameObject(PlayerName);

            JObject result = Execute("assign_player_input", new JObject
            {
                ["path"] = AssetPath,
                ["target"] = PlayerName,
                ["default_map"] = "Player",
                ["default_scheme"] = "Keyboard",
                ["notification_behavior"] = "InvokeUnityEvents",
            });

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(result["data"].Value<bool>("componentAdded"));
            Component component = player.GetComponents<Component>().FirstOrDefault(item =>
                item != null && item.GetType().FullName == "UnityEngine.InputSystem.PlayerInput");
            Assert.IsNotNull(component, result.ToString());
        }

        [Test]
        public void InvalidParameters_ReturnClearErrors()
        {
            Assert.IsTrue(CreateAsset().Value<bool>("success"));
            JObject missingMap = Execute("add_action", new JObject
            {
                ["path"] = AssetPath,
                ["action_name"] = "Jump",
            });
            Assert.IsFalse(missingMap.Value<bool>("success"));
            Assert.That(missingMap.Value<string>("error"), Does.Contain("map_name"));

            JObject badPath = Execute("create", new JObject
            {
                ["path"] = "../Outside.inputactions",
            });
            Assert.IsFalse(badPath.Value<bool>("success"));
            Assert.That(badPath.Value<string>("error"), Does.Contain("Assets"));
        }

        private static JObject CreateAsset()
        {
            return Execute("create", new JObject
            {
                ["path"] = AssetPath,
                ["name"] = "TestControls",
            });
        }

        private static void BuildMinimalAsset()
        {
            AssertSuccess(CreateAsset());
            AssertSuccess(Execute("add_action_map", new JObject
            {
                ["path"] = AssetPath,
                ["map_name"] = "Player",
            }));
            AssertSuccess(Execute("add_action", new JObject
            {
                ["path"] = AssetPath,
                ["map_name"] = "Player",
                ["action_name"] = "Jump",
            }));
            AssertSuccess(Execute("add_binding", new JObject
            {
                ["path"] = AssetPath,
                ["map_name"] = "Player",
                ["action_name"] = "Jump",
                ["binding_path"] = "<Keyboard>/space",
                ["groups"] = "Keyboard",
            }));
            AssertSuccess(Execute("add_control_scheme", new JObject
            {
                ["path"] = AssetPath,
                ["scheme_name"] = "Keyboard",
                ["devices"] = new JArray(new JObject { ["device_path"] = "<Keyboard>" }),
            }));
        }

        private static JObject Execute(string action, JObject parameters = null)
        {
            JObject request = parameters ?? new JObject();
            request["action"] = action;
            return ToJObject(ManageInput.HandleCommand(request));
        }

        private static void AssertSuccess(JObject result)
        {
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
        }
    }
}
