using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.EditMode.Tools
{
    public class ManageEditorPlayModeTests
    {
        [TearDown]
        public void TearDown()
        {
            ManageEditor.ResetPlayModeHooksForTests();
            PlannedServerRestartCoordinator.ResetForTests();
        }

        [Test]
        public void Play_DefersTransitionUntilAfterCommandResponse()
        {
            bool? requestedState = null;
            ManageEditor.IsPlayingProvider = () => false;
            ManageEditor.PlayModeTransitionScheduler = value => requestedState = value;

            JObject response = JObject.FromObject(ManageEditor.HandleCommand(
                new JObject { ["action"] = "play" }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response.Value<string>("message"), Is.EqualTo("Play mode entry requested."));
            Assert.That(response["data"]?.Value<bool>("deferred"), Is.True);
            Assert.That(requestedState, Is.True);
        }

        [Test]
        public void Stop_DefersTransitionUntilAfterCommandResponse()
        {
            bool? requestedState = null;
            ManageEditor.IsPlayingProvider = () => true;
            ManageEditor.PlayModeTransitionScheduler = value => requestedState = value;

            JObject response = JObject.FromObject(ManageEditor.HandleCommand(
                new JObject { ["action"] = "stop" }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response.Value<string>("message"), Is.EqualTo("Play mode exit requested."));
            Assert.That(response["data"]?.Value<bool>("deferred"), Is.True);
            Assert.That(requestedState, Is.False);
        }

        [Test]
        public void Play_WhenAlreadyPlaying_DoesNotScheduleTransition()
        {
            bool scheduled = false;
            ManageEditor.IsPlayingProvider = () => true;
            ManageEditor.PlayModeTransitionScheduler = _ => scheduled = true;

            JObject response = JObject.FromObject(ManageEditor.HandleCommand(
                new JObject { ["action"] = "play" }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response.Value<string>("message"), Is.EqualTo("Already in play mode."));
            Assert.That(scheduled, Is.False);
        }

        [Test]
        public void RestartMcpServer_ReturnsDeferredReceiptBeforeSchedulingRestart()
        {
            bool scheduled = false;
            ManageEditor.ServerRestartScheduler = () =>
            {
                scheduled = true;
                return null;
            };

            JObject response = JObject.FromObject(ManageEditor.HandleCommand(
                new JObject { ["action"] = "restart_mcp_server" }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response.Value<string>("message"), Is.EqualTo("MCP server restart scheduled."));
            Assert.That(response["data"]?.Value<bool>("deferred"), Is.True);
            Assert.That(response["data"]?.Value<bool>("disconnect_expected"), Is.True);
            Assert.That(response["data"]?.Value<bool>("reconnect_automatic"), Is.True);
            Assert.That(
                response["data"]?.Value<int>("response_grace_ms"),
                Is.EqualTo(PlannedServerRestartCoordinator.ResponseGraceMilliseconds));
            Assert.That(scheduled, Is.True);
        }

        [Test]
        public void RestartMcpServer_ReportsPreflightFailure()
        {
            ManageEditor.ServerRestartScheduler = () => "managed server unavailable";

            JObject response = JObject.FromObject(ManageEditor.HandleCommand(
                new JObject { ["action"] = "restart_mcp_server" }));

            Assert.That(response.Value<bool>("success"), Is.False);
            StringAssert.Contains("managed server unavailable", response.Value<string>("error"));
        }
    }

    public class PlannedServerRestartCoordinatorTests
    {
        [SetUp]
        public void SetUp()
        {
            PlannedServerRestartCoordinator.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            PlannedServerRestartCoordinator.ResetForTests();
        }

        [Test]
        public void Schedule_WaitsForResponseGraceThenStopsAndStarts()
        {
            DateTime now = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
            int stopCount = 0;
            int startCount = 0;
            bool reachable = true;
            bool launchLogAvailable = false;
            PlannedServerRestartCoordinator.UtcNowProvider = () => now;
            PlannedServerRestartCoordinator.CanScheduleProvider = () => true;
            PlannedServerRestartCoordinator.StopProvider = () =>
            {
                stopCount++;
                reachable = false;
                return true;
            };
            PlannedServerRestartCoordinator.IsReachableProvider = () => reachable;
            PlannedServerRestartCoordinator.PreviousLaunchProcessAliveProvider = () => false;
            PlannedServerRestartCoordinator.LaunchLogAvailableProvider = () => launchLogAvailable;
            PlannedServerRestartCoordinator.StartProvider = () =>
            {
                startCount++;
                return true;
            };

            Assert.That(PlannedServerRestartCoordinator.Schedule(), Is.Null);
            PlannedServerRestartCoordinator.TickForTests();
            Assert.That(stopCount, Is.Zero);
            Assert.That(startCount, Is.Zero);

            now = now.AddMilliseconds(
                PlannedServerRestartCoordinator.ResponseGraceMilliseconds - 1);
            PlannedServerRestartCoordinator.TickForTests();
            Assert.That(stopCount, Is.Zero);

            now = now.AddMilliseconds(1);
            PlannedServerRestartCoordinator.TickForTests();
            Assert.That(stopCount, Is.EqualTo(1));
            Assert.That(startCount, Is.Zero, "A locked prior launch log must delay replacement startup.");

            launchLogAvailable = true;
            PlannedServerRestartCoordinator.TickForTests();
            Assert.That(startCount, Is.EqualTo(1));
            Assert.That(PlannedServerRestartState.IsActive, Is.True);

            reachable = true;
            PlannedServerRestartCoordinator.TickForTests();
        }

        [Test]
        public void Schedule_RejectsWhenManagedLocalServerIsUnavailable()
        {
            PlannedServerRestartCoordinator.CanScheduleProvider = () => false;

            string error = PlannedServerRestartCoordinator.Schedule();

            StringAssert.Contains("Unity-managed local MCP server", error);
            Assert.That(PlannedServerRestartState.IsActive, Is.False);
        }
    }
}
