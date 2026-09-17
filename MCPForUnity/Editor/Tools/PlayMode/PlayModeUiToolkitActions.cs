using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Tools.PlayMode
{
    /// <summary>
    /// Deterministic runtime UI Toolkit inspection and interaction. This is a
    /// separate backend from uGUI's EventSystem path because VisualElements are
    /// not GameObjects and live in a retained panel tree.
    /// </summary>
    internal static class PlayModeUiToolkitActions
    {
        private static readonly PropertyInfo PseudoStatesProperty =
            typeof(VisualElement).GetProperty(
                "pseudoStates", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly HashSet<string> KeyCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Escape", "Tab", "Return", "Space",
            "LeftArrow", "RightArrow", "UpArrow", "DownArrow",
            "Backspace", "Delete", "Home", "End", "PageUp", "PageDown",
        };

        private const int MaxInputCharacters = 16 * 1024;
        private const int MaxScannedElements = 4096;
        private const int MaxElementIndex = 1023;
        private const float VisibilityEpsilon = 0.0001f;

        internal static bool IsRequested(ToolParams p)
        {
            return string.Equals(
                p.Get("ui_system"),
                "ui_toolkit",
                StringComparison.OrdinalIgnoreCase);
        }

        internal static object Handle(string action, ToolParams p)
        {
            switch (action)
            {
                case "click_ui":
                    return Click(p);
                case "inspect_ui":
                    return Inspect(p);
                case "set_text":
                    return SetText(p);
                case "set_toggle":
                    return SetToggle(p);
                case "drag_ui":
                    return Drag(p);
                case "scroll_ui":
                    return Scroll(p);
                case "hover_ui":
                    return Hover(p);
                case "key_ui":
                    return Key(p);
                default:
                    return ErrorResponse.FromCode(
                        "ui_toolkit_action_unsupported",
                        $"UI Toolkit does not support action '{action}'.");
            }
        }

        internal static object DescribeSupport()
        {
            int documentCount = UnityEngine.Resources
                .FindObjectsOfTypeAll<UIDocument>()
                .Count(document => document != null
                    && document.gameObject.scene.IsValid());

            return new
            {
                supported = true,
                documentCount,
                backend = "runtime_ui_toolkit",
                hoverSupported = PseudoStatesProperty != null,
                keyCodes = KeyCodes.OrderBy(value => value).ToArray(),
                keyModifiers = new[] { "Shift", "Control", "Alt", "Command" },
                keyNavigationEvents = false,
                addressing = new
                {
                    document = new[] { "by_id", "by_name", "by_path" },
                    element = new[]
                    {
                        "element_name",
                        "element_class",
                        "element_type",
                        "element_index",
                    },
                },
                coordinateSpace = "normalized_panel",
                coordinateOrigin = "top_left",
            };
        }

        private static object Inspect(ToolParams p)
        {
            object playModeError = RequirePlayMode(
                allowPaused: true,
                "inspect_ui");
            if (playModeError != null)
            {
                return playModeError;
            }

            if (!TryReadElementQuery(
                    p,
                    required: true,
                    out ElementQuery query,
                    out string queryError))
            {
                return ErrorResponse.FromCode(
                    "invalid_ui_toolkit_query",
                    queryError);
            }

            DocumentResolution documentResolution = ResolveDocument(
                p,
                includeInactive: true);
            if (documentResolution.Missing)
            {
                return new SuccessResponse(
                    "Runtime UI Toolkit document does not currently exist.",
                    new
                    {
                        exists = false,
                        documentExists = false,
                        document = p.Get("document"),
                        query = query.Describe(),
                        backend = "runtime_ui_toolkit",
                    });
            }
            if (!documentResolution.Success)
            {
                return ErrorResponse.FromCode(
                    documentResolution.Code,
                    documentResolution.Error);
            }

            ElementResolution elementResolution = ResolveElement(
                documentResolution.Context,
                query);
            if (elementResolution.Missing)
            {
                return new SuccessResponse(
                    "Runtime UI Toolkit element does not currently exist.",
                    new
                    {
                        exists = false,
                        documentExists = true,
                        document = DescribeDocument(documentResolution.Context),
                        query = query.Describe(),
                        backend = "runtime_ui_toolkit",
                    });
            }
            if (!elementResolution.Success)
            {
                return ErrorResponse.FromCode(
                    elementResolution.Code,
                    elementResolution.Error);
            }

            bool includeText = p.GetBool("include_text", true);
            return new SuccessResponse(
                "Runtime UI Toolkit element inspected.",
                BuildInspection(
                    documentResolution.Context,
                    elementResolution.Element,
                    query,
                    includeText));
        }

        private static object SetText(ToolParams p)
        {
            object playModeError = RequirePlayMode(
                allowPaused: false,
                "set_text");
            if (playModeError != null)
            {
                return playModeError;
            }

            if (!TryResolveRequiredElement(
                    p,
                    out DocumentContext context,
                    out VisualElement requestedElement,
                    out ElementQuery query,
                    out object resolutionError))
            {
                return resolutionError;
            }

            JToken textToken = p.GetRaw("text");
            if (textToken == null || textToken.Type == JTokenType.Null)
            {
                return ErrorResponse.FromCode(
                    "text_required",
                    "'text' is required for set_text, including when empty.");
            }
            if (textToken.Type != JTokenType.String)
            {
                return ErrorResponse.FromCode(
                    "invalid_text",
                    "'text' must be a string.");
            }

            string requestedText = textToken.Value<string>() ?? string.Empty;
            if (requestedText.Length > MaxInputCharacters)
            {
                return ErrorResponse.FromCode(
                    "text_too_large",
                    $"'text' exceeds the bounded limit of {MaxInputCharacters} characters.");
            }

            if (!TryFindAssociatedElement(
                    requestedElement,
                    context.Root,
                    out TextField textField,
                    out bool ambiguous))
            {
                return ErrorResponse.FromCode(
                    "input_field_required",
                    ambiguous
                        ? $"UI Toolkit query '{query}' contains multiple TextFields; target one uniquely."
                        : $"UI Toolkit element '{DescribeElementLabel(requestedElement)}' is not associated with a TextField.");
            }
            if (!IsDocumentMutable(context)
                || !textField.enabledInHierarchy
                || textField.isReadOnly)
            {
                return ErrorResponse.FromCode(
                    "ui_not_interactable",
                    $"Runtime UI Toolkit TextField '{DescribeElementLabel(textField)}' is not interactable.");
            }

            string previousText = textField.value ?? string.Empty;
            textField.Focus();
            textField.value = requestedText;
            string storedText = textField.value ?? string.Empty;

            bool submitted = false;
            if (p.GetBool("submit", false))
            {
                using (NavigationSubmitEvent submitEvent =
                    NavigationSubmitEvent.GetPooled(EventModifiers.None))
                {
                    submitEvent.target = textField;
                    textField.SendEvent(submitEvent);
                    submitted = true;
                }
            }

            EditorApplication.QueuePlayerLoopUpdate();
            bool sensitive = p.GetBool("sensitive", false)
                || textField.isPasswordField;
            return new SuccessResponse(
                submitted
                    ? "Runtime UI Toolkit text was updated and submitted."
                    : "Runtime UI Toolkit text was updated.",
                new
                {
                    document = DescribeDocument(context),
                    element = DescribeElement(textField, context.Root),
                    changed = !string.Equals(
                        previousText,
                        storedText,
                        StringComparison.Ordinal),
                    submitted,
                    sensitive,
                    textRedacted = true,
                    requestedLength = sensitive
                        ? (int?)null
                        : requestedText.Length,
                    storedLength = sensitive
                        ? (int?)null
                        : storedText.Length,
                    backend = "runtime_ui_toolkit",
                });
        }

        private static object SetToggle(ToolParams p)
        {
            object playModeError = RequirePlayMode(
                allowPaused: false,
                "set_toggle");
            if (playModeError != null)
            {
                return playModeError;
            }

            if (!TryResolveRequiredElement(
                    p,
                    out DocumentContext context,
                    out VisualElement requestedElement,
                    out ElementQuery query,
                    out object resolutionError))
            {
                return resolutionError;
            }

            if (!TryReadRequiredBool(p.GetRaw("value"), out bool desiredValue))
            {
                return ErrorResponse.FromCode(
                    "value_required",
                    "'value' must be true or false for set_toggle.");
            }
            if (!TryFindAssociatedElement(
                    requestedElement,
                    context.Root,
                    out Toggle toggle,
                    out bool ambiguous))
            {
                return ErrorResponse.FromCode(
                    "toggle_required",
                    ambiguous
                        ? $"UI Toolkit query '{query}' contains multiple Toggles; target one uniquely."
                        : $"UI Toolkit element '{DescribeElementLabel(requestedElement)}' is not associated with a Toggle.");
            }
            if (!IsDocumentMutable(context) || !toggle.enabledInHierarchy)
            {
                return ErrorResponse.FromCode(
                    "ui_not_interactable",
                    $"Runtime UI Toolkit Toggle '{DescribeElementLabel(toggle)}' is not interactable.");
            }

            bool previousValue = toggle.value;
            if (previousValue != desiredValue)
            {
                toggle.value = desiredValue;
            }
            bool storedValue = toggle.value;
            EditorApplication.QueuePlayerLoopUpdate();

            if (storedValue != desiredValue)
            {
                return ErrorResponse.FromCode(
                    "toggle_value_rejected",
                    $"Runtime UI Toolkit Toggle '{DescribeElementLabel(toggle)}' did not retain the requested value.",
                    new
                    {
                        requestedValue = desiredValue,
                        actualValue = storedValue,
                    });
            }

            return new SuccessResponse(
                previousValue == storedValue
                    ? "Runtime UI Toolkit Toggle already had the requested value."
                    : "Runtime UI Toolkit Toggle value was updated.",
                new
                {
                    document = DescribeDocument(context),
                    element = DescribeElement(toggle, context.Root),
                    previousValue,
                    value = storedValue,
                    changed = previousValue != storedValue,
                    backend = "runtime_ui_toolkit",
                });
        }

        private static object Click(ToolParams p)
        {
            object playModeError = RequirePlayMode(
                allowPaused: false,
                "click_ui");
            if (playModeError != null)
            {
                return playModeError;
            }

            if (!TryResolvePointerStart(
                    p,
                    out PointerAddress address,
                    out object addressError))
            {
                return addressError;
            }

            string sceneBefore = SceneManager.GetActiveScene().name;
            SendPointerDown(address.HitElement, address.PanelPoint);
            SendPointerUp(address.HitElement, address.PanelPoint);
            EditorApplication.QueuePlayerLoopUpdate();

            string sceneAfter = EditorApplication.isPlaying
                ? SceneManager.GetActiveScene().name
                : null;
            return new SuccessResponse(
                "Runtime UI Toolkit click was dispatched through the panel event system.",
                new
                {
                    document = DescribeDocument(address.Context),
                    requestedElement = address.RequestedElement == null
                        ? null
                        : DescribeElement(
                            address.RequestedElement,
                            address.Context.Root),
                    hitElement = DescribeElement(
                        address.HitElement,
                        address.Context.Root),
                    normalizedPosition = DescribeNormalized(
                        address.NormalizedPosition),
                    panelPosition = DescribeVector(address.PanelPoint),
                    eventsInvoked = new
                    {
                        pointerDown = true,
                        pointerUp = true,
                    },
                    activeSceneAtDispatch = sceneBefore,
                    activeSceneAfterInput = sceneAfter,
                    backend = "runtime_ui_toolkit",
                    note = "Pointer events were dispatched synchronously; scheduled UI callbacks may complete on later player frames.",
                });
        }

        private static object Hover(ToolParams p)
        {
            object playModeError = RequirePlayMode(allowPaused: false, "hover_ui");
            if (playModeError != null)
                return playModeError;
            if (!TryResolvePointerStart(p, out PointerAddress address, out object addressError))
                return addressError;

            object documentDescription = DescribeDocument(address.Context);
            object hitDescription = DescribeElement(address.HitElement, address.Context.Root);
            var systemEvent = new Event
            {
                type = EventType.MouseMove,
                mousePosition = address.PanelPoint,
                button = -1,
            };
            using (PointerMoveEvent pointerEvent = PointerMoveEvent.GetPooled(systemEvent))
            {
                if (pointerEvent.pressedButtons != 0
                    || address.Context.Panel.GetCapturingElement(PointerId.mousePointerId) != null)
                {
                    return ErrorResponse.FromCode(
                        "pointer_busy",
                        "hover_ui requires a mouse pointer with no pressed buttons or active capture.");
                }
                // Leave the target unset so the panel performs normal picking and hover transitions.
                address.Context.Panel.visualTree.SendEvent(pointerEvent);
            }
            EditorApplication.QueuePlayerLoopUpdate();
            return new SuccessResponse(
                "Runtime UI Toolkit hover move was dispatched through the panel event system.",
                new
                {
                    document = documentDescription,
                    hitElement = hitDescription,
                    normalizedPosition = DescribeNormalized(address.NormalizedPosition),
                    panelPosition = DescribeVector(address.PanelPoint),
                    eventsInvoked = new { pointerMove = true, pointerDown = false, pointerUp = false },
                    backend = "runtime_ui_toolkit",
                    note = "One pointer move without a click; inspect/wait for hover and scheduled UI effects on later player frames.",
                });
        }

        private static object Key(ToolParams p)
        {
            object playModeError = RequirePlayMode(allowPaused: false, "key_ui");
            if (playModeError != null)
                return playModeError;
            if (HasElementQueryFields(p) || p.GetRaw("position") != null
                || p.GetRaw("target") != null || p.GetRaw("search_method") != null)
            {
                return ErrorResponse.FromCode(
                    "invalid_key_address",
                    "key_ui accepts a document and uses its current focus or root; no target, position, or element query.");
            }
            string keyName = p.Get("key_code");
            if (keyName == null || !KeyCodes.Contains(keyName)
                || !Enum.TryParse(keyName, out KeyCode keyCode))
            {
                return ErrorResponse.FromCode(
                    "invalid_key_code", "key_code must be a named key reported by ping.uiSystems.uiToolkit.keyCodes.");
            }
            EventModifiers modifiers = EventModifiers.None;
            JToken modifierToken = p.GetRaw("modifiers");
            if (modifierToken != null && modifierToken.Type != JTokenType.Null)
            {
                if (!(modifierToken is JArray values) || values.Count > 4)
                    return ErrorResponse.FromCode("invalid_key_modifiers", "modifiers must be an array of at most four distinct modifier names.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JToken value in values)
                {
                    string name = value.Type == JTokenType.String ? value.Value<string>() : null;
                    if (name == null || !seen.Add(name)
                        || (name != "Shift" && name != "Control" && name != "Alt" && name != "Command"))
                        return ErrorResponse.FromCode("invalid_key_modifiers", "modifiers must be distinct Shift, Control, Alt, or Command values.");
                    modifiers |= (EventModifiers)Enum.Parse(typeof(EventModifiers), name);
                }
            }
            DocumentResolution resolution = ResolveDocument(p, includeInactive: false);
            if (!resolution.Success)
                return ErrorResponse.FromCode(resolution.Code, resolution.Error);
            DocumentContext context = resolution.Context;
            if (!IsDocumentMutable(context))
                return ErrorResponse.FromCode("ui_not_interactable", "The runtime UIDocument must be active on a panel.");

            VisualElement focused = context.Panel.focusController?.focusedElement as VisualElement;
            if (focused != null && !ReferenceEquals(focused, context.Root) && !context.Root.Contains(focused))
                return ErrorResponse.FromCode("ui_focus_outside_document", "The panel focus belongs to another UIDocument; address that document instead.");
            VisualElement downTarget = focused ?? context.Root;
            object documentDescription = DescribeDocument(context);
            object downDescription;
            using (KeyDownEvent down = KeyDownEvent.GetPooled('\0', keyCode, modifiers))
            {
                if (focused == null)
                    down.target = context.Root;
                context.Panel.visualTree.SendEvent(down);
                downDescription = DescribeElement(down.target as VisualElement ?? downTarget, context.Root);
            }

            if (!EditorApplication.isPlaying || context.Root.panel != context.Panel)
            {
                return ErrorResponse.FromCode(
                    "ui_target_detached_after_key_down",
                    "KeyDown was dispatched, but the runtime panel detached before KeyUp; inspect the resulting state before retrying.",
                    new { eventsInvoked = new { keyDown = true, keyUp = false } });
            }
            VisualElement upTarget = context.Panel.focusController?.focusedElement as VisualElement
                ?? context.Root;
            if (!ReferenceEquals(upTarget, context.Root) && !context.Root.Contains(upTarget))
            {
                return ErrorResponse.FromCode(
                    "ui_focus_changed_after_key_down",
                    "KeyDown was dispatched, but focus moved to another UIDocument before KeyUp; inspect the resulting state before retrying.",
                    new { eventsInvoked = new { keyDown = true, keyUp = false } });
            }
            object upDescription;
            using (KeyUpEvent up = KeyUpEvent.GetPooled('\0', keyCode, modifiers))
            {
                if (context.Panel.focusController?.focusedElement == null)
                    up.target = context.Root;
                context.Panel.visualTree.SendEvent(up);
                upDescription = DescribeElement(up.target as VisualElement ?? upTarget, context.Root);
            }
            EditorApplication.QueuePlayerLoopUpdate();
            return new SuccessResponse(
                "Runtime UI Toolkit key pair was dispatched through the panel event system.",
                new
                {
                    document = documentDescription,
                    keyCode = keyName,
                    modifiers = modifiers.ToString(),
                    keyDownTarget = downDescription,
                    keyUpTarget = upDescription,
                    eventsInvoked = new { keyDown = true, keyUp = true, navigation = false },
                    backend = "runtime_ui_toolkit",
                    note = "KeyDown/KeyUp only. Tab/Return do not generate navigation or submit; no text, IME, device-state, or operating-system input is synthesized.",
                });
        }

        private static object Drag(ToolParams p)
        {
            object playModeError = RequirePlayMode(
                allowPaused: false,
                "drag_ui");
            if (playModeError != null)
            {
                return playModeError;
            }

            if (!TryResolvePointerStart(
                    p,
                    out PointerAddress address,
                    out object addressError))
            {
                return addressError;
            }
            if (!InteractPlayMode.TryReadNormalizedPosition(
                    p.GetRaw("end_position"),
                    out Vector2 endNormalized,
                    out string endError))
            {
                return ErrorResponse.FromCode("invalid_drag_end", endError);
            }

            int steps = p.GetInt("steps") ?? 5;
            if (steps < 1 || steps > 64)
            {
                return ErrorResponse.FromCode(
                    "invalid_drag_steps",
                    "'steps' must be an integer between 1 and 64.");
            }

            Vector2 endPoint = NormalizedToPanel(
                address.Context.Panel,
                endNormalized);
            SendPointerDown(address.HitElement, address.PanelPoint);

            Vector2 previousPoint = address.PanelPoint;
            VisualElement finalHit = address.HitElement;
            for (int index = 1; index <= steps; index++)
            {
                Vector2 currentPoint = Vector2.Lerp(
                    address.PanelPoint,
                    endPoint,
                    index / (float)steps);
                VisualElement currentHit =
                    address.Context.Panel.Pick(currentPoint)
                    ?? finalHit
                    ?? address.HitElement;
                SendPointerMove(
                    currentHit,
                    currentPoint,
                    currentPoint - previousPoint);
                finalHit = address.Context.Panel.Pick(currentPoint);
                previousPoint = currentPoint;
            }

            VisualElement releaseTarget =
                finalHit ?? address.HitElement;
            SendPointerUp(releaseTarget, endPoint);
            EditorApplication.QueuePlayerLoopUpdate();

            return new SuccessResponse(
                "Runtime UI Toolkit pointer drag was dispatched through the panel event system.",
                new
                {
                    document = DescribeDocument(address.Context),
                    requestedElement = address.RequestedElement == null
                        ? null
                        : DescribeElement(
                            address.RequestedElement,
                            address.Context.Root),
                    startHitElement = DescribeElement(
                        address.HitElement,
                        address.Context.Root),
                    finalHitElement = finalHit == null
                        ? null
                        : DescribeElement(
                            finalHit,
                            address.Context.Root),
                    startPosition = DescribeNormalized(
                        address.NormalizedPosition),
                    endPosition = DescribeNormalized(endNormalized),
                    steps,
                    eventsInvoked = new
                    {
                        pointerDown = true,
                        pointerMove = steps,
                        pointerUp = true,
                    },
                    backend = "runtime_ui_toolkit",
                    note = "This dispatches runtime pointer capture/manipulator events, not Editor drag-and-drop payload events.",
                });
        }

        private static object Scroll(ToolParams p)
        {
            object playModeError = RequirePlayMode(
                allowPaused: false,
                "scroll_ui");
            if (playModeError != null)
            {
                return playModeError;
            }

            if (!TryResolvePointerStart(
                    p,
                    out PointerAddress address,
                    out object addressError))
            {
                return addressError;
            }
            if (!TryReadScrollDelta(
                    p.GetRaw("scroll_delta"),
                    out Vector2 requestedDelta))
            {
                return ErrorResponse.FromCode(
                    "invalid_scroll_delta",
                    "'scroll_delta' must contain two finite values between -100 and 100 and cannot be [0,0].");
            }

            VisualElement scrollSearchRoot =
                address.RequestedElement ?? address.HitElement;
            ScrollView scrollView = FindAssociatedScrollView(
                scrollSearchRoot,
                address.Context.Root);
            if (scrollView == null)
            {
                scrollView = FindAssociatedScrollView(
                    address.HitElement,
                    address.Context.Root);
            }
            if (scrollView == null)
            {
                return ErrorResponse.FromCode(
                    "scroll_handler_required",
                    $"Runtime UI Toolkit element '{DescribeElementLabel(scrollSearchRoot)}' is not associated with a ScrollView.");
            }

            object before = DescribeScrollState(scrollView);
            var systemEvent = new Event
            {
                type = EventType.ScrollWheel,
                mousePosition = address.PanelPoint,
                delta = new Vector2(
                    requestedDelta.x,
                    -requestedDelta.y),
            };
            using (WheelEvent wheelEvent = WheelEvent.GetPooled(systemEvent))
            {
                wheelEvent.target = address.HitElement;
                address.HitElement.SendEvent(wheelEvent);
            }

            EditorApplication.QueuePlayerLoopUpdate();
            object after = DescribeScrollState(scrollView);
            return new SuccessResponse(
                "Runtime UI Toolkit scroll was dispatched through the panel event system.",
                new
                {
                    document = DescribeDocument(address.Context),
                    requestedElement = address.RequestedElement == null
                        ? null
                        : DescribeElement(
                            address.RequestedElement,
                            address.Context.Root),
                    hitElement = DescribeElement(
                        address.HitElement,
                        address.Context.Root),
                    scrollView = DescribeElement(
                        scrollView,
                        address.Context.Root),
                    normalizedPosition = DescribeNormalized(
                        address.NormalizedPosition),
                    scrollDelta = new
                    {
                        x = requestedDelta.x,
                        y = requestedDelta.y,
                        units = "unity_scroll",
                        positiveY = "up",
                    },
                    eventInvoked = true,
                    before,
                    after,
                    normalizedPositionChanged =
                        ScrollStateChanged(before, after),
                    backend = "runtime_ui_toolkit",
                });
        }

        private static bool TryResolveRequiredElement(
            ToolParams p,
            out DocumentContext context,
            out VisualElement element,
            out ElementQuery query,
            out object errorResponse)
        {
            context = null;
            element = null;
            query = null;
            errorResponse = null;

            if (!TryReadElementQuery(
                    p,
                    required: true,
                    out query,
                    out string queryError))
            {
                errorResponse = ErrorResponse.FromCode(
                    "invalid_ui_toolkit_query",
                    queryError);
                return false;
            }

            DocumentResolution documentResolution = ResolveDocument(
                p,
                includeInactive: false);
            if (!documentResolution.Success)
            {
                errorResponse = ErrorResponse.FromCode(
                    documentResolution.Code,
                    documentResolution.Error);
                return false;
            }

            ElementResolution elementResolution = ResolveElement(
                documentResolution.Context,
                query);
            if (!elementResolution.Success)
            {
                errorResponse = ErrorResponse.FromCode(
                    elementResolution.Code,
                    elementResolution.Error);
                return false;
            }

            context = documentResolution.Context;
            element = elementResolution.Element;
            return true;
        }

        private static bool TryResolvePointerStart(
            ToolParams p,
            out PointerAddress address,
            out object errorResponse)
        {
            address = null;
            errorResponse = null;

            DocumentResolution documentResolution = ResolveDocument(
                p,
                includeInactive: false);
            if (!documentResolution.Success)
            {
                errorResponse = ErrorResponse.FromCode(
                    documentResolution.Code,
                    documentResolution.Error);
                return false;
            }

            DocumentContext context = documentResolution.Context;
            if (!IsDocumentMutable(context))
            {
                errorResponse = ErrorResponse.FromCode(
                    "ui_not_interactable",
                    $"Runtime UI Toolkit document '{context.GameObject.name}' is not active on a panel.");
                return false;
            }

            JToken positionToken = p.GetRaw("position");
            bool hasPosition = positionToken != null
                && positionToken.Type != JTokenType.Null;
            bool hasQueryFields = HasElementQueryFields(p);
            if (hasPosition == hasQueryFields)
            {
                errorResponse = ErrorResponse.FromCode(
                    "invalid_ui_address",
                    "Provide exactly one UI Toolkit element query or normalized 'position'.");
                return false;
            }

            VisualElement requestedElement = null;
            Vector2 normalizedPosition;
            Vector2 panelPoint;
            VisualElement hitElement;
            if (hasPosition)
            {
                if (!InteractPlayMode.TryReadNormalizedPosition(
                        positionToken,
                        out normalizedPosition,
                        out string positionError))
                {
                    errorResponse = ErrorResponse.FromCode(
                        "invalid_ui_position",
                        positionError);
                    return false;
                }
                if (context.Document.panelSettings != null
                    && context.Document.panelSettings.targetTexture != null)
                {
                    errorResponse = ErrorResponse.FromCode(
                        "ui_toolkit_screen_space_required",
                        "Coordinate UI Toolkit interaction currently requires a screen-space PanelSettings without a target texture.");
                    return false;
                }

                panelPoint = NormalizedToPanel(
                    context.Panel,
                    normalizedPosition);
                hitElement = context.Panel.Pick(panelPoint);
                if (hitElement == null)
                {
                    errorResponse = ErrorResponse.FromCode(
                        "ui_raycast_miss",
                        "No runtime UI Toolkit element was picked at the requested position.");
                    return false;
                }
            }
            else
            {
                if (!TryReadElementQuery(
                        p,
                        required: true,
                        out ElementQuery query,
                        out string queryError))
                {
                    errorResponse = ErrorResponse.FromCode(
                        "invalid_ui_toolkit_query",
                        queryError);
                    return false;
                }

                ElementResolution elementResolution = ResolveElement(
                    context,
                    query);
                if (!elementResolution.Success)
                {
                    errorResponse = ErrorResponse.FromCode(
                        elementResolution.Code,
                        elementResolution.Error);
                    return false;
                }
                requestedElement = elementResolution.Element;
                if (!requestedElement.enabledInHierarchy)
                {
                    errorResponse = ErrorResponse.FromCode(
                        "ui_not_interactable",
                        $"Runtime UI Toolkit element '{DescribeElementLabel(requestedElement)}' is disabled.");
                    return false;
                }
                if (!TryFindReachablePoint(
                        context,
                        requestedElement,
                        out panelPoint,
                        out hitElement))
                {
                    errorResponse = ErrorResponse.FromCode(
                        "ui_element_not_reachable",
                        $"Runtime UI Toolkit element '{DescribeElementLabel(requestedElement)}' is hidden, clipped, outside the panel, or covered by another element.");
                    return false;
                }
                normalizedPosition = PanelToNormalized(
                    context.Panel,
                    panelPoint);
            }

            address = new PointerAddress(
                context,
                requestedElement,
                hitElement,
                normalizedPosition,
                panelPoint);
            return true;
        }

        private static DocumentResolution ResolveDocument(
            ToolParams p,
            bool includeInactive)
        {
            JToken documentToken = p.GetRaw("document");
            string requestedDocument = documentToken?.ToString();
            if (string.IsNullOrWhiteSpace(requestedDocument))
            {
                return DocumentResolution.Fail(
                    "document_required",
                    "'document' is required for UI Toolkit actions.");
            }

            if (!InteractPlayMode.TryResolveTarget(
                    documentToken,
                    p.Get("document_search_method"),
                    includeInactive,
                    out GameObject gameObject,
                    out string resolutionError))
            {
                bool missing = !string.IsNullOrEmpty(resolutionError)
                    && (resolutionError.Contains("was not found")
                        || resolutionError.Contains("no longer exists"));
                if (!missing)
                {
                    return DocumentResolution.Fail(
                        "ui_document_resolution_failed",
                        resolutionError
                            ?? "Runtime UI Toolkit document could not be resolved.");
                }

                List<GameObject> runtimeMatches =
                    FindRuntimeDocumentGameObjects(
                        requestedDocument,
                        p.Get("document_search_method"),
                        includeInactive,
                        maxResults: 2);
                if (runtimeMatches.Count == 0)
                {
                    return DocumentResolution.NotFound();
                }
                if (runtimeMatches.Count > 1)
                {
                    return DocumentResolution.Fail(
                        "ui_document_resolution_failed",
                        $"UI document '{requestedDocument}' is ambiguous; use a hierarchy path or instance ID.");
                }

                gameObject = runtimeMatches[0];
            }

            UIDocument document = gameObject.GetComponent<UIDocument>();
            if (document == null)
            {
                return DocumentResolution.Fail(
                    "ui_document_required",
                    $"GameObject '{gameObject.name}' has no UIDocument component.");
            }

            VisualElement root = document.rootVisualElement;
            if (root == null)
            {
                return DocumentResolution.Fail(
                    "ui_document_tree_unavailable",
                    $"UIDocument on '{gameObject.name}' has no runtime visual tree.");
            }

            IPanel panel = document.runtimePanel ?? root.panel;
            return DocumentResolution.Ok(
                new DocumentContext(
                    gameObject,
                    document,
                    root,
                    panel));
        }

        private static List<GameObject> FindRuntimeDocumentGameObjects(
            string searchTerm,
            string requestedSearchMethod,
            bool includeInactive,
            int maxResults)
        {
            string searchMethod = requestedSearchMethod?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(searchMethod))
            {
                searchMethod = int.TryParse(searchTerm, out _)
                    ? "by_id"
                    : searchTerm.Contains("/")
                        ? "by_path"
                        : "by_name";
            }

            IEnumerable<GameObject> candidates = UnityEngine.Resources
                .FindObjectsOfTypeAll<UIDocument>()
                .Where(document => document != null)
                .Select(document => document.gameObject)
                .Where(gameObject => gameObject != null
                    && gameObject.scene.IsValid()
                    && gameObject.scene.isLoaded
                    && (includeInactive || gameObject.activeInHierarchy))
                .Distinct();

            switch (searchMethod)
            {
                case "by_id":
                    if (!int.TryParse(searchTerm, out int instanceId))
                    {
                        return new List<GameObject>();
                    }
                    candidates = candidates.Where(gameObject =>
                        gameObject.GetInstanceID() == instanceId);
                    break;
                case "by_path":
                    candidates = candidates.Where(gameObject =>
                        GameObjectLookup.MatchesPath(gameObject, searchTerm));
                    break;
                case "by_name":
                    candidates = candidates.Where(gameObject =>
                        gameObject.name == searchTerm);
                    break;
                default:
                    return new List<GameObject>();
            }

            if (maxResults > 0)
            {
                candidates = candidates.Take(maxResults);
            }
            return candidates.ToList();
        }

        internal static ElementResolution ResolveElement(
            DocumentContext context,
            ElementQuery query)
        {
            var matches = new List<VisualElement>();
            int scanned = 0;
            bool truncated = false;
            var stack = new Stack<VisualElement>();
            stack.Push(context.Root);

            while (stack.Count > 0)
            {
                VisualElement current = stack.Pop();
                scanned++;
                if (scanned > MaxScannedElements)
                {
                    truncated = true;
                    break;
                }

                if (query.Matches(current))
                {
                    matches.Add(current);
                    if (!query.Index.HasValue && matches.Count > 1)
                    {
                        break;
                    }
                    if (query.Index.HasValue
                        && matches.Count > query.Index.Value)
                    {
                        break;
                    }
                }

                for (int index = current.hierarchy.childCount - 1; index >= 0; index--)
                {
                    stack.Push(current.hierarchy[index]);
                }
            }

            if (truncated)
            {
                return ElementResolution.Fail(
                    "ui_toolkit_tree_too_large",
                    $"UI Toolkit visual-tree scan exceeded the bounded limit of {MaxScannedElements} elements.");
            }
            if (matches.Count == 0
                || query.Index.HasValue
                    && matches.Count <= query.Index.Value)
            {
                return ElementResolution.NotFound(
                    $"UI Toolkit element query '{query}' did not match.");
            }
            if (!query.Index.HasValue && matches.Count > 1)
            {
                return ElementResolution.Fail(
                    "ui_toolkit_element_ambiguous",
                    $"UI Toolkit element query '{query}' is ambiguous; add element_index or a more specific name, class, or type.");
            }

            return ElementResolution.Ok(
                matches[query.Index ?? 0]);
        }

        internal static bool TryReadElementQuery(
            ToolParams p,
            bool required,
            out ElementQuery query,
            out string error)
        {
            string name = NormalizeOptional(p.Get("element_name"));
            string className = NormalizeOptional(p.Get("element_class"));
            string typeName = NormalizeOptional(p.Get("element_type"));
            int? index = null;
            JToken indexToken = p.GetRaw("element_index");
            if (indexToken != null && indexToken.Type != JTokenType.Null)
            {
                if (indexToken.Type != JTokenType.Integer)
                {
                    query = null;
                    error = "'element_index' must be an integer.";
                    return false;
                }
                int parsedIndex = indexToken.Value<int>();
                if (parsedIndex < 0 || parsedIndex > MaxElementIndex)
                {
                    query = null;
                    error = $"'element_index' must be between 0 and {MaxElementIndex}.";
                    return false;
                }
                index = parsedIndex;
            }

            bool hasSelectors = name != null
                || className != null
                || typeName != null;
            if (!hasSelectors)
            {
                query = null;
                error = index.HasValue
                    ? "'element_index' requires element_name, element_class, or element_type."
                    : required
                        ? "Provide element_name, element_class, or element_type."
                        : null;
                return !required && !index.HasValue;
            }

            query = new ElementQuery(
                name,
                className,
                typeName,
                index);
            error = null;
            return true;
        }

        private static bool HasElementQueryFields(ToolParams p)
        {
            return !string.IsNullOrWhiteSpace(p.Get("element_name"))
                || !string.IsNullOrWhiteSpace(p.Get("element_class"))
                || !string.IsNullOrWhiteSpace(p.Get("element_type"))
                || p.GetRaw("element_index") != null
                    && p.GetRaw("element_index").Type != JTokenType.Null;
        }

        private static bool TryFindReachablePoint(
            DocumentContext context,
            VisualElement requestedElement,
            out Vector2 point,
            out VisualElement hit)
        {
            point = default;
            hit = null;
            if (!IsElementStyleVisible(requestedElement, context.Root))
            {
                return false;
            }

            Rect bounds = requestedElement.worldBound;
            Rect panelBounds = context.Panel.visualTree.worldBound;
            if (!IsFinitePositiveRect(bounds)
                || !bounds.Overlaps(panelBounds))
            {
                return false;
            }

            Rect visibleBounds = Intersect(bounds, panelBounds);
            float insetX = Mathf.Min(
                visibleBounds.width * 0.2f,
                2f);
            float insetY = Mathf.Min(
                visibleBounds.height * 0.2f,
                2f);
            Vector2[] candidates =
            {
                visibleBounds.center,
                new Vector2(
                    visibleBounds.xMin + insetX,
                    visibleBounds.yMin + insetY),
                new Vector2(
                    visibleBounds.xMax - insetX,
                    visibleBounds.yMin + insetY),
                new Vector2(
                    visibleBounds.xMin + insetX,
                    visibleBounds.yMax - insetY),
                new Vector2(
                    visibleBounds.xMax - insetX,
                    visibleBounds.yMax - insetY),
            };

            foreach (Vector2 candidate in candidates)
            {
                VisualElement candidateHit = context.Panel.Pick(candidate);
                if (candidateHit != null
                    && (ReferenceEquals(candidateHit, requestedElement)
                        || requestedElement.Contains(candidateHit)))
                {
                    point = candidate;
                    hit = candidateHit;
                    return true;
                }
            }

            return false;
        }

        private static object BuildInspection(
            DocumentContext context,
            VisualElement element,
            ElementQuery query,
            bool includeText)
        {
            bool documentActive = context.GameObject.activeInHierarchy
                && context.Document.enabled;
            bool panelAttached = context.Panel != null
                && ReferenceEquals(element.panel, context.Panel);
            bool styleVisible = IsElementStyleVisible(
                element,
                context.Root);
            bool hasBounds = TryDescribeNormalizedBounds(
                context,
                element,
                out object normalizedBounds,
                out bool overlapsPanel);
            bool hitTestVisible = panelAttached
                && TryFindReachablePoint(
                    context,
                    element,
                    out _,
                    out _);
            bool visible = documentActive
                && panelAttached
                && styleVisible
                && hasBounds
                && overlapsPanel;

            bool? hovered = panelAttached ? ReadHoverState(element) : false;
            bool selectionSupported = panelAttached
                && context.Panel.focusController != null;
            Focusable focusedElement = selectionSupported
                ? context.Panel.focusController.focusedElement
                : null;
            bool selected = focusedElement is VisualElement focusedVisual
                && AreInSameVisualBranch(element, focusedVisual);

            TextField textField = FindNearestAncestor<TextField>(
                element,
                context.Root);
            Toggle toggle = FindNearestAncestor<Toggle>(
                element,
                context.Root);
            string text = null;
            bool textExists = false;
            bool sensitiveText = false;
            if (textField != null)
            {
                text = textField.value ?? string.Empty;
                textExists = true;
                sensitiveText = textField.isPasswordField;
            }
            else if (element is TextElement textElement)
            {
                text = textElement.text ?? string.Empty;
                textExists = true;
            }
            else if (toggle != null)
            {
                text = toggle.text ?? string.Empty;
                textExists = true;
            }

            bool textAvailable = includeText
                && textExists
                && !sensitiveText;
            bool? interactable = DescribeInteractable(
                context,
                element,
                textField,
                toggle);
            ScrollView scrollView = FindAssociatedScrollView(
                element,
                context.Root);

            return new
            {
                exists = true,
                documentExists = true,
                document = DescribeDocument(context),
                query = query.Describe(),
                element = DescribeElement(element, context.Root),
                activeSelf = context.GameObject.activeSelf
                    && context.Document.enabled,
                activeInHierarchy = documentActive && panelAttached,
                panelAttached,
                visible,
                hitTestVisible,
                interactable,
                selected,
                selectionSupported,
                hovered,
                hoverSupported = hovered.HasValue,
                componentKinds = DescribeComponentKinds(
                    element,
                    textField,
                    toggle,
                    scrollView),
                inputField = textField == null
                    ? null
                    : DescribeElement(textField, context.Root),
                toggle = toggle == null
                    ? null
                    : DescribeElement(toggle, context.Root),
                scrollView = scrollView == null
                    ? null
                    : DescribeElement(scrollView, context.Root),
                scrollState = scrollView == null
                    ? null
                    : DescribeScrollState(scrollView),
                normalizedBounds = hasBounds
                    ? normalizedBounds
                    : null,
                textAvailable,
                textRedacted = textExists && sensitiveText,
                text = textAvailable ? text : null,
                textLength = textAvailable
                    ? (int?)text.Length
                    : null,
                toggleValue = toggle == null
                    ? (bool?)null
                    : toggle.value,
                backend = "runtime_ui_toolkit",
            };
        }

        private static bool? ReadHoverState(VisualElement element)
        {
            if (PseudoStatesProperty == null || !PseudoStatesProperty.PropertyType.IsEnum
                || !Enum.TryParse(PseudoStatesProperty.PropertyType, "Hover", out object hoverFlag))
                return null;
            ulong states = Convert.ToUInt64(PseudoStatesProperty.GetValue(element));
            return (states & Convert.ToUInt64(hoverFlag)) != 0;
        }

        private static bool? DescribeInteractable(
            DocumentContext context,
            VisualElement element,
            TextField textField,
            Toggle toggle)
        {
            if (textField != null)
            {
                return IsDocumentMutable(context)
                    && textField.enabledInHierarchy
                    && !textField.isReadOnly;
            }
            if (toggle != null)
            {
                return IsDocumentMutable(context)
                    && toggle.enabledInHierarchy;
            }
            if (element is Button || element.focusable)
            {
                return IsDocumentMutable(context)
                    && element.enabledInHierarchy;
            }
            return null;
        }

        private static List<string> DescribeComponentKinds(
            VisualElement element,
            TextField textField,
            Toggle toggle,
            ScrollView scrollView)
        {
            var kinds = new List<string>
            {
                element.GetType().FullName,
            };
            AddDistinctKind(kinds, textField);
            AddDistinctKind(kinds, toggle);
            AddDistinctKind(kinds, scrollView);
            return kinds;
        }

        private static void AddDistinctKind(
            List<string> kinds,
            VisualElement element)
        {
            string kind = element?.GetType().FullName;
            if (!string.IsNullOrEmpty(kind)
                && !kinds.Contains(kind))
            {
                kinds.Add(kind);
            }
        }

        private static bool IsDocumentMutable(DocumentContext context)
        {
            return context != null
                && context.GameObject != null
                && context.GameObject.activeInHierarchy
                && context.Document != null
                && context.Document.enabled
                && context.Panel != null
                && context.Root != null
                && ReferenceEquals(context.Root.panel, context.Panel);
        }

        private static bool IsElementStyleVisible(
            VisualElement element,
            VisualElement root)
        {
            VisualElement current = element;
            while (current != null)
            {
                IResolvedStyle style = current.resolvedStyle;
                if (!current.visible
                    || style.display == DisplayStyle.None
                    || style.visibility == Visibility.Hidden
                    || !float.IsFinite(style.opacity)
                    || style.opacity <= VisibilityEpsilon)
                {
                    return false;
                }
                if (ReferenceEquals(current, root))
                {
                    return true;
                }
                current = current.parent;
            }
            return false;
        }

        private static bool TryDescribeNormalizedBounds(
            DocumentContext context,
            VisualElement element,
            out object normalizedBounds,
            out bool overlapsPanel)
        {
            normalizedBounds = null;
            overlapsPanel = false;
            if (context.Panel == null)
            {
                return false;
            }

            Rect panelBounds = context.Panel.visualTree.worldBound;
            Rect elementBounds = element.worldBound;
            if (!IsFinitePositiveRect(panelBounds)
                || !IsFinitePositiveRect(elementBounds))
            {
                return false;
            }

            overlapsPanel = elementBounds.Overlaps(panelBounds);
            float x = (elementBounds.xMin - panelBounds.xMin)
                / panelBounds.width;
            float y = (elementBounds.yMin - panelBounds.yMin)
                / panelBounds.height;
            float width = elementBounds.width / panelBounds.width;
            float height = elementBounds.height / panelBounds.height;
            normalizedBounds = new
            {
                x,
                y,
                width,
                height,
                xMin = x,
                yMin = y,
                xMax = x + width,
                yMax = y + height,
                origin = "top_left",
            };
            return true;
        }

        private static ScrollView FindAssociatedScrollView(
            VisualElement element,
            VisualElement root)
        {
            ScrollView ancestor = FindNearestAncestor<ScrollView>(
                element,
                root);
            if (ancestor != null)
            {
                return ancestor;
            }

            return FindDescendants<ScrollView>(
                    element,
                    maxResults: 1)
                .FirstOrDefault();
        }

        private static object DescribeScrollState(ScrollView scrollView)
        {
            Vector2 offset = scrollView.scrollOffset;
            Rect contentBounds = scrollView.contentContainer.worldBound;
            Rect viewportBounds = scrollView.contentViewport.worldBound;
            float maxX = Mathf.Max(
                0f,
                contentBounds.width - viewportBounds.width);
            float maxY = Mathf.Max(
                0f,
                contentBounds.height - viewportBounds.height);
            return new
            {
                offset = DescribeVector(offset),
                maxOffset = new
                {
                    x = maxX,
                    y = maxY,
                },
                normalizedPosition = new
                {
                    x = maxX <= VisibilityEpsilon
                        ? 0f
                        : Mathf.Clamp01(offset.x / maxX),
                    y = maxY <= VisibilityEpsilon
                        ? 0f
                        : Mathf.Clamp01(offset.y / maxY),
                    origin = "top_left",
                },
                viewportSize = new
                {
                    width = viewportBounds.width,
                    height = viewportBounds.height,
                },
                contentSize = new
                {
                    width = contentBounds.width,
                    height = contentBounds.height,
                },
            };
        }

        private static bool ScrollStateChanged(
            object before,
            object after)
        {
            JObject beforeJson = JObject.FromObject(before);
            JObject afterJson = JObject.FromObject(after);
            JToken beforeOffset = beforeJson["offset"];
            JToken afterOffset = afterJson["offset"];
            return !JToken.DeepEquals(beforeOffset, afterOffset);
        }

        private static bool TryFindAssociatedElement<T>(
            VisualElement requested,
            VisualElement root,
            out T element,
            out bool ambiguous)
            where T : VisualElement
        {
            element = FindNearestAncestor<T>(requested, root);
            ambiguous = false;
            if (element != null)
            {
                return true;
            }

            List<T> descendants = FindDescendants<T>(
                requested,
                maxResults: 2);
            ambiguous = descendants.Count > 1;
            if (descendants.Count == 1)
            {
                element = descendants[0];
                return true;
            }
            return false;
        }

        private static T FindNearestAncestor<T>(
            VisualElement element,
            VisualElement root)
            where T : VisualElement
        {
            VisualElement current = element;
            while (current != null)
            {
                if (current is T matched)
                {
                    return matched;
                }
                if (ReferenceEquals(current, root))
                {
                    break;
                }
                current = current.parent;
            }
            return null;
        }

        private static List<T> FindDescendants<T>(
            VisualElement root,
            int maxResults)
            where T : VisualElement
        {
            var matches = new List<T>();
            var stack = new Stack<VisualElement>();
            for (int index = root.hierarchy.childCount - 1; index >= 0; index--)
            {
                stack.Push(root.hierarchy[index]);
            }

            int scanned = 0;
            while (stack.Count > 0
                && matches.Count < maxResults
                && scanned < MaxScannedElements)
            {
                VisualElement current = stack.Pop();
                scanned++;
                if (current is T matched)
                {
                    matches.Add(matched);
                }
                for (int index = current.hierarchy.childCount - 1; index >= 0; index--)
                {
                    stack.Push(current.hierarchy[index]);
                }
            }
            return matches;
        }

        private static bool AreInSameVisualBranch(
            VisualElement first,
            VisualElement second)
        {
            return ReferenceEquals(first, second)
                || first.Contains(second)
                || second.Contains(first);
        }

        private static bool TryReadRequiredBool(
            JToken token,
            out bool value)
        {
            value = false;
            if (token == null
                || token.Type == JTokenType.Null
                || token.Type != JTokenType.Boolean)
            {
                return false;
            }
            value = token.Value<bool>();
            return true;
        }

        private static bool TryReadScrollDelta(
            JToken token,
            out Vector2 delta)
        {
            delta = default;
            if (!(token is JArray values) || values.Count != 2)
            {
                return false;
            }
            if (!TryReadBoundedNumber(values[0], -100f, 100f, out float x)
                || !TryReadBoundedNumber(values[1], -100f, 100f, out float y)
                || x == 0f && y == 0f)
            {
                return false;
            }
            delta = new Vector2(x, y);
            return true;
        }

        private static bool TryReadBoundedNumber(
            JToken token,
            float minimum,
            float maximum,
            out float value)
        {
            value = 0f;
            if (token == null
                || token.Type != JTokenType.Integer
                    && token.Type != JTokenType.Float)
            {
                return false;
            }
            double number = token.Value<double>();
            if (!double.IsFinite(number)
                || number < minimum
                || number > maximum)
            {
                return false;
            }
            value = (float)number;
            return true;
        }

        private static void SendPointerDown(
            VisualElement target,
            Vector2 panelPoint)
        {
            var systemEvent = new Event
            {
                type = EventType.MouseDown,
                mousePosition = panelPoint,
                button = 0,
            };
            using (PointerDownEvent pointerEvent =
                PointerDownEvent.GetPooled(systemEvent))
            {
                pointerEvent.target = target;
                target.SendEvent(pointerEvent);
            }
        }

        private static void SendPointerMove(
            VisualElement target,
            Vector2 panelPoint,
            Vector2 delta)
        {
            var systemEvent = new Event
            {
                type = EventType.MouseDrag,
                mousePosition = panelPoint,
                delta = delta,
                button = 0,
            };
            using (PointerMoveEvent pointerEvent =
                PointerMoveEvent.GetPooled(systemEvent))
            {
                pointerEvent.target = target;
                target.SendEvent(pointerEvent);
            }
        }

        private static void SendPointerUp(
            VisualElement target,
            Vector2 panelPoint)
        {
            var systemEvent = new Event
            {
                type = EventType.MouseUp,
                mousePosition = panelPoint,
                button = 0,
            };
            using (PointerUpEvent pointerEvent =
                PointerUpEvent.GetPooled(systemEvent))
            {
                pointerEvent.target = target;
                target.SendEvent(pointerEvent);
            }
        }

        private static Vector2 NormalizedToPanel(
            IPanel panel,
            Vector2 normalized)
        {
            Rect bounds = panel.visualTree.worldBound;
            return new Vector2(
                Mathf.Lerp(bounds.xMin, bounds.xMax, normalized.x),
                Mathf.Lerp(bounds.yMin, bounds.yMax, normalized.y));
        }

        private static Vector2 PanelToNormalized(
            IPanel panel,
            Vector2 point)
        {
            Rect bounds = panel.visualTree.worldBound;
            return new Vector2(
                bounds.width <= VisibilityEpsilon
                    ? 0f
                    : Mathf.Clamp01(
                        (point.x - bounds.xMin) / bounds.width),
                bounds.height <= VisibilityEpsilon
                    ? 0f
                    : Mathf.Clamp01(
                        (point.y - bounds.yMin) / bounds.height));
        }

        private static object DescribeNormalized(Vector2 value)
        {
            return new
            {
                x = value.x,
                y = value.y,
                origin = "top_left",
            };
        }

        private static object DescribeVector(Vector2 value)
        {
            return new
            {
                x = value.x,
                y = value.y,
            };
        }

        private static object DescribeDocument(DocumentContext context)
        {
            return new
            {
                name = context.GameObject.name,
                path = GameObjectLookup.GetGameObjectPath(
                    context.GameObject),
                instanceId = context.GameObject.GetInstanceIDCompat(),
                enabled = context.Document.enabled,
                activeInHierarchy =
                    context.GameObject.activeInHierarchy,
                sortingOrder = context.Document.sortingOrder,
                panelAttached = context.Panel != null,
                panelSettings = context.Document.panelSettings == null
                    ? null
                    : context.Document.panelSettings.name,
            };
        }

        private static object DescribeElement(
            VisualElement element,
            VisualElement root)
        {
            return new
            {
                name = element.name ?? string.Empty,
                type = element.GetType().FullName,
                path = BuildElementPath(element, root),
                classes = element.GetClasses().Take(32).ToArray(),
                enabledInHierarchy = element.enabledInHierarchy,
                pickingMode = element.pickingMode.ToString(),
                viewDataKey = element.viewDataKey
                    ?? string.Empty,
            };
        }

        private static string DescribeElementLabel(
            VisualElement element)
        {
            if (element == null)
            {
                return "<null>";
            }
            return string.IsNullOrEmpty(element.name)
                ? element.GetType().Name
                : element.name;
        }

        private static string BuildElementPath(
            VisualElement element,
            VisualElement root)
        {
            var segments = new Stack<string>();
            VisualElement current = element;
            while (current != null)
            {
                int siblingIndex = current.parent == null
                    ? 0
                    : current.parent.hierarchy.IndexOf(current);
                string name = string.IsNullOrEmpty(current.name)
                    ? string.Empty
                    : $"#{current.name}";
                segments.Push(
                    $"{current.GetType().Name}{name}[{siblingIndex}]");
                if (ReferenceEquals(current, root))
                {
                    break;
                }
                current = current.parent;
            }
            return string.Join("/", segments);
        }

        private static bool IsFinitePositiveRect(Rect rect)
        {
            return float.IsFinite(rect.x)
                && float.IsFinite(rect.y)
                && float.IsFinite(rect.width)
                && float.IsFinite(rect.height)
                && rect.width > VisibilityEpsilon
                && rect.height > VisibilityEpsilon;
        }

        private static Rect Intersect(Rect first, Rect second)
        {
            float xMin = Mathf.Max(first.xMin, second.xMin);
            float yMin = Mathf.Max(first.yMin, second.yMin);
            float xMax = Mathf.Min(first.xMax, second.xMax);
            float yMax = Mathf.Min(first.yMax, second.yMax);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static string NormalizeOptional(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }

        private static object RequirePlayMode(
            bool allowPaused,
            string action)
        {
            if (!EditorApplication.isPlaying
                || !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return ErrorResponse.FromCode(
                    "play_mode_required",
                    $"{action} requires the Unity Editor to be fully in Play Mode.");
            }
            if (!allowPaused && EditorApplication.isPaused)
            {
                return ErrorResponse.FromCode(
                    "play_mode_paused",
                    $"{action} is unavailable while Play Mode is paused.");
            }
            return null;
        }

        internal sealed class ElementQuery
        {
            internal ElementQuery(
                string name,
                string className,
                string typeName,
                int? index)
            {
                Name = name;
                ClassName = className;
                TypeName = typeName;
                Index = index;
            }

            internal string Name { get; }
            internal string ClassName { get; }
            internal string TypeName { get; }
            internal int? Index { get; }

            internal bool Matches(VisualElement element)
            {
                if (Name != null
                    && !string.Equals(
                        element.name,
                        Name,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                if (ClassName != null
                    && !element.ClassListContains(ClassName))
                {
                    return false;
                }
                if (TypeName != null
                    && !string.Equals(
                        element.GetType().Name,
                        TypeName,
                        StringComparison.Ordinal)
                    && !string.Equals(
                        element.GetType().FullName,
                        TypeName,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                return true;
            }

            internal object Describe()
            {
                return new
                {
                    name = Name,
                    className = ClassName,
                    type = TypeName,
                    index = Index,
                };
            }

            public override string ToString()
            {
                var parts = new List<string>();
                if (Name != null) parts.Add($"name='{Name}'");
                if (ClassName != null) parts.Add($"class='{ClassName}'");
                if (TypeName != null) parts.Add($"type='{TypeName}'");
                if (Index.HasValue) parts.Add($"index={Index.Value}");
                return string.Join(", ", parts);
            }
        }

        internal sealed class DocumentContext
        {
            internal DocumentContext(
                GameObject gameObject,
                UIDocument document,
                VisualElement root,
                IPanel panel)
            {
                GameObject = gameObject;
                Document = document;
                Root = root;
                Panel = panel;
            }

            internal GameObject GameObject { get; }
            internal UIDocument Document { get; }
            internal VisualElement Root { get; }
            internal IPanel Panel { get; }
        }

        internal sealed class ElementResolution
        {
            private ElementResolution(
                bool success,
                bool missing,
                VisualElement element,
                string code,
                string error)
            {
                Success = success;
                Missing = missing;
                Element = element;
                Code = code;
                Error = error;
            }

            internal bool Success { get; }
            internal bool Missing { get; }
            internal VisualElement Element { get; }
            internal string Code { get; }
            internal string Error { get; }

            internal static ElementResolution Ok(
                VisualElement element)
            {
                return new ElementResolution(
                    true,
                    false,
                    element,
                    null,
                    null);
            }

            internal static ElementResolution NotFound(
                string error)
            {
                return new ElementResolution(
                    false,
                    true,
                    null,
                    "ui_toolkit_element_not_found",
                    error);
            }

            internal static ElementResolution Fail(
                string code,
                string error)
            {
                return new ElementResolution(
                    false,
                    false,
                    null,
                    code,
                    error);
            }
        }

        private sealed class DocumentResolution
        {
            private DocumentResolution(
                bool success,
                bool missing,
                DocumentContext context,
                string code,
                string error)
            {
                Success = success;
                Missing = missing;
                Context = context;
                Code = code;
                Error = error;
            }

            internal bool Success { get; }
            internal bool Missing { get; }
            internal DocumentContext Context { get; }
            internal string Code { get; }
            internal string Error { get; }

            internal static DocumentResolution Ok(
                DocumentContext context)
            {
                return new DocumentResolution(
                    true,
                    false,
                    context,
                    null,
                    null);
            }

            internal static DocumentResolution NotFound()
            {
                return new DocumentResolution(
                    false,
                    true,
                    null,
                    "ui_document_not_found",
                    "Runtime UI Toolkit document was not found.");
            }

            internal static DocumentResolution Fail(
                string code,
                string error)
            {
                return new DocumentResolution(
                    false,
                    false,
                    null,
                    code,
                    error);
            }
        }

        private sealed class PointerAddress
        {
            internal PointerAddress(
                DocumentContext context,
                VisualElement requestedElement,
                VisualElement hitElement,
                Vector2 normalizedPosition,
                Vector2 panelPoint)
            {
                Context = context;
                RequestedElement = requestedElement;
                HitElement = hitElement;
                NormalizedPosition = normalizedPosition;
                PanelPoint = panelPoint;
            }

            internal DocumentContext Context { get; }
            internal IPanel Panel => Context.Panel;
            internal VisualElement RequestedElement { get; }
            internal VisualElement HitElement { get; }
            internal Vector2 NormalizedPosition { get; }
            internal Vector2 PanelPoint { get; }
        }
    }
}
