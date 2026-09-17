using System.Collections;
using System.Threading.Tasks;
using MCPForUnity.Editor.Tools.PlayMode;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace MCPForUnityTests.PlayMode
{
    public class PlayModeUiToolkitInteractionTests
    {
        private const string DocumentName =
            "MCP_UI_Toolkit_PlayMode_Fixture";

        [UnityTest]
        public IEnumerator RuntimeUiToolkitBackend_HandlesCoreInteractions()
        {
            GameObject fixture = null;
            PanelSettings panelSettings = null;
            RenderTexture targetTexture = null;

            try
            {
                targetTexture = new RenderTexture(960, 540, 0)
                {
                    name = "MCP_UI_Toolkit_Test_Target",
                };
                Assert.IsTrue(targetTexture.Create());

                panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
                panelSettings.name = "MCP UI Toolkit Test Panel";
                panelSettings.targetTexture = targetTexture;
                panelSettings.themeStyleSheet =
                    AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                        "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss");
                Assert.IsNotNull(panelSettings.themeStyleSheet);
                panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                panelSettings.referenceResolution = new Vector2Int(960, 540);

                fixture = new GameObject(DocumentName);
                UIDocument document = fixture.AddComponent<UIDocument>();
                document.enabled = false;
                document.panelSettings = panelSettings;
                document.enabled = true;
                Object.DontDestroyOnLoad(fixture);

                yield return null;

                VisualElement root = document.rootVisualElement;
                root.name = "fixture-root";
                root.style.width = 960;
                root.style.height = 540;

                var status = new Label("ready")
                {
                    name = "status-label",
                };
                Place(status, 340, 20, 300, 40);
                root.Add(status);

                var button = new Button(() => status.text = "clicked")
                {
                    name = "action-button",
                    text = "Run",
                };
                Place(button, 20, 20, 280, 48);
                root.Add(button);

                var input = new TextField("Input")
                {
                    name = "text-input",
                };
                Place(input, 20, 88, 280, 48);
                root.Add(input);

                var password = new TextField("Password")
                {
                    name = "password-input",
                    isPasswordField = true,
                };
                Place(password, 20, 156, 280, 48);
                root.Add(password);

                var toggle = new Toggle("Enabled")
                {
                    name = "feature-toggle",
                };
                Place(toggle, 20, 224, 280, 40);
                root.Add(toggle);

                var scrollView = new ScrollView(ScrollViewMode.Vertical)
                {
                    name = "definition-scroll",
                };
                Place(scrollView, 340, 88, 360, 300);
                for (int index = 0; index < 24; index++)
                {
                    var row = new Label($"Knowledge entry {index:00}")
                    {
                        name = $"knowledge-row-{index:00}",
                    };
                    row.style.height = 36;
                    row.style.flexShrink = 0;
                    scrollView.Add(row);
                }
                root.Add(scrollView);

                yield return null;
                yield return null;

                JObject inspectedByCanonicalPath = Execute(
                    "inspect_ui",
                    "action-button",
                    new JObject
                    {
                        ["document"] = "/" + DocumentName,
                        ["document_search_method"] = "by_path",
                    });
                AssertSuccess(inspectedByCanonicalPath);

                JObject inspected = Execute(
                    "inspect_ui",
                    "action-button");
                AssertSuccess(inspected);
                Assert.IsTrue(
                    inspected["data"].Value<bool>("visible"),
                    inspected.ToString());
                Assert.IsTrue(
                    inspected["data"].Value<bool>("hitTestVisible"),
                    inspected.ToString());

                Task<object> changedWait = InteractPlayMode.HandleCommandAsync(
                    WaitRequest(
                        "status-label",
                        "text_equals",
                        "wait-complete",
                        timeoutSeconds: 1f));
                Assert.IsFalse(
                    changedWait.IsCompleted,
                    changedWait.IsCompleted
                        ? ToJObject(changedWait.Result).ToString()
                        : "wait_ui unexpectedly completed before the UI state changed.");
                yield return null;
                status.text = "wait-complete";
                for (int frame = 0; frame < 180 && !changedWait.IsCompleted; frame++)
                {
                    yield return null;
                }
                Assert.IsTrue(changedWait.IsCompleted, "wait_ui did not complete after the UI state changed.");
                JObject waited = ToJObject(changedWait.Result);
                AssertSuccess(waited);
                Assert.GreaterOrEqual(
                    waited["data"].Value<int>("attempts"),
                    2,
                    waited.ToString());
                Assert.AreEqual(
                    "wait-complete",
                    waited["data"]["state"].Value<string>("text"));

                JObject clicked = Execute("click_ui", "action-button");
                AssertSuccess(clicked);
                yield return null;
                Assert.AreEqual("clicked", status.text);

                JObject textSet = Execute(
                    "set_text",
                    "text-input",
                    new JObject
                    {
                        ["text"] = "plasma",
                        ["submit"] = true,
                    });
                AssertSuccess(textSet);
                Assert.AreEqual("plasma", input.value);
                Assert.IsTrue(
                    textSet["data"].Value<bool>("submitted"),
                    textSet.ToString());

                JObject passwordSet = Execute(
                    "set_text",
                    "password-input",
                    new JObject { ["text"] = "classified" });
                AssertSuccess(passwordSet);
                JObject passwordInspection = Execute(
                    "inspect_ui",
                    "password-input");
                AssertSuccess(passwordInspection);
                Assert.IsTrue(
                    passwordInspection["data"].Value<bool>("textRedacted"),
                    passwordInspection.ToString());
                Assert.IsFalse(
                    passwordInspection["data"].Value<bool>("textAvailable"),
                    passwordInspection.ToString());
                Assert.IsNull(passwordInspection["data"]["text"]?.Value<string>());

                Task<object> sensitiveWait = InteractPlayMode.HandleCommandAsync(
                    WaitRequest(
                        "password-input",
                        "text_equals",
                        "classified",
                        timeoutSeconds: 1f));
                for (int frame = 0; frame < 30 && !sensitiveWait.IsCompleted; frame++)
                {
                    yield return null;
                }
                Assert.IsTrue(sensitiveWait.IsCompleted);
                JObject sensitiveResult = ToJObject(sensitiveWait.Result);
                Assert.IsFalse(
                    sensitiveResult.Value<bool>("success"),
                    sensitiveResult.ToString());
                Assert.AreEqual(
                    "ui_condition_unavailable",
                    sensitiveResult.Value<string>("code"));
                StringAssert.DoesNotContain(
                    "classified",
                    sensitiveResult.ToString());

                Task<object> timeoutWait = InteractPlayMode.HandleCommandAsync(
                    WaitRequest(
                        "status-label",
                        "text_equals",
                        "never-reached",
                        timeoutSeconds: 0.1f));
                double timeoutDeadline = EditorApplication.timeSinceStartup + 2d;
                while (!timeoutWait.IsCompleted
                    && EditorApplication.timeSinceStartup < timeoutDeadline)
                {
                    yield return null;
                }
                Assert.IsTrue(timeoutWait.IsCompleted, "wait_ui did not return its bounded timeout.");
                JObject timeoutResult = ToJObject(timeoutWait.Result);
                Assert.IsFalse(
                    timeoutResult.Value<bool>("success"),
                    timeoutResult.ToString());
                Assert.AreEqual(
                    "wait_ui_timeout",
                    timeoutResult.Value<string>("code"));
                Assert.GreaterOrEqual(
                    timeoutResult["data"].Value<int>("attempts"),
                    2,
                    timeoutResult.ToString());

                JObject toggleSet = Execute(
                    "set_toggle",
                    "feature-toggle",
                    new JObject { ["value"] = true });
                AssertSuccess(toggleSet);
                Assert.IsTrue(toggle.value);

                JObject scrolled = Execute(
                    "scroll_ui",
                    "definition-scroll",
                    new JObject
                    {
                        ["scroll_delta"] = new JArray(0f, -4f),
                    });
                AssertSuccess(scrolled);
                Assert.IsTrue(
                    scrolled["data"].Value<bool>("eventInvoked"),
                    scrolled.ToString());
                Assert.IsTrue(
                    scrolled["data"].Value<bool>("normalizedPositionChanged"),
                    scrolled.ToString());
                Assert.Greater(scrollView.scrollOffset.y, 0f);
            }
            finally
            {
                if (fixture != null)
                {
                    Object.DestroyImmediate(fixture);
                }
                if (panelSettings != null)
                {
                    Object.DestroyImmediate(panelSettings);
                }
                if (targetTexture != null)
                {
                    targetTexture.Release();
                    Object.DestroyImmediate(targetTexture);
                }
            }
        }

        private static void Place(
            VisualElement element,
            float left,
            float top,
            float width,
            float height)
        {
            element.style.position = Position.Absolute;
            element.style.left = left;
            element.style.top = top;
            element.style.width = width;
            element.style.height = height;
        }

        private static JObject Execute(
            string action,
            string elementName,
            JObject extra = null)
        {
            var request = new JObject
            {
                ["action"] = action,
                ["ui_system"] = "ui_toolkit",
                ["document"] = DocumentName,
                ["document_search_method"] = "by_name",
                ["element_name"] = elementName,
            };
            if (extra != null)
            {
                request.Merge(extra);
            }

            object response = InteractPlayMode.HandleCommand(request);
            return response as JObject ?? JObject.FromObject(response);
        }

        private static JObject WaitRequest(
            string elementName,
            string condition,
            string expected,
            float timeoutSeconds)
        {
            return new JObject
            {
                ["action"] = "wait_ui",
                ["ui_system"] = "ui_toolkit",
                ["document"] = DocumentName,
                ["document_search_method"] = "by_name",
                ["element_name"] = elementName,
                ["condition"] = condition,
                ["expected"] = expected,
                ["timeout_seconds"] = timeoutSeconds,
                ["poll_interval_seconds"] = 0.05f,
            };
        }

        private static JObject ToJObject(object response)
        {
            return response as JObject ?? JObject.FromObject(response);
        }

        private static void AssertSuccess(JObject result)
        {
            Assert.IsTrue(
                result.Value<bool>("success"),
                result.ToString());
        }
    }
}
