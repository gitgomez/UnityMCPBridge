using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Tools;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class ReadConsoleTests
    {
        [Test]
        public void HandleCommand_Clear_Works()
        {
            // Arrange
            // Ensure there's something to clear
            Debug.Log("Log to clear");
            
            // Verify content exists before clear
            var getBefore = ToJObject(ReadConsole.HandleCommand(new JObject { ["action"] = "get", ["types"] = new JArray { "error", "warning", "log" }, ["count"] = 10 }));
            Assert.IsTrue(getBefore.Value<bool>("success"), getBefore.ToString());
            var entriesBefore = getBefore["data"] as JArray;
            
            // Ideally we'd assert count > 0, but other tests/system logs might affect this.
            // Just ensuring the call doesn't fail is a baseline, but let's try to be stricter if possible.
            // Since we just logged, there should be at least one entry.
            Assert.IsTrue(entriesBefore != null && entriesBefore.Count > 0, "Setup failed: console should have logs.");

            // Act
            var result = ToJObject(ReadConsole.HandleCommand(new JObject { ["action"] = "clear" }));

            // Assert
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            
            // Verify clear effect
            var getAfter = ToJObject(ReadConsole.HandleCommand(new JObject { ["action"] = "get", ["types"] = new JArray { "error", "warning", "log" }, ["count"] = 10 }));
            Assert.IsTrue(getAfter.Value<bool>("success"), getAfter.ToString());
            var entriesAfter = getAfter["data"] as JArray;
            Assert.IsTrue(entriesAfter == null || entriesAfter.Count == 0, "Console should be empty after clear.");
        }

        [Test]
        public void HandleCommand_Get_Works()
        {
            // Arrange
            string uniqueMessage = $"Test Log Message {Guid.NewGuid()}";
            Debug.Log(uniqueMessage);
            
            var paramsObj = new JObject
            {
                ["action"] = "get",
                ["types"] = new JArray { "error", "warning", "log" },
                ["format"] = "detailed",
                ["count"] = 1000 // Fetch enough to likely catch our message
            };

            // Act
            var result = ToJObject(ReadConsole.HandleCommand(paramsObj));

            // Assert
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var data = result["data"] as JArray;
            Assert.IsNotNull(data, "Data array should not be null.");
            Assert.IsTrue(data.Count > 0, "Should retrieve at least one log entry.");

            // Verify content
            bool found = false;
            foreach (var entry in data)
            {
                if (entry["message"]?.ToString().Contains(uniqueMessage) == true)
                {
                    found = true;
                    break;
                }
            }
            Assert.IsTrue(found, $"The unique log message '{uniqueMessage}' was not found in retrieved logs.");
        }

        [Test]
        public void HandleCommand_Get_PreservesMultilineMessageBody()
        {
            string id = Guid.NewGuid().ToString();
            string firstLine = $"First line {id}";
            string secondLine = $"Second line {id}";
            Debug.Log($"{firstLine}\n\n{secondLine}");

            var paramsObj = new JObject
            {
                ["action"] = "get",
                ["types"] = new JArray { "error", "warning", "log" },
                ["format"] = "detailed",
                ["count"] = 1000
            };

            var result = ToJObject(ReadConsole.HandleCommand(paramsObj));
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var data = result["data"] as JArray;
            Assert.IsNotNull(data, "Data array should not be null.");

            string message = null;
            foreach (var entry in data)
            {
                string candidate = entry["message"]?.ToString();
                if (candidate != null && candidate.Contains(firstLine))
                {
                    message = candidate;
                    break;
                }
            }

            Assert.IsNotNull(message, "Multi-line log entry was not found.");
            StringAssert.Contains($"{firstLine}\n\n{secondLine}", message);
            StringAssert.DoesNotContain("UnityEngine.Debug", message);
        }

        [Test]
        public void HandleCommand_Get_DoesNotInferSeverityFromStackTraceWords()
        {
            string id = Guid.NewGuid().ToString();
            string message = $"Informational disconnect {id}";
            Debug.Log(
                $"{message}\nSystem.Threading.Tasks.TaskCompletionSource:SetException (System.Exception)");

            JObject errors = ToJObject(ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get",
                ["types"] = new JArray { "error" },
                ["format"] = "detailed",
                ["count"] = 1000,
                ["filterText"] = id,
                ["includeStacktrace"] = true,
            }));
            JObject logs = ToJObject(ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get",
                ["types"] = new JArray { "log" },
                ["format"] = "detailed",
                ["count"] = 1000,
                ["filterText"] = id,
                ["includeStacktrace"] = true,
            }));

            Assert.IsTrue(errors.Value<bool>("success"), errors.ToString());
            Assert.IsEmpty((JArray)errors["data"], errors.ToString());
            Assert.IsTrue(logs.Value<bool>("success"), logs.ToString());
            var entries = (JArray)logs["data"];
            Assert.AreEqual(1, entries.Count, logs.ToString());
            Assert.AreEqual("Log", entries[0].Value<string>("type"));
            Assert.AreEqual(message, entries[0].Value<string>("message"));
            StringAssert.Contains(
                "SetException",
                entries[0].Value<string>("stackTrace"));
        }

        [TestCase("DebugLog", LogType.Log)]
        [TestCase("DebugWarning", LogType.Warning)]
        [TestCase("DebugError", LogType.Error)]
        [TestCase("DebugException", LogType.Exception)]
        [TestCase("DebugAssert", LogType.Assert)]
        [TestCase("kScriptCompileError", LogType.Error)]
        [TestCase("kScriptCompileWarning", LogType.Warning)]
        public void ClassifyLogType_UsesCurrentUnityModeBeforeMessageBody(
            string flagName,
            LogType expected)
        {
            Type flagsType = typeof(EditorApplication).Assembly.GetType(
                "UnityEditor.LogMessageFlags"
            );
            Assert.IsNotNull(flagsType, "UnityEditor.LogMessageFlags was not found.");
            int mode = Convert.ToInt32(Enum.Parse(flagsType, flagName));

            Assert.AreEqual(
                expected,
                ReadConsole.ClassifyLogType(
                    mode,
                    "Informational Exception error warning Assertion report"
                )
            );
        }

        [TestCase("Client handler exited", LogType.Log)]
        [TestCase("System.InvalidOperationException: failed", LogType.Exception)]
        [TestCase("Assertion failed", LogType.Assert)]
        [TestCase("Assets/Test.cs(1,1): error CS1002: ; expected", LogType.Error)]
        [TestCase("Assets/Test.cs(1,1): warning CS0168: unused", LogType.Warning)]
        public void ClassifyLogType_FallsBackToMessageBodyWithoutKnownMode(
            string messageBody,
            LogType expected)
        {
            Assert.AreEqual(expected, ReadConsole.ClassifyLogType(0, messageBody));
        }
    }
}
