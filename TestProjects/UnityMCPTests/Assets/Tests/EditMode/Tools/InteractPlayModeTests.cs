using MCPForUnity.Editor.Tools.PlayMode;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine.UIElements;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class InteractPlayModeTests
    {
        [Test]
        public void Ping_ReportsRuntimeEventSystemSupport()
        {
            JObject result = Execute("ping");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(result["data"].Value<bool>("supported"), result.ToString());
            Assert.AreEqual(
                "top_left",
                result["data"].Value<string>("coordinateOrigin"));
            Assert.AreEqual(
                "runtime_event_system",
                result["data"].Value<string>("backend"));
            Assert.IsTrue(
                result["data"]["uiSystems"]["uiToolkit"]
                    .Value<bool>("supported"));
            Assert.AreEqual(
                "runtime_ui_toolkit",
                result["data"]["uiSystems"]["uiToolkit"]
                    .Value<string>("backend"));
        }

        [TestCase("click_ui", "ugui", "right", "ui_toolkit_required")]
        [TestCase("click_ui", "ui_toolkit", "middle", "invalid_click_button")]
        [TestCase("click_ui", "ui_toolkit", "RIGHT", "invalid_click_button")]
        [TestCase("ping", "ui_toolkit", "left", "invalid_click_parameters")]
        [TestCase("drag_ui", "ui_toolkit", "right", "invalid_click_parameters")]
        [TestCase("wait_ui", "ui_toolkit", "right", "invalid_click_parameters")]
        public async Task ClickButton_RejectsInvalidParameters(string action, string uiSystem,
            string button, string code)
        {
            var request = new JObject
            {
                ["action"] = action, ["ui_system"] = uiSystem, ["button"] = button,
            };
            JObject result = ToJObject(await InteractPlayMode.HandleCommandAsync(request));
            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(code, result.Value<string>("code"));
        }

        [TestCase(1)]
        [TestCase(false)]
        public async Task Collection_RejectsInvalidIndexTypes(object index)
        {
            var request = new JObject { ["action"] = "reveal_item", ["ui_system"] = "ui_toolkit",
                ["collection"] = new JObject { ["index"] = JToken.FromObject(index) } };
            if (index is int) request["collection"]["id"] = 1;
            JObject result = ToJObject(await InteractPlayMode.HandleCommandAsync(request));
            Assert.AreEqual("invalid_collection_parameters", result.Value<string>("code"));
        }

        [TestCase("inspect_collection", "{\"limit\":101}")]
        [TestCase("inspect_collection", "{\"offset\":100001}")]
        [TestCase("reveal_item", "{\"index\":-1}")]
        [TestCase("reveal_item", "{\"id\":1,\"expand_ancestors\":\"true\"}")]
        [TestCase("inspect_ui", "{\"id\":1,\"query\":{}}")]
        [TestCase("set_collection_expanded", "{\"id\":1}")]
        public async Task Collection_RejectsInvalidOptions(string action, string address)
        {
            var request = new JObject { ["action"] = action, ["ui_system"] = "ui_toolkit", ["collection"] = JObject.Parse(address) };
            JObject result = ToJObject(await InteractPlayMode.HandleCommandAsync(request));
            Assert.AreEqual("invalid_collection_parameters", result.Value<string>("code"));
        }

        [TestCase(1)]
        [TestCase(false)]
        public void ClickButton_RejectsNonString(object button)
        {
            JObject result = Execute("click_ui", new JObject { ["button"] = JToken.FromObject(button) });
            Assert.AreEqual("invalid_click_button", result.Value<string>("code"));
        }

        [Test]
        public void Ping_AdvertisesUiToolkitClickButtons()
        {
            JObject result = Execute("ping");
            CollectionAssert.AreEqual(new[] { "left", "right" },
                result["data"]["uiSystems"]["uiToolkit"]["clickButtons"].ToObject<string[]>());
        }

        [Test]
        public void ClickUi_RejectsEditModeWithoutSideEffects()
        {
            JObject result = Execute("click_ui", new JObject
            {
                ["position"] = new JArray(0.5f, 0.5f),
            });

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.That(result.Value<string>("error"), Does.Contain("Play Mode"));
        }

        [TestCase("inspect_ui")]
        [TestCase("set_text")]
        [TestCase("set_toggle")]
        [TestCase("drag_ui")]
        [TestCase("scroll_ui")]
        public void RuntimeUiActions_RejectEditModeWithoutSideEffects(string action)
        {
            JObject result = Execute(action, new JObject
            {
                ["target"] = "Canvas/Target",
                ["text"] = "not-applied",
                ["value"] = true,
            });

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("play_mode_required", result.Value<string>("code"));
            Assert.That(result.Value<string>("error"), Does.Contain("Play Mode"));
        }

        [Test]
        public void UnknownAction_ReturnsBoundedActionList()
        {
            JObject result = Execute("invoke_component");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.That(result.Value<string>("error"), Does.Contain("click_ui"));
            Assert.That(result.Value<string>("error"), Does.Contain("inspect_ui"));
            Assert.That(result.Value<string>("error"), Does.Contain("set_text"));
            Assert.That(result.Value<string>("error"), Does.Contain("set_toggle"));
            Assert.That(result.Value<string>("error"), Does.Contain("drag_ui"));
            Assert.That(result.Value<string>("error"), Does.Contain("scroll_ui"));
        }

        [Test]
        public void UiToolkitAction_RejectsEditModeBeforeResolvingDocument()
        {
            JObject result = Execute("inspect_ui", new JObject
            {
                ["ui_system"] = "ui_toolkit",
                ["document"] = "RuntimeUI",
                ["element_name"] = "status",
            });

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("play_mode_required", result.Value<string>("code"));
        }

        [Test]
        public void InvalidUiSystem_IsRejectedDeterministically()
        {
            JObject result = Execute("inspect_ui", new JObject
            {
                ["ui_system"] = "imgui",
            });

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("invalid_ui_system", result.Value<string>("code"));
        }

        [Test]
        public async Task WaitUi_RejectsMissingConditionBeforeInspection()
        {
            object response = await InteractPlayMode.HandleCommandAsync(
                new JObject
                {
                    ["action"] = "wait_ui",
                    ["target"] = "Canvas/Target",
                });
            JObject result = ToJObject(response);

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(
                "invalid_wait_condition",
                result.Value<string>("code"));
        }

        [Test]
        public async Task WaitUi_RejectsOutOfRangeTimeoutBeforeInspection()
        {
            object response = await InteractPlayMode.HandleCommandAsync(
                new JObject
                {
                    ["action"] = "wait_ui",
                    ["target"] = "Canvas/Target",
                    ["condition"] = "visible",
                    ["timeout_seconds"] = 31,
                });
            JObject result = ToJObject(response);

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(
                "invalid_wait_timeout",
                result.Value<string>("code"));
        }

        [Test]
        public async Task WaitUi_AcceptsNumericIntervalsUnderCommaDecimalCulture()
        {
            CultureInfo originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                object response = await InteractPlayMode.HandleCommandAsync(
                    new JObject
                    {
                        ["action"] = "wait_ui",
                        ["target"] = "Canvas/Target",
                        ["condition"] = "visible",
                        ["timeout_seconds"] = 0.1d,
                        ["poll_interval_seconds"] = 0.05d,
                    });
                JObject result = ToJObject(response);

                Assert.IsFalse(result.Value<bool>("success"), result.ToString());
                Assert.AreEqual(
                    "play_mode_required",
                    result.Value<string>("code"),
                    result.ToString());
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [Test]
        public void UiToolkitQuery_ResolvesUniqueNameClassAndType()
        {
            var root = new VisualElement { name = "root" };
            var button = new Button { name = "start-button" };
            button.AddToClassList("primary");
            root.Add(button);
            var context = new PlayModeUiToolkitActions.DocumentContext(
                null,
                null,
                root,
                null);
            var query = new PlayModeUiToolkitActions.ElementQuery(
                "start-button",
                "primary",
                "Button",
                null);

            PlayModeUiToolkitActions.ElementResolution result =
                PlayModeUiToolkitActions.ResolveElement(context, query);

            Assert.IsTrue(result.Success);
            Assert.AreSame(button, result.Element);
        }

        [Test]
        public void UiToolkitQuery_RequiresIndexForAmbiguousMatches()
        {
            var root = new VisualElement { name = "root" };
            root.Add(new Label { name = "row" });
            var second = new Label { name = "row" };
            root.Add(second);
            var context = new PlayModeUiToolkitActions.DocumentContext(
                null,
                null,
                root,
                null);

            PlayModeUiToolkitActions.ElementResolution ambiguous =
                PlayModeUiToolkitActions.ResolveElement(
                    context,
                    new PlayModeUiToolkitActions.ElementQuery(
                        "row",
                        null,
                        null,
                        null));
            PlayModeUiToolkitActions.ElementResolution indexed =
                PlayModeUiToolkitActions.ResolveElement(
                    context,
                    new PlayModeUiToolkitActions.ElementQuery(
                        "row",
                        null,
                        null,
                        1));

            Assert.IsFalse(ambiguous.Success);
            Assert.AreEqual(
                "ui_toolkit_element_ambiguous",
                ambiguous.Code);
            Assert.IsTrue(indexed.Success);
            Assert.AreSame(second, indexed.Element);
        }

        [Test]
        public void UiToolkitQuery_TraversesRealizedPhysicalHierarchy()
        {
            var root = new VisualElement { name = "root" };
            var collection = new PhysicalHierarchyContainer
            {
                name = "collection",
            };
            var realizedButton = new Button { name = "realized-action" };
            collection.hierarchy.Add(realizedButton);
            root.Add(collection);
            var context = new PlayModeUiToolkitActions.DocumentContext(
                null,
                null,
                root,
                null);
            var query = new PlayModeUiToolkitActions.ElementQuery(
                "realized-action",
                null,
                "Button",
                null);

            PlayModeUiToolkitActions.ElementResolution result =
                PlayModeUiToolkitActions.ResolveElement(context, query);

            Assert.IsTrue(result.Success);
            Assert.AreSame(realizedButton, result.Element);
        }

        [TestCase("{}")]
        [TestCase("[]")]
        [TestCase("{\"camera\":\"Cam\"}")]
        [TestCase("{\"camera\":true,\"target\":\"Screen\"}")]
        [TestCase("{\"camera\":\"Cam\",\"target\":\"Screen\",\"uv\":1}")]
        [TestCase("{\"camera\":\"Cam\",\"target\":\"Screen\",\"target_search_method\":\"wrong\"}")]
        public void SurfaceMapping_RejectsInvalidOptionsBeforePlayMode(string surface)
        {
            JObject result = Execute("click_ui", new JObject { ["ui_system"] = "ui_toolkit", ["document"] = "UI",
                ["position"] = new JArray(0.5, 0.5), ["coordinate_space"] = "camera_viewport", ["surface"] = JToken.Parse(surface) });
            Assert.AreEqual("invalid_coordinate_space", result.Value<string>("code"), result.ToString());
        }

        private sealed class PhysicalHierarchyContainer : VisualElement
        {
            private readonly VisualElement content;

            internal PhysicalHierarchyContainer()
            {
                content = new VisualElement { name = "logical-content" };
                hierarchy.Add(content);
            }

            public override VisualElement contentContainer => content;
        }

        private static JObject Execute(string action, JObject parameters = null)
        {
            JObject request = parameters ?? new JObject();
            request["action"] = action;
            return ToJObject(InteractPlayMode.HandleCommand(request));
        }
    }
}
