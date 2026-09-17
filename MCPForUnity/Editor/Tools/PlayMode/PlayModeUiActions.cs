using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.PlayMode
{
    /// <summary>
    /// Bounded runtime-uGUI inspection and deterministic value mutations. Optional
    /// uGUI/TMP types are resolved through reflection so the core package does not
    /// acquire hard assembly references to either package.
    /// </summary>
    internal static class PlayModeUiActions
    {
        private const int MaxInputCharacters = 16 * 1024;
        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        internal static object Inspect(ToolParams p)
        {
            object playModeError = RequirePlayMode(allowPaused: true, "inspect_ui");
            if (playModeError != null)
            {
                return playModeError;
            }

            JToken targetToken = p.GetRaw("target");
            string requestedTarget = targetToken?.ToString();
            if (!InteractPlayMode.TryResolveTarget(
                    targetToken,
                    p.Get("search_method"),
                    includeInactive: true,
                    out GameObject target,
                    out string targetError))
            {
                if (!string.IsNullOrEmpty(targetError)
                    && (targetError.Contains("was not found")
                        || targetError.Contains("no longer exists")))
                {
                    return new SuccessResponse(
                        "Runtime UI target does not currently exist.",
                        new
                        {
                            exists = false,
                            query = requestedTarget,
                            searchMethod = NormalizeSearchMethod(
                                requestedTarget,
                                p.Get("search_method")),
                        });
                }

                return ErrorResponse.FromCode(
                    "ui_target_resolution_failed",
                    targetError ?? "Runtime UI target could not be resolved.");
            }

            Canvas.ForceUpdateCanvases();
            bool includeText = p.GetBool("include_text", true);
            return new SuccessResponse(
                "Runtime UI target inspected.",
                BuildInspection(target, includeText));
        }

        internal static object SetText(ToolParams p)
        {
            object playModeError = RequirePlayMode(allowPaused: false, "set_text");
            if (playModeError != null)
            {
                return playModeError;
            }

            if (!InteractPlayMode.TryResolveTarget(
                    p.GetRaw("target"),
                    p.Get("search_method"),
                    includeInactive: false,
                    out GameObject target,
                    out string targetError))
            {
                return ErrorResponse.FromCode(
                    "ui_target_resolution_failed",
                    targetError ?? "Runtime UI target could not be resolved.");
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

            if (!TryFindInputField(target, out Component inputField, out string inputError))
            {
                return ErrorResponse.FromCode("input_field_required", inputError);
            }
            if (!IsInteractable(inputField))
            {
                return ErrorResponse.FromCode(
                    "ui_not_interactable",
                    $"Runtime input field '{inputField.gameObject.name}' is not interactable.");
            }

            bool submit = p.GetBool("submit", false);
            SubmitContext submitContext = null;
            if (submit && !TryCreateSubmitContext(inputField.gameObject, out submitContext, out string submitError))
            {
                return ErrorResponse.FromCode("ui_submit_unavailable", submitError);
            }

            PropertyInfo textProperty = inputField.GetType().GetProperty(
                "text",
                InstanceMembers);
            if (textProperty?.CanRead != true
                || textProperty.CanWrite != true
                || textProperty.PropertyType != typeof(string))
            {
                return ErrorResponse.FromCode(
                    "input_text_unavailable",
                    $"Runtime input field '{inputField.gameObject.name}' exposes no writable text property.");
            }

            string previousText = textProperty.GetValue(inputField) as string ?? string.Empty;
            textProperty.SetValue(inputField, requestedText);
            string storedText = textProperty.GetValue(inputField) as string ?? string.Empty;

            bool submitted = false;
            if (submit)
            {
                submitted = DispatchSubmit(submitContext);
            }
            EditorApplication.QueuePlayerLoopUpdate();

            bool sensitive = p.GetBool("sensitive", false) || IsSensitiveInput(inputField);
            return new SuccessResponse(
                submitted
                    ? "Runtime input text was updated and submitted."
                    : "Runtime input text was updated.",
                new
                {
                    target = DescribeTarget(target),
                    component = DescribeComponent(inputField),
                    changed = !string.Equals(previousText, storedText, StringComparison.Ordinal),
                    submitted,
                    sensitive,
                    textRedacted = true,
                    requestedLength = sensitive ? (int?)null : requestedText.Length,
                    storedLength = sensitive ? (int?)null : storedText.Length,
                });
        }

        internal static object SetToggle(ToolParams p)
        {
            object playModeError = RequirePlayMode(allowPaused: false, "set_toggle");
            if (playModeError != null)
            {
                return playModeError;
            }

            if (!InteractPlayMode.TryResolveTarget(
                    p.GetRaw("target"),
                    p.Get("search_method"),
                    includeInactive: false,
                    out GameObject target,
                    out string targetError))
            {
                return ErrorResponse.FromCode(
                    "ui_target_resolution_failed",
                    targetError ?? "Runtime UI target could not be resolved.");
            }

            if (!TryReadRequiredBool(p.GetRaw("value"), out bool desiredValue))
            {
                return ErrorResponse.FromCode(
                    "value_required",
                    "'value' must be true or false for set_toggle.");
            }
            if (!TryFindToggle(target, out Component toggle, out string toggleError))
            {
                return ErrorResponse.FromCode("toggle_required", toggleError);
            }
            if (!IsInteractable(toggle))
            {
                return ErrorResponse.FromCode(
                    "ui_not_interactable",
                    $"Runtime Toggle '{toggle.gameObject.name}' is not interactable.");
            }

            PropertyInfo isOnProperty = toggle.GetType().GetProperty(
                "isOn",
                InstanceMembers);
            if (isOnProperty?.CanRead != true
                || isOnProperty.CanWrite != true
                || isOnProperty.PropertyType != typeof(bool))
            {
                return ErrorResponse.FromCode(
                    "toggle_value_unavailable",
                    $"Runtime Toggle '{toggle.gameObject.name}' exposes no writable isOn property.");
            }

            bool previousValue = (bool)isOnProperty.GetValue(toggle);
            if (previousValue != desiredValue)
            {
                isOnProperty.SetValue(toggle, desiredValue);
            }
            bool storedValue = (bool)isOnProperty.GetValue(toggle);
            EditorApplication.QueuePlayerLoopUpdate();

            if (storedValue != desiredValue)
            {
                return ErrorResponse.FromCode(
                    "toggle_value_rejected",
                    $"Runtime Toggle '{toggle.gameObject.name}' did not retain the requested value.",
                    new
                    {
                        requestedValue = desiredValue,
                        actualValue = storedValue,
                    });
            }

            return new SuccessResponse(
                previousValue == storedValue
                    ? "Runtime Toggle already had the requested value."
                    : "Runtime Toggle value was updated.",
                new
                {
                    target = DescribeTarget(target),
                    component = DescribeComponent(toggle),
                    previousValue,
                    value = storedValue,
                    changed = previousValue != storedValue,
                });
        }

        private static object BuildInspection(GameObject target, bool includeText)
        {
            TryFindInputField(target, out Component inputField, out _);
            TryFindToggle(target, out Component toggle, out _);
            TryFindScrollRect(target, out Component scrollRect, out _);
            Component selectable = inputField
                ?? toggle
                ?? FindAssociatedComponent(
                    target,
                    ResolveType("UnityEngine.UI.Selectable"),
                    includeChildren: false,
                    out _);
            Component textComponent = inputField ?? FindTextComponent(target, out _);

            string text = ReadStringProperty(textComponent, "text");
            bool textExists = textComponent != null && text != null;
            bool sensitiveText = inputField != null && IsSensitiveInput(inputField);
            bool textAvailable = includeText && textExists && !sensitiveText;
            bool activeInHierarchy = target.activeInHierarchy;
            bool? interactable = selectable == null
                ? (bool?)null
                : activeInHierarchy && IsInteractable(selectable);
            bool? toggleValue = ReadBoolProperty(toggle, "isOn");
            bool selectionSupported = TryGetSelectedObject(out GameObject selectedObject);
            bool selected = selectionSupported
                && selectedObject != null
                && AreInSameHierarchyBranch(target, selectedObject);

            GameObject geometryTarget = inputField?.gameObject
                ?? toggle?.gameObject
                ?? target;
            bool hasBounds = TryGetNormalizedBounds(
                geometryTarget,
                out object normalizedBounds,
                out bool overlapsGameView);
            bool visible = activeInHierarchy
                && (!hasBounds || overlapsGameView)
                && AreCanvasGroupsVisible(geometryTarget)
                && AreRectMasksVisible(geometryTarget);

            var componentKinds = new List<string>();
            AddComponentKind(componentKinds, inputField);
            AddComponentKind(componentKinds, toggle);
            AddComponentKind(componentKinds, selectable);
            AddComponentKind(componentKinds, textComponent);
            AddComponentKind(componentKinds, scrollRect);

            return new
            {
                exists = true,
                target = DescribeTarget(target),
                activeSelf = target.activeSelf,
                activeInHierarchy,
                visible,
                interactable,
                selected,
                selectionSupported,
                componentKinds,
                inputField = inputField == null ? null : DescribeComponent(inputField),
                toggle = toggle == null ? null : DescribeComponent(toggle),
                scrollRect = scrollRect == null ? null : DescribeComponent(scrollRect),
                scrollState = scrollRect == null ? null : DescribeScrollState(scrollRect),
                textComponent = textComponent == null ? null : DescribeComponent(textComponent),
                normalizedBounds = hasBounds ? normalizedBounds : null,
                textAvailable,
                textRedacted = textExists && sensitiveText,
                text = textAvailable ? text : null,
                textLength = textAvailable ? (int?)text.Length : null,
                toggleValue,
                backend = "runtime_ugui",
            };
        }

        private static object RequirePlayMode(bool allowPaused, string action)
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

        private static bool TryFindInputField(
            GameObject target,
            out Component component,
            out string error)
        {
            Type tmpInputField = ResolveType("TMPro.TMP_InputField");
            Type legacyInputField = ResolveType("UnityEngine.UI.InputField");
            component = FindAssociatedComponent(
                target,
                new[] { tmpInputField, legacyInputField },
                includeChildren: true,
                out bool ambiguous);
            if (component != null)
            {
                error = null;
                return true;
            }
            error = ambiguous
                ? $"UI target '{target.name}' contains multiple supported input fields; target one by path or instance ID."
                : $"UI target '{target.name}' has no TMP_InputField or uGUI InputField in its direct hierarchy."
                    + " UI Toolkit fields are not supported by this action.";
            return false;
        }

        private static bool TryFindToggle(
            GameObject target,
            out Component component,
            out string error)
        {
            component = FindAssociatedComponent(
                target,
                ResolveType("UnityEngine.UI.Toggle"),
                includeChildren: true,
                out bool ambiguous);
            if (component != null)
            {
                error = null;
                return true;
            }
            error = ambiguous
                ? $"UI target '{target.name}' contains multiple Toggles; target one by path or instance ID."
                : $"UI target '{target.name}' has no uGUI Toggle in its direct hierarchy.";
            return false;
        }

        private static bool TryFindScrollRect(
            GameObject target,
            out Component component,
            out string error)
        {
            component = FindAssociatedComponent(
                target,
                ResolveType("UnityEngine.UI.ScrollRect"),
                includeChildren: true,
                out bool ambiguous);
            if (component != null)
            {
                error = null;
                return true;
            }
            error = ambiguous
                ? $"UI target '{target.name}' contains multiple ScrollRects; target one by path or instance ID."
                : $"UI target '{target.name}' has no uGUI ScrollRect in its direct hierarchy.";
            return false;
        }

        private static Component FindTextComponent(GameObject target, out bool ambiguous)
        {
            return FindAssociatedComponent(
                target,
                new[]
                {
                    ResolveType("TMPro.TMP_Text"),
                    ResolveType("UnityEngine.UI.Text"),
                },
                includeChildren: true,
                out ambiguous);
        }

        private static Component FindAssociatedComponent(
            GameObject target,
            Type type,
            bool includeChildren,
            out bool ambiguous)
        {
            return FindAssociatedComponent(
                target,
                new[] { type },
                includeChildren,
                out ambiguous);
        }

        private static Component FindAssociatedComponent(
            GameObject target,
            IEnumerable<Type> candidateTypes,
            bool includeChildren,
            out bool ambiguous)
        {
            ambiguous = false;
            Type[] types = candidateTypes
                .Where(type => type != null && typeof(Component).IsAssignableFrom(type))
                .Distinct()
                .ToArray();
            if (target == null || types.Length == 0)
            {
                return null;
            }

            Component direct = FindFirstComponent(target, types);
            if (direct != null)
            {
                return direct;
            }

            Transform parent = target.transform.parent;
            while (parent != null)
            {
                Component parentComponent = FindFirstComponent(parent.gameObject, types);
                if (parentComponent != null)
                {
                    return parentComponent;
                }
                parent = parent.parent;
            }

            if (!includeChildren)
            {
                return null;
            }

            Component[] childMatches = types
                .SelectMany(type => target.GetComponentsInChildren(type, includeInactive: true))
                .Where(component => component != null)
                .GroupBy(component => component.GetInstanceIDCompat())
                .Select(group => group.First())
                .Take(2)
                .ToArray();
            if (childMatches.Length == 1)
            {
                return childMatches[0];
            }
            ambiguous = childMatches.Length > 1;
            return null;
        }

        private static Component FindFirstComponent(GameObject target, IEnumerable<Type> types)
        {
            foreach (Type type in types)
            {
                Component component = target.GetComponent(type);
                if (component != null)
                {
                    return component;
                }
            }
            return null;
        }

        private static bool IsInteractable(Component component)
        {
            if (component == null || !component.gameObject.activeInHierarchy)
            {
                return false;
            }
            if (component is Behaviour behaviour && !behaviour.isActiveAndEnabled)
            {
                return false;
            }

            MethodInfo method = component.GetType().GetMethod(
                "IsInteractable",
                InstanceMembers,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            return method?.ReturnType == typeof(bool)
                ? (bool)method.Invoke(component, null)
                : true;
        }

        private static bool IsSensitiveInput(Component inputField)
        {
            foreach (string propertyName in new[] { "contentType", "inputType" })
            {
                object value = inputField?.GetType()
                    .GetProperty(propertyName, InstanceMembers)
                    ?.GetValue(inputField);
                string name = value?.ToString();
                if (!string.IsNullOrEmpty(name)
                    && (name.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("pin", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryCreateSubmitContext(
            GameObject target,
            out SubmitContext context,
            out string error)
        {
            context = null;
            Type eventSystemType = InteractPlayMode.ResolveUnityUiType(
                "UnityEngine.EventSystems.EventSystem");
            Type baseEventDataType = InteractPlayMode.ResolveUnityUiType(
                "UnityEngine.EventSystems.BaseEventData");
            Type executeEventsType = InteractPlayMode.ResolveUnityUiType(
                "UnityEngine.EventSystems.ExecuteEvents");
            Type submitHandlerType = InteractPlayMode.ResolveUnityUiType(
                "UnityEngine.EventSystems.ISubmitHandler");
            if (eventSystemType == null
                || baseEventDataType == null
                || executeEventsType == null
                || submitHandlerType == null)
            {
                error = "Unity's uGUI submit event types are unavailable.";
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

            object eventFunction = executeEventsType.GetProperty(
                "submitHandler",
                StaticMembers)?.GetValue(null);
            MethodInfo executeHierarchy = executeEventsType.GetMethods(StaticMembers)
                .FirstOrDefault(candidate =>
                    candidate.Name == "ExecuteHierarchy"
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetGenericArguments().Length == 1
                    && candidate.GetParameters().Length == 3);
            if (eventFunction == null || executeHierarchy == null)
            {
                error = "Unity's ExecuteEvents.submitHandler API is unavailable.";
                return false;
            }

            object eventData = Activator.CreateInstance(baseEventDataType, eventSystem);
            context = new SubmitContext(
                target,
                executeHierarchy.MakeGenericMethod(submitHandlerType),
                eventData,
                eventFunction);
            error = null;
            return true;
        }

        private static bool DispatchSubmit(SubmitContext context)
        {
            return context?.ExecuteHierarchy.Invoke(
                null,
                new[] { context.Target, context.EventData, context.EventFunction }) as GameObject != null;
        }

        private static bool TryGetSelectedObject(out GameObject selectedObject)
        {
            selectedObject = null;
            Type eventSystemType = InteractPlayMode.ResolveUnityUiType(
                "UnityEngine.EventSystems.EventSystem");
            object eventSystem = eventSystemType?.GetProperty(
                "current",
                StaticMembers)?.GetValue(null);
            if (eventSystem == null)
            {
                return false;
            }
            selectedObject = eventSystemType.GetProperty(
                "currentSelectedGameObject",
                InstanceMembers)?.GetValue(eventSystem) as GameObject;
            return true;
        }

        private static bool TryGetNormalizedBounds(
            GameObject target,
            out object bounds,
            out bool overlapsGameView)
        {
            bounds = null;
            overlapsGameView = false;
            RectTransform rectTransform = target?.GetComponent<RectTransform>();
            if (rectTransform == null || Screen.width < 1 || Screen.height < 1)
            {
                return false;
            }

            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            Camera camera = ResolveEventCamera(target);
            Vector2[] screenCorners = corners
                .Select(corner => RectTransformUtility.WorldToScreenPoint(camera, corner))
                .ToArray();
            float minX = screenCorners.Min(point => point.x);
            float maxX = screenCorners.Max(point => point.x);
            float minY = screenCorners.Min(point => point.y);
            float maxY = screenCorners.Max(point => point.y);
            overlapsGameView = maxX >= 0f
                && minX <= Screen.width
                && maxY >= 0f
                && minY <= Screen.height
                && maxX > minX
                && maxY > minY;

            float width = Math.Max(1, Screen.width);
            float height = Math.Max(1, Screen.height);
            float left = minX / width;
            float right = maxX / width;
            float top = 1f - maxY / height;
            float bottom = 1f - minY / height;
            bounds = new
            {
                left,
                top,
                right,
                bottom,
                centerX = (left + right) * 0.5f,
                centerY = (top + bottom) * 0.5f,
                origin = "top_left",
            };
            return true;
        }

        private static Camera ResolveEventCamera(GameObject target)
        {
            Type canvasType = Type.GetType(
                "UnityEngine.Canvas, UnityEngine.UIModule",
                throwOnError: false);
            Component canvas = null;
            Transform current = target?.transform;
            while (current != null && canvas == null)
            {
                canvas = canvasType == null ? null : current.gameObject.GetComponent(canvasType);
                current = current.parent;
            }
            if (canvas == null)
            {
                return Camera.main;
            }

            object renderMode = canvasType.GetProperty(
                "renderMode",
                InstanceMembers)?.GetValue(canvas);
            if (renderMode != null && Convert.ToInt32(renderMode) == 0)
            {
                return null;
            }
            return canvasType.GetProperty(
                "worldCamera",
                InstanceMembers)?.GetValue(canvas) as Camera ?? Camera.main;
        }

        private static bool AreRectMasksVisible(GameObject target)
        {
            RectTransform targetRectTransform = target?.GetComponent<RectTransform>();
            if (targetRectTransform == null
                || !TryGetScreenRect(targetRectTransform, ResolveEventCamera(target), out Rect targetRect))
            {
                return true;
            }

            Type rectMaskType = ResolveType("UnityEngine.UI.RectMask2D");
            Transform current = target.transform.parent;
            while (current != null)
            {
                Component mask = rectMaskType == null
                    ? null
                    : current.gameObject.GetComponent(rectMaskType);
                if (mask is Behaviour behaviour
                    && behaviour.isActiveAndEnabled
                    && current.TryGetComponent(out RectTransform maskRectTransform)
                    && TryGetScreenRect(
                        maskRectTransform,
                        ResolveEventCamera(current.gameObject),
                        out Rect maskRect)
                    && (targetRect.xMax <= maskRect.xMin
                        || targetRect.xMin >= maskRect.xMax
                        || targetRect.yMax <= maskRect.yMin
                        || targetRect.yMin >= maskRect.yMax))
                {
                    return false;
                }
                current = current.parent;
            }
            return true;
        }

        private static bool TryGetScreenRect(
            RectTransform rectTransform,
            Camera camera,
            out Rect screenRect)
        {
            screenRect = default;
            if (rectTransform == null)
            {
                return false;
            }

            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            Vector2 first = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            float minX = first.x;
            float maxX = first.x;
            float minY = first.y;
            float maxY = first.y;
            for (int index = 1; index < corners.Length; index++)
            {
                Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, corners[index]);
                minX = Math.Min(minX, point.x);
                maxX = Math.Max(maxX, point.x);
                minY = Math.Min(minY, point.y);
                maxY = Math.Max(maxY, point.y);
            }

            if (maxX <= minX || maxY <= minY)
            {
                return false;
            }
            screenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        private static bool AreCanvasGroupsVisible(GameObject target)
        {
            Transform current = target?.transform;
            while (current != null)
            {
                foreach (CanvasGroup group in current.GetComponents<CanvasGroup>())
                {
                    if (!group.enabled)
                    {
                        continue;
                    }
                    if (group.alpha <= 0.001f)
                    {
                        return false;
                    }
                    if (group.ignoreParentGroups)
                    {
                        return true;
                    }
                }
                current = current.parent;
            }
            return true;
        }

        private static Type ResolveType(string fullName)
        {
            Type uiType = InteractPlayMode.ResolveUnityUiType(fullName);
            if (uiType != null)
            {
                return uiType;
            }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        private static bool? ReadBoolProperty(Component component, string name)
        {
            PropertyInfo property = component?.GetType().GetProperty(name, InstanceMembers);
            return property?.CanRead == true && property.PropertyType == typeof(bool)
                ? (bool?)property.GetValue(component)
                : null;
        }

        private static string ReadStringProperty(Component component, string name)
        {
            PropertyInfo property = component?.GetType().GetProperty(name, InstanceMembers);
            return property?.CanRead == true && property.PropertyType == typeof(string)
                ? property.GetValue(component) as string
                : null;
        }

        private static bool TryReadRequiredBool(JToken token, out bool value)
        {
            value = false;
            if (token?.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>();
                return true;
            }
            return token?.Type == JTokenType.String
                && bool.TryParse(token.Value<string>(), out value);
        }

        private static bool AreInSameHierarchyBranch(GameObject first, GameObject second)
        {
            return first == second
                || second.transform.IsChildOf(first.transform)
                || first.transform.IsChildOf(second.transform);
        }

        private static object DescribeScrollState(Component scrollRect)
        {
            Vector2 normalizedPosition = ReadProperty(
                scrollRect,
                "normalizedPosition",
                Vector2.zero);
            Vector2 velocity = ReadProperty(
                scrollRect,
                "velocity",
                Vector2.zero);
            return new
            {
                horizontal = ReadProperty(scrollRect, "horizontal", false),
                vertical = ReadProperty(scrollRect, "vertical", false),
                horizontalNormalizedPosition = normalizedPosition.x,
                verticalNormalizedPosition = normalizedPosition.y,
                velocity = new
                {
                    x = velocity.x,
                    y = velocity.y,
                },
            };
        }

        private static T ReadProperty<T>(
            Component component,
            string propertyName,
            T defaultValue)
        {
            PropertyInfo property = component?.GetType().GetProperty(
                propertyName,
                InstanceMembers);
            return property?.CanRead == true && property.PropertyType == typeof(T)
                ? (T)property.GetValue(component)
                : defaultValue;
        }

        private static string NormalizeSearchMethod(string target, string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested))
            {
                return requested.ToLowerInvariant();
            }
            return int.TryParse(target, out _)
                ? "by_id"
                : target?.Contains("/") == true
                    ? "by_path"
                    : "by_name";
        }

        private static object DescribeTarget(GameObject target)
        {
            return new
            {
                name = target.name,
                path = GameObjectLookup.GetGameObjectPath(target),
                instanceId = target.GetInstanceIDCompat(),
                scene = target.scene.IsValid() ? target.scene.path : null,
            };
        }

        private static object DescribeComponent(Component component)
        {
            return new
            {
                type = component.GetType().FullName,
                owner = new
                {
                    name = component.gameObject.name,
                    path = GameObjectLookup.GetGameObjectPath(component.gameObject),
                    instanceId = component.gameObject.GetInstanceIDCompat(),
                },
            };
        }

        private static void AddComponentKind(List<string> kinds, Component component)
        {
            if (component == null)
            {
                return;
            }
            string fullName = component.GetType().FullName ?? string.Empty;
            string kind = fullName switch
            {
                "TMPro.TMP_InputField" => "tmp_input_field",
                "UnityEngine.UI.InputField" => "input_field",
                "UnityEngine.UI.Toggle" => "toggle",
                "UnityEngine.UI.ScrollRect" => "scroll_rect",
                "UnityEngine.UI.Text" => "text",
                _ when IsTypeOrSubclass(component.GetType(), "TMPro.TMP_Text") => "tmp_text",
                _ when IsTypeOrSubclass(component.GetType(), "UnityEngine.UI.Selectable") => "selectable",
                _ => fullName,
            };
            if (!string.IsNullOrEmpty(kind) && !kinds.Contains(kind))
            {
                kinds.Add(kind);
            }
        }

        private static bool IsTypeOrSubclass(Type type, string baseTypeName)
        {
            while (type != null)
            {
                if (string.Equals(type.FullName, baseTypeName, StringComparison.Ordinal))
                {
                    return true;
                }
                type = type.BaseType;
            }
            return false;
        }

        private sealed class SubmitContext
        {
            internal SubmitContext(
                GameObject target,
                MethodInfo executeHierarchy,
                object eventData,
                object eventFunction)
            {
                Target = target;
                ExecuteHierarchy = executeHierarchy;
                EventData = eventData;
                EventFunction = eventFunction;
            }

            internal GameObject Target { get; }
            internal MethodInfo ExecuteHierarchy { get; }
            internal object EventData { get; }
            internal object EventFunction { get; }
        }
    }
}
