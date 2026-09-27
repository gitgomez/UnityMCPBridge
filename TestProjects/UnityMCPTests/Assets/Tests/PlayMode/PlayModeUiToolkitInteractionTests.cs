using System.Collections;
using System.Collections.Generic;
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

                // The non-square target texture uses panel top-left or texture bottom-left,
                // independently of the Game View dimensions and PanelSettings scaling.
                Rect panelBounds = document.runtimePanel.visualTree.worldBound;
                Vector2 center = button.worldBound.center;
                float px = (center.x - panelBounds.x) / panelBounds.width;
                float py = (center.y - panelBounds.y) / panelBounds.height;
                JObject coordinateRequest = new JObject {
                    ["action"] = "click_ui", ["ui_system"] = "ui_toolkit", ["document"] = DocumentName,
                    ["position"] = new JArray(px, py),
                };
                Assert.AreEqual("ui_toolkit_screen_space_required", ToJObject(InteractPlayMode.HandleCommand(coordinateRequest)).Value<string>("code"));
                coordinateRequest["coordinate_space"] = "panel_normalized";
                JObject panelClick = ToJObject(InteractPlayMode.HandleCommand(coordinateRequest));
                AssertSuccess(panelClick);
                Assert.AreEqual("clicked", status.text);
                status.text = "ready";
                coordinateRequest["coordinate_space"] = "texture_uv";
                coordinateRequest["position"] = new JArray(px, 1f - py);
                JObject uvClick = ToJObject(InteractPlayMode.HandleCommand(coordinateRequest));
                AssertSuccess(uvClick);
                Assert.AreEqual("texture_uv", uvClick["data"].Value<string>("coordinateSpace"));
                Assert.AreEqual("bottom_left", uvClick["data"]["normalizedPosition"].Value<string>("origin"));
                Assert.That(uvClick["data"]["panelPosition"].Value<float>("x"), Is.EqualTo(panelClick["data"]["panelPosition"].Value<float>("x")).Within(0.001f));
                Assert.That(uvClick["data"]["panelPosition"].Value<float>("y"), Is.EqualTo(panelClick["data"]["panelPosition"].Value<float>("y")).Within(0.001f));
                Assert.AreEqual("clicked", status.text);

                Vector2 lastMappedMove = Vector2.zero;
                EventCallback<PointerMoveEvent> observeMappedMove = evt => lastMappedMove = evt.position;
                root.RegisterCallback(observeMappedMove, TrickleDown.TrickleDown);
                coordinateRequest["action"] = "drag_ui";
                coordinateRequest["end_position"] = new JArray(0.2f, 0.15f);
                coordinateRequest["steps"] = 2;
                JObject uvDrag = ToJObject(InteractPlayMode.HandleCommand(coordinateRequest));
                root.UnregisterCallback(observeMappedMove, TrickleDown.TrickleDown);
                AssertSuccess(uvDrag);
                Assert.AreEqual("bottom_left", uvDrag["data"]["endPosition"].Value<string>("origin"));
                Assert.That(lastMappedMove.x, Is.EqualTo(panelBounds.x + panelBounds.width * 0.2f).Within(0.001f));
                Assert.That(lastMappedMove.y, Is.EqualTo(panelBounds.y + panelBounds.height * 0.85f).Within(0.001f));

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
                Assert.AreEqual("left", clicked["data"].Value<string>("button"));
                yield return null;
                Assert.AreEqual("clicked", status.text);

                JObject nullButton = ToJObject(InteractPlayMode.HandleCommand(new JObject
                {
                    ["action"] = "click_ui", ["ui_system"] = "ui_toolkit",
                    ["document"] = DocumentName, ["element_name"] = "action-button",
                    ["button"] = JValue.CreateNull(),
                }));
                AssertSuccess(nullButton);
                Assert.AreEqual("left", nullButton["data"].Value<string>("button"));

                var pointerEvents = new List<string>();
                int downPointerId = -1;
                EventCallback<PointerDownEvent> rightDown = evt =>
                {
                    if (evt.button != 1) return;
                    downPointerId = evt.pointerId;
                    pointerEvents.Add($"down:{evt.button}:{evt.pressedButtons}");
                };
                EventCallback<PointerUpEvent> rightUp = evt =>
                {
                    if (evt.button != 1) return;
                    Assert.AreEqual(downPointerId, evt.pointerId);
                    pointerEvents.Add($"up:{evt.button}:{evt.pressedButtons}");
                };
                root.RegisterCallback(rightDown, TrickleDown.TrickleDown);
                root.RegisterCallback(rightUp, TrickleDown.TrickleDown);
                status.text = "right-ready";
                JObject rightClicked = Execute("click_ui", "action-button", new JObject { ["button"] = "right" });
                AssertSuccess(rightClicked);
                Assert.AreEqual("right", rightClicked["data"].Value<string>("button"));
                CollectionAssert.AreEqual(new[] { "down:1:2", "up:1:0" }, pointerEvents);
                Assert.AreEqual("right-ready", status.text, "Right click must not invoke the left Button action.");

                root.CapturePointer(PointerId.mousePointerId);
                try
                {
                    JObject busy = Execute("click_ui", "action-button", new JObject { ["button"] = "right" });
                    Assert.AreEqual("pointer_busy", busy.Value<string>("code"));
                    Assert.AreEqual(2, pointerEvents.Count);
                }
                finally { root.ReleasePointer(PointerId.mousePointerId); }

                using (PointerDownEvent held = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0 })) { }
                try
                {
                    JObject busy = Execute("click_ui", "action-button", new JObject { ["button"] = "right" });
                    Assert.AreEqual("pointer_busy", busy.Value<string>("code"));
                    Assert.AreEqual(2, pointerEvents.Count);
                }
                finally
                {
                    using (PointerUpEvent released = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0 })) { }
                }
                root.UnregisterCallback(rightDown, TrickleDown.TrickleDown);
                root.UnregisterCallback(rightUp, TrickleDown.TrickleDown);

                // The next default click must still be a normal left Button activation.
                AssertSuccess(Execute("click_ui", "action-button"));
                Assert.AreEqual("clicked", status.text);

                var dismissible = new VisualElement { name = "dismissible" };
                Place(dismissible, 730, 20, 180, 48);
                root.Add(dismissible);
                yield return null;
                dismissible.RegisterCallback<PointerUpEvent>(evt =>
                {
                    if (evt.button == 1) dismissible.RemoveFromHierarchy();
                });
                AssertSuccess(Execute("click_ui", "dismissible", new JObject { ["button"] = "right" }));
                Assert.IsNull(dismissible.parent, "Right release should be able to close its view.");

                var interrupted = new VisualElement { name = "interrupted" };
                Place(interrupted, 730, 20, 180, 48);
                root.Add(interrupted);
                yield return null;
                interrupted.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button == 1) interrupted.RemoveFromHierarchy();
                });
                JObject partial = Execute("click_ui", "interrupted", new JObject { ["button"] = "right" });
                Assert.AreEqual("pointer_dispatch_interrupted", partial.Value<string>("code"));
                Assert.IsFalse(partial["data"]["eventsInvoked"].Value<bool>("pointerUp"));
                AssertSuccess(Execute("click_ui", "action-button", new JObject { ["button"] = "right" }));

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

        [UnityTest]
        public IEnumerator RuntimeCollections_InspectRevealAndExpandWithoutImplicitMutation()
        {
            var texture = new RenderTexture(960, 540, 0);
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            var fixture = new GameObject(DocumentName);
            try
            {
                Assert.IsTrue(texture.Create());
                settings.targetTexture = texture;
                settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                    "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss");
                settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                var document = fixture.AddComponent<UIDocument>();
                document.panelSettings = settings;
                yield return null;
                var root = document.rootVisualElement;
                var items = new List<string>();
                for (int i = 0; i < 2000; i++) items.Add("item-" + i);
                int clicks = 0;
                var list = new ListView(items, 24, () => new Button(() => clicks++) { name = "row-button" },
                    (element, index) => ((Button)element).text = items[index]) { name = "virtual-list" };
                Place(list, 0, 0, 400, 180);
                root.Add(list);
                var tree = new TreeView { name = "virtual-tree", fixedItemHeight = 24,
                    makeItem = () => new Label(), bindItem = (element, index) => ((Label)element).text = "tree-row" };
                tree.SetRootItems(new List<TreeViewItemData<string>> {
                    new TreeViewItemData<string>(10, "parent", new List<TreeViewItemData<string>> {
                        new TreeViewItemData<string>(20, "child") }) });
                Place(tree, 420, 0, 400, 180);
                root.Add(tree);
                yield return null;
                yield return null;

                var itemAddress = new JObject { ["index"] = 1700, ["query"] = new JObject { ["element_name"] = "row-button" } };
                var options = new JObject { ["collection"] = itemAddress };
                var absent = Execute("inspect_ui", "absent-collection", options);
                AssertSuccess(absent);
                Assert.IsFalse(absent["data"].Value<bool>("realized"));
                Assert.IsFalse(absent["data"].Value<bool>("itemExists"));
                JObject inspected = Execute("inspect_ui", "virtual-list", options);
                AssertSuccess(inspected);
                Assert.IsTrue(inspected["data"].Value<bool>("itemExists"));
                Assert.IsFalse(inspected["data"].Value<bool>("realized"));
                Assert.IsFalse(inspected["data"].Value<bool>("exists"));
                Assert.IsNull(list.GetRootElementForIndex(1700));
                Assert.AreEqual(-1, list.selectedIndex);
                JObject page = Execute("inspect_collection", "virtual-list", new JObject { ["collection"] = new JObject { ["offset"] = 1699, ["limit"] = 2 } });
                AssertSuccess(page);
                Assert.AreEqual(2, ((JArray)page["data"]["items"]).Count);
                Assert.AreEqual(1701, page["data"].Value<int>("nextOffset"));
                Assert.AreEqual("collection_item_not_realized", Execute("click_ui", "virtual-list", options).Value<string>("code"));

                JObject request = WaitRequest("virtual-list", "realized", null, 5);
                request["action"] = "reveal_item";
                request["collection"] = itemAddress.DeepClone();
                Task<object> reveal = InteractPlayMode.HandleCommandAsync(request);
                while (!reveal.IsCompleted) yield return null;
                JObject revealed = ToJObject(reveal.Result);
                AssertSuccess(revealed);
                Assert.IsTrue(revealed["data"].Value<bool>("realized"));
                Assert.IsTrue(revealed["data"].Value<bool>("interactable"));
                Assert.AreEqual(-1, list.selectedIndex, "Reveal must not select an item.");
                AssertSuccess(Execute("click_ui", "virtual-list", options));
                Assert.AreEqual(1, clicks);

                tree.CollapseItem(10);
                yield return null;
                JObject treePage = Execute("inspect_collection", "virtual-tree");
                AssertSuccess(treePage);
                Assert.AreEqual(2, ((JArray)treePage["data"]["items"]).Count, "Logical enumeration includes collapsed nodes.");
                Assert.IsFalse(tree.IsExpanded(10));
                request["element_name"] = "virtual-tree";
                request["collection"] = new JObject { ["id"] = 20 };
                reveal = InteractPlayMode.HandleCommandAsync(request);
                while (!reveal.IsCompleted) yield return null;
                Assert.AreEqual("collection_item_collapsed", ToJObject(reveal.Result).Value<string>("code"));
                Assert.IsFalse(tree.IsExpanded(10));
                request["collection"]["expand_ancestors"] = true;
                reveal = InteractPlayMode.HandleCommandAsync(request);
                while (!reveal.IsCompleted) yield return null;
                AssertSuccess(ToJObject(reveal.Result));
                Assert.IsTrue(tree.IsExpanded(10));
                AssertSuccess(Execute("set_collection_expanded", "virtual-tree", new JObject { ["collection"] = new JObject { ["id"] = 10 }, ["value"] = false }));
                Assert.IsFalse(tree.IsExpanded(10));

                request["element_name"] = "virtual-list";
                request["collection"] = new JObject { ["index"] = 1900 };
                reveal = InteractPlayMode.HandleCommandAsync(request);
                list.itemsSource = new List<string> { "replacement" };
                while (!reveal.IsCompleted) yield return null;
                Assert.AreEqual("collection_changed", ToJObject(reveal.Result).Value<string>("code"));
                list.itemsSource = items;
                request["timeout_seconds"] = 0.1;
                list.style.visibility = Visibility.Hidden;
                reveal = InteractPlayMode.HandleCommandAsync(request);
                while (!reveal.IsCompleted) yield return null;
                Assert.AreEqual("collection_reveal_timeout", ToJObject(reveal.Result).Value<string>("code"));
                list.style.visibility = Visibility.Visible;
                reveal = InteractPlayMode.HandleCommandAsync(request);
                list.RemoveFromHierarchy();
                while (!reveal.IsCompleted) yield return null;
                Assert.AreEqual("collection_reveal_interrupted", ToJObject(reveal.Result).Value<string>("code"));
            }
            finally
            {
                Object.DestroyImmediate(fixture);
                Object.DestroyImmediate(settings);
                texture.Release();
                Object.DestroyImmediate(texture);
            }
        }

        [UnityTest]
        public IEnumerator RuntimeUiToolkitBackend_MapsCameraSurfaceAndRejectsUnsafeGeometry()
        {
            var fixture = new GameObject(DocumentName);
            var cameraObject = new GameObject("MCP_Surface_Camera");
            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screen.name = "MCP_Surface_Screen";
            screen.layer = 31;
            screen.transform.localScale = new Vector3(8, 4, 1);
            screen.transform.rotation = Quaternion.Euler(0, 20, 0);
            var material = new Material(Shader.Find("Unlit/Texture"));
            var texture = new RenderTexture(960, 540, 0);
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            GameObject blocker = null;
            GameObject duplicate = null;
            try
            {
                Assert.IsTrue(texture.Create());
                settings.targetTexture = texture;
                settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                settings.scale = 2;
                settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>("Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss");
                var document = fixture.AddComponent<UIDocument>();
                document.panelSettings = settings;
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 0, -10);
                camera.aspect = 16f / 9;
                camera.cullingMask = 1 << 31;
                camera.farClipPlane = 30;
                material.mainTexture = texture;
                var renderer = screen.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                var root = document.rootVisualElement;
                int clicks = 0, downs = 0;
                var button = new Button(() => clicks++) { name = "surface-button" };
                button.style.position = Position.Absolute;
                button.style.left = button.style.top = 0;
                button.style.width = Length.Percent(100);
                button.style.height = Length.Percent(100);
                root.Add(button);
                Vector2 lastMove = default;
                root.RegisterCallback<PointerDownEvent>(_ => downs++, TrickleDown.TrickleDown);
                root.RegisterCallback<PointerMoveEvent>(e => lastMove = e.position, TrickleDown.TrickleDown);
                button.RegisterCallback<PointerMoveEvent>(e => lastMove = e.position, TrickleDown.TrickleDown);
                yield return null;
                yield return null;
                Physics.SyncTransforms();

                Vector2 PositionForUv(float u, float v)
                {
                    Vector3 point = camera.WorldToViewportPoint(screen.transform.TransformPoint(new Vector3(u - .5f, v - .5f, 0)));
                    return new Vector2(point.x, 1 - point.y);
                }
                var start = PositionForUv(.3f, .7f);
                var end = PositionForUv(.7f, .3f);
                var request = new JObject { ["ui_system"] = "ui_toolkit", ["document"] = DocumentName,
                    ["position"] = new JArray(start.x, start.y), ["coordinate_space"] = "camera_viewport",
                    ["surface"] = new JObject { ["camera"] = cameraObject.GetInstanceID().ToString(), ["target"] = screen.GetInstanceID().ToString() } };
                JObject Call(string action)
                {
                    request["action"] = action;
                    return ToJObject(InteractPlayMode.HandleCommand(request));
                }
                var clicked = Call("click_ui");
                AssertSuccess(clicked);
                Assert.AreEqual(1, clicks);
                var mapped = clicked["data"]["surfaceMapping"];
                Assert.AreEqual(.3f, mapped["textureUv"].Value<float>("x"), .001f);
                Assert.AreEqual(.7f, mapped["textureUv"].Value<float>("y"), .001f);
                Rect bounds = root.panel.visualTree.worldBound;
                Assert.AreEqual(bounds.xMin + bounds.width * .3f, mapped["panelPosition"].Value<float>("x"), .01f);
                Assert.AreEqual(bounds.yMin + bounds.height * .3f, mapped["panelPosition"].Value<float>("y"), .01f);
                AssertSuccess(Call("hover_ui"));
                request["end_position"] = new JArray(end.x, end.y);
                request["steps"] = 4;
                AssertSuccess(Call("drag_ui"));
                Assert.AreEqual(bounds.xMin + bounds.width * .7f, lastMove.x, .01f);
                Assert.AreEqual(bounds.yMin + bounds.height * .7f, lastMove.y, .01f);

                blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                blocker.layer = 31;
                var middle = Vector2.Lerp(start, end, .5f);
                blocker.transform.position = camera.ViewportPointToRay(new Vector3(middle.x, 1 - middle.y)).GetPoint(5);
                blocker.transform.localScale = Vector3.one * .15f;
                Physics.SyncTransforms();
                int before = downs;
                Assert.AreEqual("ui_surface_occluded", Call("drag_ui").Value<string>("code"));
                Assert.AreEqual(before, downs, "Preflight must not send PointerDown when the intermediate path is blocked.");
                request.Remove("end_position");
                request.Remove("steps");
                blocker.transform.position = camera.ViewportPointToRay(new Vector3(start.x, 1 - start.y)).GetPoint(5);
                Physics.SyncTransforms();
                Assert.AreEqual("ui_surface_occluded", Call("click_ui").Value<string>("code"));
                blocker.SetActive(false);
                material.mainTexture = null;
                Assert.AreEqual("ui_surface_texture_mismatch", Call("click_ui").Value<string>("code"));
                material.mainTexture = texture;
                material.mainTextureScale = new Vector2(2, 1);
                Assert.AreEqual("ui_surface_uv_unsupported", Call("click_ui").Value<string>("code"));
                material.mainTextureScale = Vector2.one;
                var block = new MaterialPropertyBlock();
                block.SetTexture("_MainTex", texture);
                renderer.SetPropertyBlock(block);
                Assert.AreEqual("ui_surface_material_unsupported", Call("click_ui").Value<string>("code"));
                renderer.SetPropertyBlock(null);
                screen.GetComponent<MeshCollider>().convex = true;
                Assert.AreEqual("ui_surface_geometry_unsupported", Call("click_ui").Value<string>("code"));
                screen.GetComponent<MeshCollider>().convex = false;
                camera.cullingMask = 0;
                Assert.AreEqual("ui_surface_not_visible", Call("click_ui").Value<string>("code"));
                camera.cullingMask = 1 << 31;
                request["position"] = new JArray(0, 0);
                Assert.AreEqual("ui_surface_raycast_miss", Call("click_ui").Value<string>("code"));
                request["position"] = new JArray(start.x, start.y);
                duplicate = new GameObject(screen.name);
                request["surface"]["target"] = screen.name;
                Assert.AreEqual("ui_surface_resolution_failed", Call("click_ui").Value<string>("code"));
                request["surface"]["target"] = screen.GetInstanceID().ToString();
                request["surface"]["camera"] = "Missing_Camera";
                Assert.AreEqual("ui_surface_camera_resolution_failed", Call("click_ui").Value<string>("code"));
                request["surface"]["camera"] = cameraObject.GetInstanceID().ToString();
                root.RegisterCallback<PointerDownEvent>(_ => renderer.enabled = false, TrickleDown.TrickleDown);
                request["end_position"] = new JArray(end.x, end.y);
                var interrupted = Call("drag_ui");
                Assert.AreEqual("pointer_dispatch_interrupted", interrupted.Value<string>("code"));
                Assert.IsTrue(interrupted["data"]["eventsInvoked"].Value<bool>("pointerUp"));
                renderer.enabled = true;
                request.Remove("end_position");
                AssertSuccess(Call("hover_ui")); // No stuck synthetic mouse button/capture.
            }
            finally
            {
                Object.DestroyImmediate(duplicate);
                Object.DestroyImmediate(blocker);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(screen);
                Object.DestroyImmediate(fixture);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(settings);
                texture.Release();
                Object.DestroyImmediate(texture);
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
