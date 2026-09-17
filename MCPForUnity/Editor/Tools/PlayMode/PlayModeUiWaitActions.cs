using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Tools.PlayMode
{
    /// <summary>
    /// Performs bounded frame-driven UI inspection inside one Unity command so a
    /// logical wait consumes one durable runtime receipt rather than one per poll.
    /// </summary>
    internal static class PlayModeUiWaitActions
    {
        private const double DefaultTimeoutSeconds = 5d;
        private const double MinTimeoutSeconds = 0.1d;
        private const double MaxTimeoutSeconds = 30d;
        private const double DefaultPollIntervalSeconds = 0.1d;
        private const double MinPollIntervalSeconds = 0.05d;
        private const double MaxPollIntervalSeconds = 1d;

        private static readonly IReadOnlyDictionary<string, string> BooleanFields =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["exists"] = "exists",
                ["active"] = "activeInHierarchy",
                ["visible"] = "visible",
                ["interactable"] = "interactable",
                ["selected"] = "selected",
                ["hovered"] = "hovered",
                ["toggle_equals"] = "toggleValue",
            };

        internal static Task<object> WaitAsync(JObject parameters)
        {
            if (parameters == null)
            {
                return Task.FromResult<object>(
                    new ErrorResponse("Parameters cannot be null."));
            }

            string condition = parameters.Value<string>("condition")
                ?.ToLowerInvariant();
            if (condition == "hovered"
                && parameters.Value<string>("ui_system") != "ui_toolkit")
                return Task.FromResult<object>(ErrorResponse.FromCode(
                    "ui_toolkit_required", "The hovered condition requires ui_system='ui_toolkit'."));
            if (string.IsNullOrWhiteSpace(condition)
                || !BooleanFields.ContainsKey(condition)
                    && condition != "text_equals"
                    && condition != "text_contains")
            {
                return Task.FromResult<object>(ErrorResponse.FromCode(
                    "invalid_wait_condition",
                    "condition is required for wait_ui and must be exists, active, visible, interactable, selected, hovered, text_equals, text_contains, or toggle_equals."));
            }

            if (!TryResolveExpected(
                    parameters["expected"],
                    condition,
                    out JToken expected,
                    out string expectedError))
            {
                return Task.FromResult<object>(ErrorResponse.FromCode(
                    "invalid_wait_expected",
                    expectedError));
            }

            if (!TryReadBoundedNumber(
                    parameters["timeout_seconds"],
                    DefaultTimeoutSeconds,
                    MinTimeoutSeconds,
                    MaxTimeoutSeconds,
                    out double timeoutSeconds))
            {
                return Task.FromResult<object>(ErrorResponse.FromCode(
                    "invalid_wait_timeout",
                    $"timeout_seconds must be between {FormatNumber(MinTimeoutSeconds)} and {FormatNumber(MaxTimeoutSeconds)}."));
            }

            if (!TryReadBoundedNumber(
                    parameters["poll_interval_seconds"],
                    DefaultPollIntervalSeconds,
                    MinPollIntervalSeconds,
                    MaxPollIntervalSeconds,
                    out double pollIntervalSeconds))
            {
                return Task.FromResult<object>(ErrorResponse.FromCode(
                    "invalid_poll_interval",
                    $"poll_interval_seconds must be between {FormatNumber(MinPollIntervalSeconds)} and {FormatNumber(MaxPollIntervalSeconds)}."));
            }

            var inspectParameters = (JObject)parameters.DeepClone();
            inspectParameters["action"] = "inspect_ui";
            inspectParameters["include_text"] =
                (parameters.Value<bool?>("include_text") ?? true)
                || condition == "text_equals"
                || condition == "text_contains";

            return PollAsync(
                inspectParameters,
                condition,
                expected,
                timeoutSeconds,
                pollIntervalSeconds);
        }

        private static Task<object> PollAsync(
            JObject inspectParameters,
            string condition,
            JToken expected,
            double timeoutSeconds,
            double pollIntervalSeconds)
        {
            var completion = new TaskCompletionSource<object>();
            double started = EditorApplication.timeSinceStartup;
            double nextPollAt = started;
            int attempts = 0;
            EditorApplication.CallbackFunction tick = null;

            void Complete(object result)
            {
                EditorApplication.update -= tick;
                completion.TrySetResult(result);
            }

            tick = () =>
            {
                double now = EditorApplication.timeSinceStartup;
                if (now < nextPollAt)
                {
                    return;
                }

                try
                {
                    attempts++;
                    object inspection = InteractPlayMode.HandleCommand(
                        inspectParameters);
                    JObject inspectionJson = JObject.FromObject(inspection);
                    if (inspectionJson.Value<bool?>("success") != true)
                    {
                        Complete(inspection);
                        return;
                    }

                    if (!(inspectionJson["data"] is JObject state))
                    {
                        Complete(ErrorResponse.FromCode(
                            "invalid_ui_state",
                            "inspect_ui returned no structured UI state.",
                            new { attempts }));
                        return;
                    }

                    bool satisfied = EvaluateCondition(
                        state,
                        condition,
                        expected,
                        out string unavailable);
                    double elapsed = now - started;
                    if (unavailable != null)
                    {
                        JObject unavailableData = BuildWaitData(
                            condition,
                            expected,
                            attempts,
                            elapsed);
                        unavailableData["state"] = state.DeepClone();
                        Complete(ErrorResponse.FromCode(
                            "ui_condition_unavailable",
                            unavailable,
                            unavailableData));
                        return;
                    }

                    if (satisfied)
                    {
                        JObject successData = BuildWaitData(
                            condition,
                            expected,
                            attempts,
                            elapsed);
                        successData["state"] = state.DeepClone();
                        Complete(new SuccessResponse(
                            $"Runtime UI condition '{condition}' is satisfied.",
                            successData));
                        return;
                    }

                    if (elapsed >= timeoutSeconds)
                    {
                        JObject timeoutData = BuildWaitData(
                            condition,
                            expected,
                            attempts,
                            elapsed);
                        timeoutData["lastState"] = state.DeepClone();
                        timeoutData["lastResult"] = inspectionJson;
                        Complete(ErrorResponse.FromCode(
                            "wait_ui_timeout",
                            $"Runtime UI condition '{condition}' was not satisfied within {FormatNumber(timeoutSeconds)} seconds.",
                            timeoutData));
                        return;
                    }

                    nextPollAt = now + Math.Min(
                        pollIntervalSeconds,
                        timeoutSeconds - elapsed);
                }
                catch (Exception ex)
                {
                    EditorApplication.update -= tick;
                    completion.TrySetException(ex);
                }
            };

            EditorApplication.update += tick;
            tick();
            return completion.Task;
        }

        internal static bool EvaluateCondition(
            JObject state,
            string condition,
            JToken expected,
            out string unavailable)
        {
            unavailable = null;
            if (BooleanFields.TryGetValue(condition, out string field))
            {
                JToken actualValue = state[field];
                if ((actualValue == null || actualValue.Type == JTokenType.Null)
                    && condition != "exists")
                {
                    unavailable = $"inspect_ui did not expose '{field}' for this target.";
                    return false;
                }

                return actualValue?.Type == JTokenType.Boolean
                    && actualValue.Value<bool>() == expected.Value<bool>();
            }

            if (state.Value<bool?>("textAvailable") != true)
            {
                unavailable = state.Value<bool?>("textRedacted") == true
                    ? "Text comparison is unavailable because the target text is sensitive."
                    : "Text comparison is unavailable because the target exposes no unambiguous text.";
                return false;
            }

            JToken actualText = state["text"];
            if (actualText?.Type != JTokenType.String)
            {
                unavailable = "inspect_ui did not return a string text value.";
                return false;
            }

            string actual = actualText.Value<string>();
            string requested = expected.Value<string>();
            return condition == "text_equals"
                ? string.Equals(actual, requested, StringComparison.Ordinal)
                : actual?.Contains(requested) == true;
        }

        private static JObject BuildWaitData(
            string condition,
            JToken expected,
            int attempts,
            double elapsedSeconds)
        {
            var data = new JObject
            {
                ["condition"] = condition,
                ["attempts"] = attempts,
                ["elapsedSeconds"] = Math.Round(elapsedSeconds, 3),
            };
            if (condition == "text_equals" || condition == "text_contains")
            {
                data["expectedRedacted"] = true;
            }
            else
            {
                data["expected"] = expected.DeepClone();
            }
            return data;
        }

        private static bool TryResolveExpected(
            JToken token,
            string condition,
            out JToken expected,
            out string error)
        {
            bool textCondition = condition == "text_equals"
                || condition == "text_contains";
            if (textCondition)
            {
                if (token?.Type != JTokenType.String)
                {
                    expected = null;
                    error = $"expected must be a string for {condition}.";
                    return false;
                }

                expected = token.DeepClone();
                error = null;
                return true;
            }

            if (token == null || token.Type == JTokenType.Null)
            {
                expected = new JValue(true);
                error = null;
                return true;
            }
            if (token.Type == JTokenType.Boolean)
            {
                expected = token.DeepClone();
                error = null;
                return true;
            }
            if (token.Type == JTokenType.String
                && bool.TryParse(token.Value<string>(), out bool parsed))
            {
                expected = new JValue(parsed);
                error = null;
                return true;
            }

            expected = null;
            error = $"expected must be true or false for {condition}.";
            return false;
        }

        private static bool TryReadBoundedNumber(
            JToken token,
            double defaultValue,
            double minimum,
            double maximum,
            out double value)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                value = defaultValue;
                return true;
            }

            value = 0d;
            bool parsed;
            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    try
                    {
                        value = Convert.ToDouble(
                            ((JValue)token).Value,
                            CultureInfo.InvariantCulture);
                        parsed = true;
                    }
                    catch (FormatException)
                    {
                        parsed = false;
                    }
                    catch (InvalidCastException)
                    {
                        parsed = false;
                    }
                    catch (OverflowException)
                    {
                        parsed = false;
                    }
                    break;
                case JTokenType.String:
                    parsed = double.TryParse(
                        token.Value<string>(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out value);
                    break;
                default:
                    parsed = false;
                    break;
            }
            return parsed
                && !double.IsNaN(value)
                && !double.IsInfinity(value)
                && value >= minimum
                && value <= maximum;
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("G", CultureInfo.InvariantCulture);
        }
    }
}
