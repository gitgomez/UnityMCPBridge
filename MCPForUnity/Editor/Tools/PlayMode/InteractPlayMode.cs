using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools.PlayMode
{
    [McpForUnityTool("interact_play_mode", AutoRegister = false, Group = "core")]
    public static class InteractPlayMode
    {
        private static readonly string[] ValidActions =
        {
            "ping",
            "click_ui",
            "inspect_ui",
            "wait_ui",
            "set_text",
            "set_toggle",
            "drag_ui",
            "scroll_ui",
            "hover_ui",
            "key_ui",
        };

        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            var p = new ToolParams(@params);
            string action = p.Get("action")?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
            {
                return new ErrorResponse("'action' parameter is required.");
            }

            try
            {
                string uiSystem = p.Get("ui_system")?.ToLowerInvariant()
                    ?? "ugui";
                if (uiSystem != "ugui" && uiSystem != "ui_toolkit")
                {
                    return ErrorResponse.FromCode(
                        "invalid_ui_system",
                        "'ui_system' must be 'ugui' or 'ui_toolkit'.");
                }
                if ((action == "hover_ui" || action == "key_ui")
                    && uiSystem != "ui_toolkit")
                {
                    return ErrorResponse.FromCode(
                        "ui_toolkit_required",
                        $"{action} requires ui_system='ui_toolkit'.");
                }
                if (uiSystem == "ui_toolkit"
                    && action != "ping"
                    && action != "wait_ui")
                {
                    return PlayModeUiToolkitActions.Handle(action, p);
                }

                switch (action)
                {
                    case "ping":
                        return Ping();
                    case "click_ui":
                        return ClickUi(p);
                    case "inspect_ui":
                        return PlayModeUiActions.Inspect(p);
                    case "wait_ui":
                        return ErrorResponse.FromCode(
                            "asynchronous_dispatch_required",
                            "wait_ui requires the asynchronous Unity command dispatcher.");
                    case "set_text":
                        return PlayModeUiActions.SetText(p);
                    case "set_toggle":
                        return PlayModeUiActions.SetToggle(p);
                    case "drag_ui":
                        return PlayModeGestureActions.Drag(p);
                    case "scroll_ui":
                        return PlayModeGestureActions.Scroll(p);
                    default:
                        return new ErrorResponse(
                            $"Unknown action: '{action}'. Valid actions: {string.Join(", ", ValidActions)}.");
                }
            }
            catch (Exception ex)
            {
                Exception reported = ex is TargetInvocationException invocation
                    && invocation.InnerException != null
                        ? invocation.InnerException
                        : ex;
                McpLog.Error($"[InteractPlayMode] Action '{action}' failed: {ex}");
                return new ErrorResponse(
                    $"Play Mode interaction '{action}' failed: {reported.Message}");
            }
        }

        public static async Task<object> HandleCommandAsync(JObject @params)
        {
            string action = @params?["action"]?.ToString()?.ToLowerInvariant();
            if (action != "wait_ui")
            {
                return HandleCommand(@params);
            }

            try
            {
                return await PlayModeUiWaitActions.WaitAsync(@params);
            }
            catch (Exception ex)
            {
                Exception reported = ex is TargetInvocationException invocation
                    && invocation.InnerException != null
                        ? invocation.InnerException
                        : ex;
                McpLog.Error($"[InteractPlayMode] Action 'wait_ui' failed: {ex}");
                return new ErrorResponse(
                    $"Play Mode interaction 'wait_ui' failed: {reported.Message}");
            }
        }

        private static object Ping()
        {
            bool eventSystemAvailable = TryResolveEventSystemTypes(
                out Type eventSystemType,
                out _,
                out _,
                out _,
                out string supportError);
            int eventSystemCount = eventSystemType == null
                ? 0
                : UnityEngine.Resources.FindObjectsOfTypeAll(eventSystemType)
                    .OfType<Component>()
                    .Count(component => component.gameObject.scene.IsValid());

            return new SuccessResponse(
                eventSystemAvailable
                    ? "Unity Play Mode UI interaction is available."
                    : "Unity's runtime EventSystem UI interaction is unavailable.",
                new
                {
                    supported = eventSystemAvailable,
                    supportError,
                    isPlaying = EditorApplication.isPlaying,
                    isPaused = EditorApplication.isPaused,
                    eventSystemCount,
                    backend = "runtime_event_system",
                    uiSystems = new
                    {
                        ugui = new
                        {
                            supported = eventSystemAvailable,
                            supportError,
                            backend = "runtime_event_system",
                        },
                        uiToolkit =
                            PlayModeUiToolkitActions.DescribeSupport(),
                    },
                    coordinateSpace = "normalized",
                    coordinateOrigin = "top_left",
                    actions = ValidActions,
                });
        }

        private static object ClickUi(ToolParams p)
        {
            if (!EditorApplication.isPlaying
                || !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return new ErrorResponse(
                    "click_ui requires the Unity Editor to be fully in Play Mode.");
            }

            if (EditorApplication.isPaused)
            {
                return new ErrorResponse(
                    "click_ui is unavailable while Play Mode is paused.");
            }

            JToken targetToken = p.GetRaw("target");
            JToken positionToken = p.GetRaw("position");
            bool hasTarget = targetToken != null
                && targetToken.Type != JTokenType.Null
                && !string.IsNullOrWhiteSpace(targetToken.ToString());
            bool hasPosition = positionToken != null
                && positionToken.Type != JTokenType.Null;

            if (hasTarget == hasPosition)
            {
                return new ErrorResponse(
                    "Provide exactly one of 'target' or normalized 'position'.");
            }

            // Newly loaded scenes and freshly enabled panels can still have a
            // pending uGUI rebuild when an MCP command arrives between player
            // frames. Resolve it synchronously so target centers and raycasts
            // use the current runtime layout instead of the previous frame.
            Canvas.ForceUpdateCanvases();

            if (!TryResolveGamePoint(
                    p,
                    targetToken,
                    positionToken,
                    out Vector2 gamePoint,
                    out Vector2 normalizedPosition,
                    out GameObject target,
                    out string pointError))
            {
                return new ErrorResponse(pointError);
            }

            if (!TryCreatePointerContext(
                    normalizedPosition,
                    target,
                    out UiPointerContext context,
                    out string contextError))
            {
                return new ErrorResponse(contextError);
            }

            Type clickHandlerType = ResolveUnityUiType(
                "UnityEngine.EventSystems.IPointerClickHandler");
            GameObject clickHandler = GetEventHandler(
                context.ExecuteEventsType,
                clickHandlerType,
                context.RaycastTarget);
            if (clickHandler == null)
            {
                return new ErrorResponse(
                    $"Runtime UI element '{context.RaycastTarget.name}' has no pointer-click handler in its hierarchy.");
            }

            if (!TryValidateClickHandlerInteractable(clickHandler, out string clickError))
            {
                return new ErrorResponse(clickError);
            }

            string sceneBefore = SceneManager.GetActiveScene().name;
            string requestedTargetName = target?.name;
            string requestedTargetPath = target == null
                ? null
                : GameObjectLookup.GetGameObjectPath(target);
            int? requestedTargetId = target == null
                ? null
                : target.GetInstanceIDCompat();
            string hitTargetName = context.RaycastTarget.name;
            string hitTargetPath = GameObjectLookup.GetGameObjectPath(
                context.RaycastTarget);
            int hitTargetId = context.RaycastTarget.GetInstanceIDCompat();
            string clickHandlerName = clickHandler.name;
            string clickHandlerPath = GameObjectLookup.GetGameObjectPath(
                clickHandler);
            int clickHandlerId = clickHandler.GetInstanceIDCompat();

            GameObject downHandler = ExecutePointerEvent(
                context,
                "UnityEngine.EventSystems.IPointerDownHandler",
                "pointerDownHandler");
            SetPropertyIfWritable(
                context.PointerEventData,
                "pointerPress",
                downHandler ?? clickHandler);
            SetPropertyIfWritable(
                context.PointerEventData,
                "rawPointerPress",
                context.RaycastTarget);
            SetPropertyIfWritable(
                context.PointerEventData,
                "pointerClick",
                clickHandler);

            GameObject upHandler = ExecutePointerEvent(
                context,
                "UnityEngine.EventSystems.IPointerUpHandler",
                "pointerUpHandler");
            GameObject invokedClickHandler = ExecutePointerEvent(
                context,
                "UnityEngine.EventSystems.IPointerClickHandler",
                "pointerClickHandler");
            EditorApplication.QueuePlayerLoopUpdate();

            string sceneAfter = EditorApplication.isPlaying
                ? SceneManager.GetActiveScene().name
                : null;

            return new SuccessResponse(
                context.RaycastSource == "event_system"
                    ? "Left click was dispatched through Unity's runtime EventSystem."
                    : "Left click was dispatched to an explicitly targeted UI element before its first Game View render.",
                new
                {
                    target = target == null
                        ? null
                        : new
                        {
                            name = requestedTargetName,
                            path = requestedTargetPath,
                            instanceId = requestedTargetId,
                        },
                    raycastTarget = new
                    {
                        name = hitTargetName,
                        path = hitTargetPath,
                        instanceId = hitTargetId,
                    },
                    clickHandler = new
                    {
                        name = clickHandlerName,
                        path = clickHandlerPath,
                        instanceId = clickHandlerId,
                    },
                    normalizedPosition = new
                    {
                        x = normalizedPosition.x,
                        y = normalizedPosition.y,
                        origin = "top_left",
                    },
                    gamePosition = new
                    {
                        x = gamePoint.x,
                        y = gamePoint.y,
                    },
                    eventsInvoked = new
                    {
                        pointerDown = downHandler != null,
                        pointerUp = upHandler != null,
                        pointerClick = invokedClickHandler != null,
                    },
                    activeSceneAtDispatch = sceneBefore,
                    activeSceneAfterInput = sceneAfter,
                    backend = "runtime_event_system",
                    raycastSource = context.RaycastSource,
                    layoutPreparedBeforeRaycast = true,
                    note = "Pointer handlers were invoked synchronously; subsequent coroutine-driven effects may complete on later player frames.",
                });
        }

        internal static bool TryResolveGamePoint(
            ToolParams p,
            JToken targetToken,
            JToken positionToken,
            out Vector2 gamePoint,
            out Vector2 normalizedPosition,
            out GameObject target,
            out string error)
        {
            gamePoint = default;
            normalizedPosition = default;
            target = null;
            error = null;

            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            if (screenWidth < 1 || screenHeight < 1)
            {
                error = "The active Game View has no valid render size.";
                return false;
            }

            if (positionToken != null && positionToken.Type != JTokenType.Null)
            {
                if (!TryReadNormalizedPosition(
                        positionToken,
                        out normalizedPosition,
                        out error))
                {
                    return false;
                }

                gamePoint = new Vector2(
                    normalizedPosition.x * Math.Max(0, screenWidth - 1),
                    normalizedPosition.y * Math.Max(0, screenHeight - 1));
                return true;
            }

            if (!TryResolveTarget(
                    targetToken,
                    p.Get("search_method"),
                    includeInactive: false,
                    out target,
                    out error))
            {
                return false;
            }

            RectTransform rectTransform = target.GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                error = $"Target '{target.name}' has no RectTransform and cannot define a UI click position.";
                return false;
            }

            Camera eventCamera = ResolveEventCamera(target);
            Vector3 worldCenter = rectTransform.TransformPoint(rectTransform.rect.center);
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(
                eventCamera,
                worldCenter);
            if (screenPoint.x < 0f
                || screenPoint.x >= screenWidth
                || screenPoint.y < 0f
                || screenPoint.y >= screenHeight)
            {
                error = $"Target '{target.name}' is outside the active Game View render area.";
                return false;
            }

            float maxScreenX = Math.Max(1, screenWidth - 1);
            float maxScreenY = Math.Max(1, screenHeight - 1);
            normalizedPosition = new Vector2(
                Mathf.Clamp01(screenPoint.x / maxScreenX),
                Mathf.Clamp01((maxScreenY - screenPoint.y) / maxScreenY));
            gamePoint = new Vector2(
                normalizedPosition.x * Math.Max(0, screenWidth - 1),
                normalizedPosition.y * Math.Max(0, screenHeight - 1));
            return true;
        }

        internal static bool TryReadNormalizedPosition(
            JToken token,
            out Vector2 position,
            out string error)
        {
            position = default;
            error = null;
            if (!(token is JArray values) || values.Count != 2)
            {
                error = "'position' must be [x, y] in normalized Game View coordinates.";
                return false;
            }

            if (!TryReadUnitCoordinate(values[0], out float x)
                || !TryReadUnitCoordinate(values[1], out float y))
            {
                error = "'position' values must be finite numbers between 0 and 1.";
                return false;
            }

            position = new Vector2(x, y);
            return true;
        }

        private static bool TryReadUnitCoordinate(
            JToken token,
            out float value)
        {
            value = 0f;
            if (token == null
                || token.Type != JTokenType.Float
                    && token.Type != JTokenType.Integer)
            {
                return false;
            }

            double number = token.Value<double>();
            if (double.IsNaN(number)
                || double.IsInfinity(number)
                || number < 0d
                || number > 1d)
            {
                return false;
            }

            value = (float)number;
            return true;
        }

        internal static bool TryResolveTarget(
            JToken targetToken,
            string requestedSearchMethod,
            bool includeInactive,
            out GameObject target,
            out string error)
        {
            target = null;
            error = null;
            string searchTerm = targetToken?.ToString();
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                error = "'target' must identify an active UI GameObject.";
                return false;
            }

            string searchMethod = requestedSearchMethod?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(searchMethod))
            {
                searchMethod = int.TryParse(searchTerm, out _)
                    ? "by_id"
                    : searchTerm.Contains("/")
                        ? "by_path"
                        : "by_name";
            }

            if (searchMethod != "by_id"
                && searchMethod != "by_name"
                && searchMethod != "by_path")
            {
                error = "'search_method' must be by_id, by_name, or by_path.";
                return false;
            }

            List<int> matches = GameObjectLookup.SearchGameObjects(
                searchMethod,
                searchTerm,
                includeInactive,
                maxResults: 2);
            if (matches.Count == 0)
            {
                error = $"UI target '{searchTerm}' was not found using {searchMethod}.";
                return false;
            }

            if (matches.Count > 1)
            {
                error = $"UI target '{searchTerm}' is ambiguous; use a hierarchy path or instance ID.";
                return false;
            }

            target = GameObjectLookup.FindById(matches[0]);
            if (target == null)
            {
                error = $"UI target '{searchTerm}' no longer exists.";
                return false;
            }

            if (!includeInactive && !target.activeInHierarchy)
            {
                error = $"UI target '{searchTerm}' is not active in the loaded scene.";
                return false;
            }

            return true;
        }

        private static Camera ResolveEventCamera(GameObject target)
        {
            Type canvasType = Type.GetType(
                "UnityEngine.Canvas, UnityEngine.UIModule",
                throwOnError: false);
            if (canvasType == null)
            {
                return Camera.main;
            }

            Component canvas = null;
            Transform current = target.transform;
            while (current != null && canvas == null)
            {
                canvas = current.gameObject.GetComponent(canvasType);
                current = current.parent;
            }

            if (canvas == null)
            {
                return Camera.main;
            }

            PropertyInfo renderModeProperty = canvasType.GetProperty(
                "renderMode",
                InstanceMembers);
            object renderMode = renderModeProperty?.GetValue(canvas);
            if (renderMode != null && Convert.ToInt32(renderMode) == 0)
            {
                return null;
            }

            PropertyInfo worldCameraProperty = canvasType.GetProperty(
                "worldCamera",
                InstanceMembers);
            return worldCameraProperty?.GetValue(canvas) as Camera ?? Camera.main;
        }

        internal static bool TryCreatePointerContext(
            Vector2 normalizedPosition,
            GameObject requestedTarget,
            out UiPointerContext context,
            out string error)
        {
            context = null;
            if (!TryResolveEventSystemTypes(
                    out Type eventSystemType,
                    out Type pointerEventDataType,
                    out Type raycastResultType,
                    out Type executeEventsType,
                    out error))
            {
                return false;
            }

            object eventSystem = eventSystemType.GetProperty(
                "current",
                StaticMembers)?.GetValue(null);
            if (eventSystem == null)
            {
                error = "No active runtime EventSystem was found in the loaded scene.";
                return false;
            }

            object pointerEventData = Activator.CreateInstance(
                pointerEventDataType,
                eventSystem);
            Vector2 screenPosition = NormalizedToScreen(normalizedPosition);
            SetPropertyIfWritable(pointerEventData, "position", screenPosition);
            SetPropertyIfWritable(pointerEventData, "pressPosition", screenPosition);
            SetPropertyIfWritable(pointerEventData, "delta", Vector2.zero);
            SetPropertyIfWritable(pointerEventData, "clickCount", 1);
            SetPropertyIfWritable(pointerEventData, "clickTime", Time.unscaledTime);
            SetPropertyIfWritable(pointerEventData, "eligibleForClick", true);

            PropertyInfo buttonProperty = pointerEventDataType.GetProperty(
                "button",
                InstanceMembers);
            if (buttonProperty == null || !buttonProperty.CanWrite)
            {
                error = "Unity's PointerEventData.button property is unavailable.";
                return false;
            }

            buttonProperty.SetValue(
                pointerEventData,
                Enum.Parse(buttonProperty.PropertyType, "Left"));

            Type resultListType = typeof(List<>).MakeGenericType(
                raycastResultType);
            IList raycastResults = (IList)Activator.CreateInstance(
                resultListType);
            MethodInfo raycastAll = eventSystemType.GetMethod(
                "RaycastAll",
                InstanceMembers,
                binder: null,
                types: new[] { pointerEventDataType, resultListType },
                modifiers: null);
            if (raycastAll == null)
            {
                error = "Unity's EventSystem.RaycastAll API is unavailable.";
                return false;
            }

            raycastAll.Invoke(
                eventSystem,
                new object[] { pointerEventData, raycastResults });

            object raycastResult;
            string raycastSource;
            if (raycastResults.Count > 0)
            {
                raycastResult = raycastResults[0];
                raycastSource = "event_system";
            }
            else if (requestedTarget != null
                && TryCreateUnrenderedTargetRaycastResult(
                    requestedTarget,
                    screenPosition,
                    raycastResultType,
                    out raycastResult))
            {
                raycastSource = "explicit_unrendered_target";
            }
            else
            {
                error = "No runtime UI element was hit at the requested position.";
                return false;
            }

            GameObject raycastTarget = raycastResultType.GetProperty(
                "gameObject",
                InstanceMembers)?.GetValue(raycastResult) as GameObject;
            if (raycastTarget == null)
            {
                error = "The first runtime UI raycast result has no GameObject target.";
                return false;
            }

            if (requestedTarget != null
                && !AreInSameHierarchyBranch(requestedTarget, raycastTarget))
            {
                error = $"UI target '{requestedTarget.name}' is occluded by '{raycastTarget.name}' at its center point.";
                return false;
            }

            SetPropertyIfWritable(
                pointerEventData,
                "pointerCurrentRaycast",
                raycastResult);
            SetPropertyIfWritable(
                pointerEventData,
                "pointerPressRaycast",
                raycastResult);

            context = new UiPointerContext(
                eventSystemType,
                pointerEventDataType,
                raycastResultType,
                executeEventsType,
                eventSystem,
                pointerEventData,
                raycastTarget,
                raycastSource);
            return true;
        }

        private static bool TryCreateUnrenderedTargetRaycastResult(
            GameObject requestedTarget,
            Vector2 screenPosition,
            Type raycastResultType,
            out object raycastResult)
        {
            // GraphicRaycaster deliberately skips depth -1 before a Canvas has
            // rendered. Only an explicit target may cross that boundary, and it
            // must still pass the Graphic, rectangle, CanvasGroup and raycaster
            // checks that are available without render-order metadata.
            raycastResult = null;
            Type graphicType = ResolveUnityUiType("UnityEngine.UI.Graphic");
            Type graphicRaycasterType = ResolveUnityUiType(
                "UnityEngine.UI.GraphicRaycaster");
            if (graphicType == null || graphicRaycasterType == null)
            {
                return false;
            }

            Component[] graphics = requestedTarget.GetComponentsInChildren(
                graphicType,
                includeInactive: false);
            foreach (Component graphic in graphics.OrderBy(component =>
                         component.gameObject == requestedTarget ? 0 : 1))
            {
                if (!(graphic is Behaviour behaviour)
                    || !behaviour.isActiveAndEnabled
                    || graphicType.GetProperty("raycastTarget", InstanceMembers)
                        ?.GetValue(graphic) as bool? != true
                    || graphicType.GetProperty("depth", InstanceMembers)
                        ?.GetValue(graphic) as int? != -1)
                {
                    continue;
                }

                CanvasRenderer canvasRenderer = graphic.GetComponent<CanvasRenderer>();
                RectTransform rectTransform = graphic.GetComponent<RectTransform>();
                Camera eventCamera = ResolveEventCamera(graphic.gameObject);
                if (canvasRenderer == null
                    || canvasRenderer.cull
                    || rectTransform == null
                    || !RectTransformUtility.RectangleContainsScreenPoint(
                        rectTransform,
                        screenPosition,
                        eventCamera))
                {
                    continue;
                }

                MethodInfo graphicRaycast = graphicType.GetMethod(
                    "Raycast",
                    InstanceMembers,
                    binder: null,
                    types: new[] { typeof(Vector2), typeof(Camera) },
                    modifiers: null);
                if (graphicRaycast == null
                    || !(bool)graphicRaycast.Invoke(
                        graphic,
                        new object[] { screenPosition, eventCamera }))
                {
                    continue;
                }

                Component raycaster = FindComponentInParents(
                    graphic.gameObject,
                    graphicRaycasterType);
                if (raycaster == null)
                {
                    continue;
                }

                raycastResult = Activator.CreateInstance(raycastResultType);
                SetPropertyIfWritable(
                    raycastResult,
                    "gameObject",
                    graphic.gameObject);
                SetFieldIfPresent(raycastResult, "module", raycaster);
                SetFieldIfPresent(raycastResult, "screenPosition", screenPosition);
                SetFieldIfPresent(raycastResult, "distance", 0f);
                SetFieldIfPresent(raycastResult, "index", 0f);
                SetFieldIfPresent(raycastResult, "depth", -1);
                return true;
            }

            return false;
        }

        private static Component FindComponentInParents(
            GameObject target,
            Type componentType)
        {
            Transform current = target?.transform;
            while (current != null)
            {
                Component component = current.gameObject.GetComponent(componentType);
                if (component != null)
                {
                    return component;
                }

                current = current.parent;
            }

            return null;
        }

        private static bool TryValidateClickHandlerInteractable(
            GameObject clickHandler,
            out string error)
        {
            foreach (Component component in clickHandler.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                MethodInfo isInteractable = component.GetType().GetMethod(
                    "IsInteractable",
                    InstanceMembers,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);
                string declaringType = isInteractable?.DeclaringType?.FullName;
                if (isInteractable?.ReturnType != typeof(bool)
                    || declaringType == null
                    || !declaringType.StartsWith(
                        "UnityEngine.UI.",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!(bool)isInteractable.Invoke(component, null))
                {
                    error = $"Runtime UI click handler '{clickHandler.name}' is not interactable.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private static bool TryResolveEventSystemTypes(
            out Type eventSystemType,
            out Type pointerEventDataType,
            out Type raycastResultType,
            out Type executeEventsType,
            out string error)
        {
            eventSystemType = ResolveUnityUiType(
                "UnityEngine.EventSystems.EventSystem");
            pointerEventDataType = ResolveUnityUiType(
                "UnityEngine.EventSystems.PointerEventData");
            raycastResultType = ResolveUnityUiType(
                "UnityEngine.EventSystems.RaycastResult");
            executeEventsType = ResolveUnityUiType(
                "UnityEngine.EventSystems.ExecuteEvents");
            if (eventSystemType != null
                && pointerEventDataType != null
                && raycastResultType != null
                && executeEventsType != null)
            {
                error = null;
                return true;
            }

            error = "Unity's uGUI EventSystem types are unavailable; install or enable com.unity.ugui.";
            return false;
        }

        internal static Type ResolveUnityUiType(string fullName)
        {
            return Type.GetType(
                $"{fullName}, UnityEngine.UI",
                throwOnError: false);
        }

        private static bool AreInSameHierarchyBranch(
            GameObject requestedTarget,
            GameObject raycastTarget)
        {
            return requestedTarget == raycastTarget
                || raycastTarget.transform.IsChildOf(requestedTarget.transform)
                || requestedTarget.transform.IsChildOf(raycastTarget.transform);
        }

        internal static Vector2 NormalizedToScreen(Vector2 normalizedPosition)
        {
            return new Vector2(
                normalizedPosition.x * Math.Max(0, Screen.width - 1),
                (1f - normalizedPosition.y) * Math.Max(0, Screen.height - 1));
        }

        internal static bool TryRaycastAt(
            UiPointerContext context,
            Vector2 screenPosition,
            out GameObject raycastTarget,
            out object raycastResult)
        {
            raycastTarget = null;
            raycastResult = null;
            if (context == null)
            {
                return false;
            }

            SetPropertyIfWritable(
                context.PointerEventData,
                "position",
                screenPosition);
            Type resultListType = typeof(List<>).MakeGenericType(
                context.RaycastResultType);
            IList raycastResults = (IList)Activator.CreateInstance(
                resultListType);
            MethodInfo raycastAll = context.EventSystemType.GetMethod(
                "RaycastAll",
                InstanceMembers,
                binder: null,
                types: new[] { context.PointerEventDataType, resultListType },
                modifiers: null);
            if (raycastAll == null)
            {
                return false;
            }

            raycastAll.Invoke(
                context.EventSystem,
                new[] { context.PointerEventData, raycastResults });
            if (raycastResults.Count == 0)
            {
                return false;
            }

            raycastResult = raycastResults[0];
            raycastTarget = context.RaycastResultType.GetProperty(
                "gameObject",
                InstanceMembers)?.GetValue(raycastResult) as GameObject;
            if (raycastTarget == null)
            {
                raycastResult = null;
                return false;
            }

            SetPropertyIfWritable(
                context.PointerEventData,
                "pointerCurrentRaycast",
                raycastResult);
            return true;
        }

        internal static GameObject GetEventHandler(
            Type executeEventsType,
            Type handlerType,
            GameObject root)
        {
            if (handlerType == null)
            {
                return null;
            }

            MethodInfo method = executeEventsType.GetMethods(StaticMembers)
                .FirstOrDefault(candidate =>
                    candidate.Name == "GetEventHandler"
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetGenericArguments().Length == 1
                    && candidate.GetParameters().Length == 1);
            return method?.MakeGenericMethod(handlerType)
                .Invoke(null, new object[] { root }) as GameObject;
        }

        internal static GameObject ExecutePointerEvent(
            UiPointerContext context,
            string handlerTypeName,
            string eventFunctionPropertyName)
        {
            return ExecutePointerEvent(
                context,
                context.RaycastTarget,
                handlerTypeName,
                eventFunctionPropertyName);
        }

        internal static GameObject ExecutePointerEvent(
            UiPointerContext context,
            GameObject root,
            string handlerTypeName,
            string eventFunctionPropertyName)
        {
            Type handlerType = ResolveUnityUiType(handlerTypeName);
            if (handlerType == null)
            {
                throw new InvalidOperationException(
                    $"Unity UI handler type '{handlerTypeName}' is unavailable.");
            }

            PropertyInfo eventFunctionProperty = context.ExecuteEventsType.GetProperty(
                eventFunctionPropertyName,
                StaticMembers);
            object eventFunction = eventFunctionProperty?.GetValue(null);
            MethodInfo method = context.ExecuteEventsType.GetMethods(StaticMembers)
                .FirstOrDefault(candidate =>
                    candidate.Name == "ExecuteHierarchy"
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetGenericArguments().Length == 1
                    && candidate.GetParameters().Length == 3);
            if (eventFunction == null || method == null)
            {
                throw new InvalidOperationException(
                    $"Unity's ExecuteEvents.{eventFunctionPropertyName} API is unavailable.");
            }

            return method.MakeGenericMethod(handlerType)
                .Invoke(
                    null,
                    new[]
                    {
                        root,
                        context.PointerEventData,
                        eventFunction,
                    }) as GameObject;
        }

        internal static bool ExecutePointerEventDirect(
            UiPointerContext context,
            GameObject handler,
            string handlerTypeName,
            string eventFunctionPropertyName)
        {
            Type handlerType = ResolveUnityUiType(handlerTypeName);
            PropertyInfo eventFunctionProperty = context.ExecuteEventsType.GetProperty(
                eventFunctionPropertyName,
                StaticMembers);
            object eventFunction = eventFunctionProperty?.GetValue(null);
            MethodInfo method = context.ExecuteEventsType.GetMethods(StaticMembers)
                .FirstOrDefault(candidate =>
                    candidate.Name == "Execute"
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetGenericArguments().Length == 1
                    && candidate.GetParameters().Length == 3);
            if (handlerType == null || eventFunction == null || method == null)
            {
                throw new InvalidOperationException(
                    $"Unity's ExecuteEvents.{eventFunctionPropertyName} API is unavailable.");
            }

            return (bool)method.MakeGenericMethod(handlerType)
                .Invoke(
                    null,
                    new[]
                    {
                        handler,
                        context.PointerEventData,
                        eventFunction,
                    });
        }

        internal static void SetPropertyIfWritable(
            object target,
            string propertyName,
            object value)
        {
            PropertyInfo property = target.GetType().GetProperty(
                propertyName,
                InstanceMembers);
            if (property?.CanWrite == true)
            {
                property.SetValue(target, value);
            }
        }

        private static void SetFieldIfPresent(
            object target,
            string fieldName,
            object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, InstanceMembers);
            field?.SetValue(target, value);
        }

        internal sealed class UiPointerContext
        {
            public UiPointerContext(
                Type eventSystemType,
                Type pointerEventDataType,
                Type raycastResultType,
                Type executeEventsType,
                object eventSystem,
                object pointerEventData,
                GameObject raycastTarget,
                string raycastSource)
            {
                EventSystemType = eventSystemType;
                PointerEventDataType = pointerEventDataType;
                RaycastResultType = raycastResultType;
                ExecuteEventsType = executeEventsType;
                EventSystem = eventSystem;
                PointerEventData = pointerEventData;
                RaycastTarget = raycastTarget;
                RaycastSource = raycastSource;
            }

            public Type EventSystemType { get; }

            public Type PointerEventDataType { get; }

            public Type RaycastResultType { get; }

            public Type ExecuteEventsType { get; }

            public object EventSystem { get; }

            public object PointerEventData { get; }

            public GameObject RaycastTarget { get; }

            public string RaycastSource { get; }
        }
    }
}
