using System;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.PlayMode
{
    /// <summary>
    /// Deterministic runtime-uGUI pointer gestures built on the same EventSystem
    /// addressing and raycast path as click_ui.
    /// </summary>
    internal static class PlayModeGestureActions
    {
        private const int DefaultDragSteps = 5;
        private const int MinDragSteps = 1;
        private const int MaxDragSteps = 64;
        private const float MaxScrollDelta = 100f;
        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static object Drag(ToolParams p)
        {
            object playModeError = RequireMutablePlayMode("drag_ui");
            if (playModeError != null)
            {
                return playModeError;
            }

            if (!TryPreparePointerContext(
                    p,
                    out InteractPlayMode.UiPointerContext context,
                    out GameObject requestedTarget,
                    out Vector2 startNormalized,
                    out string contextError))
            {
                return ErrorResponse.FromCode(
                    "ui_pointer_context_failed",
                    contextError);
            }

            if (!InteractPlayMode.TryReadNormalizedPosition(
                    p.GetRaw("end_position"),
                    out Vector2 endNormalized,
                    out string endError))
            {
                return ErrorResponse.FromCode("invalid_drag_end", endError);
            }

            if (!TryReadDragSteps(p.GetRaw("steps"), out int steps))
            {
                return ErrorResponse.FromCode(
                    "invalid_drag_steps",
                    $"'steps' must be an integer between {MinDragSteps} and {MaxDragSteps}.");
            }

            Type dragHandlerType = InteractPlayMode.ResolveUnityUiType(
                "UnityEngine.EventSystems.IDragHandler");
            GameObject dragHandler = InteractPlayMode.GetEventHandler(
                context.ExecuteEventsType,
                dragHandlerType,
                context.RaycastTarget);
            if (dragHandler == null)
            {
                return ErrorResponse.FromCode(
                    "drag_handler_required",
                    $"Runtime UI element '{context.RaycastTarget.name}' has no IDragHandler in its hierarchy.");
            }

            Vector2 startScreen = InteractPlayMode.NormalizedToScreen(startNormalized);
            Vector2 endScreen = InteractPlayMode.NormalizedToScreen(endNormalized);
            InteractPlayMode.SetPropertyIfWritable(
                context.PointerEventData,
                "pointerDrag",
                dragHandler);
            InteractPlayMode.SetPropertyIfWritable(
                context.PointerEventData,
                "pressPosition",
                startScreen);
            InteractPlayMode.SetPropertyIfWritable(
                context.PointerEventData,
                "position",
                startScreen);
            InteractPlayMode.SetPropertyIfWritable(
                context.PointerEventData,
                "delta",
                Vector2.zero);
            InteractPlayMode.SetPropertyIfWritable(
                context.PointerEventData,
                "eligibleForClick",
                false);
            InteractPlayMode.SetPropertyIfWritable(
                context.PointerEventData,
                "useDragThreshold",
                false);

            bool initialized = InteractPlayMode.ExecutePointerEventDirect(
                context,
                dragHandler,
                "UnityEngine.EventSystems.IInitializePotentialDragHandler",
                "initializePotentialDrag");
            InteractPlayMode.SetPropertyIfWritable(
                context.PointerEventData,
                "dragging",
                true);
            bool began = InteractPlayMode.ExecutePointerEventDirect(
                context,
                dragHandler,
                "UnityEngine.EventSystems.IBeginDragHandler",
                "beginDragHandler");

            int dragEventsInvoked = 0;
            Vector2 previousScreen = startScreen;
            GameObject finalRaycastTarget = context.RaycastTarget;
            for (int index = 1; index <= steps; index++)
            {
                Vector2 currentScreen = Vector2.Lerp(
                    startScreen,
                    endScreen,
                    index / (float)steps);
                InteractPlayMode.SetPropertyIfWritable(
                    context.PointerEventData,
                    "delta",
                    currentScreen - previousScreen);
                InteractPlayMode.SetPropertyIfWritable(
                    context.PointerEventData,
                    "position",
                    currentScreen);
                if (InteractPlayMode.TryRaycastAt(
                        context,
                        currentScreen,
                        out GameObject stepRaycastTarget,
                        out _))
                {
                    finalRaycastTarget = stepRaycastTarget;
                }
                else
                {
                    finalRaycastTarget = null;
                }

                if (InteractPlayMode.ExecutePointerEventDirect(
                        context,
                        dragHandler,
                        "UnityEngine.EventSystems.IDragHandler",
                        "dragHandler"))
                {
                    dragEventsInvoked++;
                }
                previousScreen = currentScreen;
            }

            GameObject dropHandler = finalRaycastTarget == null
                ? null
                : InteractPlayMode.ExecutePointerEvent(
                    context,
                    finalRaycastTarget,
                    "UnityEngine.EventSystems.IDropHandler",
                    "dropHandler");
            bool ended = InteractPlayMode.ExecutePointerEventDirect(
                context,
                dragHandler,
                "UnityEngine.EventSystems.IEndDragHandler",
                "endDragHandler");
            InteractPlayMode.SetPropertyIfWritable(
                context.PointerEventData,
                "dragging",
                false);
            InteractPlayMode.SetPropertyIfWritable(
                context.PointerEventData,
                "pointerDrag",
                null);
            InteractPlayMode.SetPropertyIfWritable(
                context.PointerEventData,
                "delta",
                Vector2.zero);
            Canvas.ForceUpdateCanvases();
            EditorApplication.QueuePlayerLoopUpdate();

            return new SuccessResponse(
                "Runtime UI drag was dispatched through Unity's EventSystem.",
                new
                {
                    target = DescribeOptionalTarget(requestedTarget),
                    raycastTarget = DescribeTarget(context.RaycastTarget),
                    dragHandler = DescribeTarget(dragHandler),
                    dropHandler = DescribeOptionalTarget(dropHandler),
                    finalRaycastTarget = DescribeOptionalTarget(finalRaycastTarget),
                    startPosition = DescribeNormalized(startNormalized),
                    endPosition = DescribeNormalized(endNormalized),
                    steps,
                    eventsInvoked = new
                    {
                        initializePotentialDrag = initialized,
                        beginDrag = began,
                        drag = dragEventsInvoked,
                        drop = dropHandler != null,
                        endDrag = ended,
                    },
                    backend = "runtime_event_system",
                    raycastSource = context.RaycastSource,
                    note = "Gesture handlers were invoked synchronously; subsequent coroutine-driven effects may complete on later player frames.",
                });
        }

        internal static object Scroll(ToolParams p)
        {
            object playModeError = RequireMutablePlayMode("scroll_ui");
            if (playModeError != null)
            {
                return playModeError;
            }

            if (!TryPreparePointerContext(
                    p,
                    out InteractPlayMode.UiPointerContext context,
                    out GameObject requestedTarget,
                    out Vector2 normalizedPosition,
                    out string contextError))
            {
                return ErrorResponse.FromCode(
                    "ui_pointer_context_failed",
                    contextError);
            }

            if (!TryReadScrollDelta(
                    p.GetRaw("scroll_delta"),
                    out Vector2 scrollDelta,
                    out string deltaError))
            {
                return ErrorResponse.FromCode("invalid_scroll_delta", deltaError);
            }

            Type scrollHandlerType = InteractPlayMode.ResolveUnityUiType(
                "UnityEngine.EventSystems.IScrollHandler");
            GameObject scrollHandler = InteractPlayMode.GetEventHandler(
                context.ExecuteEventsType,
                scrollHandlerType,
                context.RaycastTarget);
            if (scrollHandler == null)
            {
                return ErrorResponse.FromCode(
                    "scroll_handler_required",
                    $"Runtime UI element '{context.RaycastTarget.name}' has no IScrollHandler in its hierarchy.");
            }

            ScrollSnapshot before = ScrollSnapshot.Capture(scrollHandler);
            InteractPlayMode.SetPropertyIfWritable(
                context.PointerEventData,
                "scrollDelta",
                scrollDelta);
            GameObject invokedHandler = InteractPlayMode.ExecutePointerEvent(
                context,
                context.RaycastTarget,
                "UnityEngine.EventSystems.IScrollHandler",
                "scrollHandler");
            Canvas.ForceUpdateCanvases();
            EditorApplication.QueuePlayerLoopUpdate();
            ScrollSnapshot after = ScrollSnapshot.Capture(scrollHandler);

            return new SuccessResponse(
                "Runtime UI scroll was dispatched through Unity's EventSystem.",
                new
                {
                    target = DescribeOptionalTarget(requestedTarget),
                    raycastTarget = DescribeTarget(context.RaycastTarget),
                    scrollHandler = DescribeTarget(scrollHandler),
                    normalizedPosition = DescribeNormalized(normalizedPosition),
                    scrollDelta = new
                    {
                        x = scrollDelta.x,
                        y = scrollDelta.y,
                        units = "unity_scroll",
                        positiveY = "up",
                    },
                    eventInvoked = invokedHandler != null,
                    before = before?.Describe(),
                    after = after?.Describe(),
                    normalizedPositionChanged = ScrollSnapshot.HasMoved(before, after),
                    backend = "runtime_event_system",
                    raycastSource = context.RaycastSource,
                });
        }

        private static object RequireMutablePlayMode(string action)
        {
            if (!EditorApplication.isPlaying
                || !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return ErrorResponse.FromCode(
                    "play_mode_required",
                    $"{action} requires the Unity Editor to be fully in Play Mode.");
            }
            if (EditorApplication.isPaused)
            {
                return ErrorResponse.FromCode(
                    "play_mode_paused",
                    $"{action} is unavailable while Play Mode is paused.");
            }
            return null;
        }

        private static bool TryPreparePointerContext(
            ToolParams p,
            out InteractPlayMode.UiPointerContext context,
            out GameObject requestedTarget,
            out Vector2 normalizedPosition,
            out string error)
        {
            context = null;
            requestedTarget = null;
            normalizedPosition = default;
            JToken targetToken = p.GetRaw("target");
            JToken positionToken = p.GetRaw("position");
            bool hasTarget = targetToken != null
                && targetToken.Type != JTokenType.Null
                && !string.IsNullOrWhiteSpace(targetToken.ToString());
            bool hasPosition = positionToken != null
                && positionToken.Type != JTokenType.Null;
            if (hasTarget == hasPosition)
            {
                error = "Provide exactly one of 'target' or normalized 'position'.";
                return false;
            }
            if (!hasTarget && !string.IsNullOrWhiteSpace(p.Get("search_method")))
            {
                error = "'search_method' can only be used with a target.";
                return false;
            }

            Canvas.ForceUpdateCanvases();
            if (!InteractPlayMode.TryResolveGamePoint(
                    p,
                    targetToken,
                    positionToken,
                    out _,
                    out normalizedPosition,
                    out requestedTarget,
                    out error))
            {
                return false;
            }

            return InteractPlayMode.TryCreatePointerContext(
                normalizedPosition,
                requestedTarget,
                out context,
                out error);
        }

        private static bool TryReadDragSteps(JToken token, out int steps)
        {
            steps = DefaultDragSteps;
            if (token == null || token.Type == JTokenType.Null)
            {
                return true;
            }
            if (token.Type != JTokenType.Integer)
            {
                return false;
            }

            steps = token.Value<int>();
            return steps >= MinDragSteps && steps <= MaxDragSteps;
        }

        private static bool TryReadScrollDelta(
            JToken token,
            out Vector2 delta,
            out string error)
        {
            delta = default;
            if (!(token is JArray values) || values.Count != 2)
            {
                error = "'scroll_delta' must be [x, y] in Unity scroll units.";
                return false;
            }
            if (!TryReadBoundedFloat(values[0], out float x)
                || !TryReadBoundedFloat(values[1], out float y)
                || Mathf.Approximately(x, 0f) && Mathf.Approximately(y, 0f))
            {
                error = $"'scroll_delta' values must be finite, within {-MaxScrollDelta:g}..{MaxScrollDelta:g}, and not both zero.";
                return false;
            }

            delta = new Vector2(x, y);
            error = null;
            return true;
        }

        private static bool TryReadBoundedFloat(JToken token, out float value)
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
                || number < -MaxScrollDelta
                || number > MaxScrollDelta)
            {
                return false;
            }

            value = (float)number;
            return true;
        }

        private static object DescribeNormalized(Vector2 position)
        {
            return new
            {
                x = position.x,
                y = position.y,
                origin = "top_left",
            };
        }

        private static object DescribeOptionalTarget(GameObject target)
        {
            return target == null ? null : DescribeTarget(target);
        }

        private static object DescribeTarget(GameObject target)
        {
            return new
            {
                name = target.name,
                path = GameObjectLookup.GetGameObjectPath(target),
                instanceId = target.GetInstanceIDCompat(),
            };
        }

        private sealed class ScrollSnapshot
        {
            private ScrollSnapshot(
                bool horizontal,
                bool vertical,
                Vector2 normalizedPosition,
                Vector2 velocity)
            {
                Horizontal = horizontal;
                Vertical = vertical;
                NormalizedPosition = normalizedPosition;
                Velocity = velocity;
            }

            private bool Horizontal { get; }
            private bool Vertical { get; }
            private Vector2 NormalizedPosition { get; }
            private Vector2 Velocity { get; }

            internal static ScrollSnapshot Capture(GameObject handler)
            {
                Type scrollRectType = InteractPlayMode.ResolveUnityUiType(
                    "UnityEngine.UI.ScrollRect");
                Component scrollRect = scrollRectType == null
                    ? null
                    : handler?.GetComponent(scrollRectType);
                if (scrollRect == null)
                {
                    return null;
                }

                return new ScrollSnapshot(
                    Read<bool>(scrollRect, "horizontal"),
                    Read<bool>(scrollRect, "vertical"),
                    Read<Vector2>(scrollRect, "normalizedPosition"),
                    Read<Vector2>(scrollRect, "velocity"));
            }

            internal static bool? HasMoved(
                ScrollSnapshot before,
                ScrollSnapshot after)
            {
                return before == null || after == null
                    ? (bool?)null
                    : Vector2.Distance(
                        before.NormalizedPosition,
                        after.NormalizedPosition) > 0.0001f;
            }

            internal object Describe()
            {
                return new
                {
                    horizontal = Horizontal,
                    vertical = Vertical,
                    horizontalNormalizedPosition = NormalizedPosition.x,
                    verticalNormalizedPosition = NormalizedPosition.y,
                    velocity = new
                    {
                        x = Velocity.x,
                        y = Velocity.y,
                    },
                };
            }

            private static T Read<T>(Component component, string propertyName)
            {
                PropertyInfo property = component.GetType().GetProperty(
                    propertyName,
                    InstanceMembers);
                return property?.CanRead == true
                    && property.PropertyType == typeof(T)
                        ? (T)property.GetValue(component)
                        : default;
            }
        }
    }
}
