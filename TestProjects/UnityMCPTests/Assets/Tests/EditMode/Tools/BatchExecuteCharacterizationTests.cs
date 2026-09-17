using System;
using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MCPForUnityTests.Editor.Tools
{
    [TestFixture]
    public class BatchExecuteCharacterizationTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            CommandRegistry.Initialize();
        }

        [Test]
        public void ConfiguredLimit_IsEnforcedBeforeAnyCommandRuns()
        {
            int currentLimit = BatchExecute.GetMaxCommandsPerBatch();
            var commands = new JArray(
                Enumerable.Range(0, currentLimit + 1)
                    .Select(_ => new JObject { ["tool"] = "missing_tool" }));

            JObject response = Execute(new JObject { ["commands"] = commands });

            Assert.IsFalse(response.Value<bool>("success"));
            StringAssert.Contains(
                $"maximum of {currentLimit} commands",
                response.Value<string>("error"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FullPreflight_ReportsEveryInvalidEntryBeforeExecution(bool failFast)
        {
            string objectName = "BatchPreflight_" + Guid.NewGuid().ToString("N");
            try
            {
                var request = new JObject
                {
                    ["failFast"] = failFast,
                    ["commands"] = new JArray(
                        CreateGameObjectCommand(objectName),
                        42,
                        new JObject { ["tool"] = "missing_tool" })
                };

                JObject response = Execute(request);
                JObject data = (JObject)response["data"];

                Assert.IsFalse(response.Value<bool>("success"));
                Assert.AreEqual("BATCH_PREFLIGHT_FAILED", response.Value<string>("code"));
                Assert.AreEqual(2, ((JArray)data["errors"]).Count);
                Assert.AreEqual(0, data.Value<int>("started_count"));
                Assert.IsNull(GameObject.Find(objectName));
            }
            finally
            {
                DestroyNamed(objectName);
            }
        }

        [Test]
        public void ParallelRequest_IsReportedButStillAppliedSequentially()
        {
            var request = new JObject
            {
                ["parallel"] = true,
                ["maxParallelism"] = 8,
                ["dryRun"] = true,
                ["commands"] = new JArray(
                    new JObject
                    {
                        ["tool"] = "find_gameobjects",
                        ["params"] = new JObject()
                    })
            };

            JObject data = (JObject)Execute(request)["data"];

            Assert.IsTrue(data.Value<bool>("parallelRequested"));
            Assert.IsFalse(data.Value<bool>("parallelApplied"));
            Assert.AreEqual(8, data.Value<int>("maxParallelism"));
        }

        [Test]
        public void DryRun_PreflightsWithoutMutatingScene()
        {
            string objectName = "BatchDryRun_" + Guid.NewGuid().ToString("N");
            try
            {
                var request = new JObject
                {
                    ["dryRun"] = true,
                    ["commands"] = new JArray(CreateGameObjectCommand(objectName))
                };

                JObject response = Execute(request);
                JObject data = (JObject)response["data"];

                Assert.IsTrue(response.Value<bool>("success"), response.ToString());
                Assert.IsNull(GameObject.Find(objectName));
                Assert.AreEqual(1, data.Value<int>("validated_count"));
                Assert.AreEqual(0, data.Value<int>("started_count"));
                Assert.AreEqual("validated", data["results"]?[0]?.Value<string>("status"));
            }
            finally
            {
                DestroyNamed(objectName);
            }
        }

        [Test]
        public void UndoGroup_RollsBackEarlierSuccessfulMutationAfterFailure()
        {
            string objectName = "BatchRollback_" + Guid.NewGuid().ToString("N");
            try
            {
                var request = new JObject
                {
                    ["atomicity"] = "undo_group",
                    ["rollbackOnFailure"] = true,
                    ["commands"] = new JArray(
                        CreateGameObjectCommand(objectName),
                        new JObject
                        {
                            ["tool"] = "manage_gameobject",
                            ["params"] = new JObject { ["action"] = "modify" }
                        })
                };

                JObject response = Execute(request);
                JObject data = (JObject)response["data"];

                Assert.IsFalse(response.Value<bool>("success"));
                Assert.AreEqual("BATCH_EXECUTION_FAILED", response.Value<string>("code"));
                Assert.IsTrue(data.Value<bool>("rollback_applied"));
                Assert.AreEqual(1, data.Value<int>("reverted_count"));
                Assert.IsNull(GameObject.Find(objectName));
            }
            finally
            {
                DestroyNamed(objectName);
            }
        }

        [Test]
        public void NonUndoableBatch_IsRejectedBeforeFirstCommand()
        {
            var request = new JObject
            {
                ["dryRun"] = true,
                ["atomicity"] = "undo_group",
                ["commands"] = new JArray(
                    new JObject
                    {
                        ["tool"] = "manage_asset",
                        ["params"] = new JObject()
                    })
            };

            JObject response = Execute(request);

            Assert.IsFalse(response.Value<bool>("success"));
            Assert.AreEqual("BATCH_ROLLBACK_UNSUPPORTED", response.Value<string>("code"));
            Assert.AreEqual(0, response["data"]?.Value<int>("started_count"));
        }

        [Test]
        public void FailFast_ReportsLaterCommandsAsSkipped()
        {
            string objectName = "BatchSkipped_" + Guid.NewGuid().ToString("N");
            try
            {
                var request = new JObject
                {
                    ["failFast"] = true,
                    ["commands"] = new JArray(
                        new JObject
                        {
                            ["tool"] = "manage_gameobject",
                            ["params"] = new JObject { ["action"] = "modify" }
                        },
                        CreateGameObjectCommand(objectName))
                };

                JObject response = Execute(request);
                JObject data = (JObject)response["data"];

                Assert.AreEqual(1, data.Value<int>("started_count"));
                Assert.AreEqual(1, data.Value<int>("skipped_count"));
                Assert.AreEqual("skipped", data["results"]?[1]?.Value<string>("status"));
                Assert.IsNull(GameObject.Find(objectName));
            }
            finally
            {
                DestroyNamed(objectName);
            }
        }

        private static JObject Execute(JObject request)
        {
            object result = BatchExecute.HandleCommand(request).GetAwaiter().GetResult();
            return JObject.FromObject(result);
        }

        private static JObject CreateGameObjectCommand(string objectName)
        {
            return new JObject
            {
                ["tool"] = "manage_gameobject",
                ["params"] = new JObject
                {
                    ["action"] = "create",
                    ["name"] = objectName
                }
            };
        }

        private static void DestroyNamed(string objectName)
        {
            GameObject existing = GameObject.Find(objectName);
            if (existing != null)
            {
                UnityEngine.Object.DestroyImmediate(existing);
            }
        }
    }
}
