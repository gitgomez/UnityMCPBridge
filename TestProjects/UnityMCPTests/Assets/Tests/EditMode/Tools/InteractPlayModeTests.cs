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
