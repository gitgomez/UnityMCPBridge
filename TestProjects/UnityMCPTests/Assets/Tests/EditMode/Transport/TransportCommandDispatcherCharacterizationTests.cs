using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Transport
{
    [TestFixture]
    public class TransportCommandDispatcherCharacterizationTests
    {
        private const int CharacterizationCommandCount = 80;

        private static readonly Type DispatcherType = typeof(TransportCommandDispatcher);
        private static readonly FieldInfo MainThreadContextField = GetRequiredField("_mainThreadContext");
        private static readonly FieldInfo PendingField = GetRequiredField("Pending");
        private static readonly FieldInfo RuntimeQueuedPayloadBytesField = GetRequiredField("runtimeQueuedPayloadBytes");
        private static readonly MethodInfo ProcessQueueMethod = GetRequiredMethod("ProcessQueue");

        private SynchronizationContext originalMainThreadContext;

        [SetUp]
        public void SetUp()
        {
            Assert.AreEqual(0, GetPendingCount(), "Dispatcher must be idle before each characterization test.");
            originalMainThreadContext = (SynchronizationContext)MainThreadContextField.GetValue(null);
            MainThreadContextField.SetValue(null, new CapturingSynchronizationContext());
        }

        [TearDown]
        public void TearDown()
        {
            MainThreadContextField.SetValue(null, originalMainThreadContext);
            ProcessQueueMethod.Invoke(null, null);
            Assert.AreEqual(0, GetPendingCount(), "Dispatcher must not leak pending commands between tests.");
        }

        [Test]
        public void CurrentDispatcher_AcceptsMoreThan64Commands_AndDrainsAllInOnePump()
        {
            // Command Runtime v1 proposes a limit of 64 and a per-frame budget. This
            // freezes the current behavior so the later change is explicit and reviewed.
            List<Task<string>> tasks = QueuePingCommandsFromBackgroundThread(CharacterizationCommandCount);

            Assert.AreEqual(CharacterizationCommandCount, GetPendingCount());
            Assert.IsTrue(tasks.All(task => !task.IsCompleted));

            ProcessQueueMethod.Invoke(null, null);

            Assert.AreEqual(0, GetPendingCount());
            Assert.IsTrue(tasks.All(task => task.IsCompletedSuccessfully));
            foreach (Task<string> task in tasks)
            {
                JObject response = JObject.Parse(task.Result);
                Assert.AreEqual("success", response.Value<string>("status"));
                Assert.AreEqual("pong", response["result"]?.Value<string>("message"));
            }
        }

        [Test]
        public void CancellingQueuedCommand_RemovesItBeforeExecution()
        {
            var cancellation = new CancellationTokenSource();
            Task<string> commandTask = null;
            Exception backgroundException = null;

            var thread = new Thread(() =>
            {
                try
                {
                    commandTask = TransportCommandDispatcher.ExecuteCommandJsonAsync(
                        "ping",
                        cancellation.Token);
                }
                catch (Exception ex)
                {
                    backgroundException = ex;
                }
            });

            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "Background enqueue thread timed out.");
            Assert.IsNull(backgroundException);
            Assert.AreEqual(1, GetPendingCount());

            cancellation.Cancel();

            Assert.AreEqual(0, GetPendingCount());
            Assert.IsTrue(commandTask.IsCanceled);
            cancellation.Dispose();
        }

        [Test]
        public void RuntimeDispatcher_RejectsSixtyFifthCommandWithoutGrowingQueue()
        {
            List<Task<string>> tasks = QueuePingCommandsFromBackgroundThread(
                TransportCommandDispatcher.RuntimeMaxQueuedCommands,
                runtimeManaged: true);

            Exception rejection = EnqueueRuntimePingFromBackgroundThread();

            Assert.IsInstanceOf<CommandQueueFullException>(rejection);
            Assert.AreEqual("QUEUE_FULL", ((CommandQueueFullException)rejection).Code);
            Assert.AreEqual(TransportCommandDispatcher.RuntimeMaxQueuedCommands, GetPendingCount());

            DrainAllRuntimeCommands();
            Assert.IsTrue(tasks.All(task => task.IsCompletedSuccessfully));
        }

        [Test]
        public void RuntimeDispatcher_StartsAtMostFourCommandsPerPump()
        {
            const int commandCount = 8;
            List<Task<string>> tasks = QueuePingCommandsFromBackgroundThread(
                commandCount,
                runtimeManaged: true);

            ProcessQueueMethod.Invoke(null, null);

            int completedAfterOnePump = tasks.Count(task => task.IsCompletedSuccessfully);
            Assert.Greater(completedAfterOnePump, 0);
            Assert.LessOrEqual(
                completedAfterOnePump,
                TransportCommandDispatcher.RuntimeMaxCommandsPerPump);
            Assert.AreEqual(commandCount - completedAfterOnePump, GetPendingCount());

            DrainAllRuntimeCommands();
            Assert.IsTrue(tasks.All(task => task.IsCompletedSuccessfully));
        }

        [Test]
        public void RuntimeDispatcher_RejectsPayloadBytesBeforeQueueStorageGrows()
        {
            RuntimeQueuedPayloadBytesField.SetValue(
                null,
                TransportCommandDispatcher.RuntimeMaxQueuedPayloadBytes);

            try
            {
                Exception rejection = EnqueueRuntimePingFromBackgroundThread();

                Assert.IsInstanceOf<CommandQueueFullException>(rejection);
                Assert.AreEqual(
                    "QUEUE_BYTES_EXCEEDED",
                    ((CommandQueueFullException)rejection).Code);
                Assert.AreEqual(0, GetPendingCount());
            }
            finally
            {
                RuntimeQueuedPayloadBytesField.SetValue(null, 0L);
            }
        }

        [Test]
        public void RuntimeCancellationAfterExecutionSelection_PreservesActualResult()
        {
            var cancellation = new CancellationTokenSource();
            Task<string> commandTask = null;
            Exception backgroundException = null;
            var thread = new Thread(() =>
            {
                try
                {
                    commandTask = TransportCommandDispatcher.ExecuteRuntimeCommandJsonAsync(
                        "ping",
                        cancellation.Token,
                        () => cancellation.Cancel());
                }
                catch (Exception ex)
                {
                    backgroundException = ex;
                }
            });

            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "Background enqueue thread timed out.");
            Assert.IsNull(backgroundException);

            ProcessQueueMethod.Invoke(null, null);

            Assert.IsTrue(commandTask.IsCompletedSuccessfully);
            Assert.AreEqual(
                "pong",
                JObject.Parse(commandTask.Result)["result"]?.Value<string>("message"));
            Assert.AreEqual(0, GetPendingCount());
            cancellation.Dispose();
        }

        [Test]
        public void RuntimePreconditionConflict_ReturnsStructuredFailureBeforeHandler()
        {
            Task<string> commandTask = null;
            Exception backgroundException = null;
            var thread = new Thread(() =>
            {
                try
                {
                    commandTask = TransportCommandDispatcher.ExecuteRuntimeCommandJsonAsync(
                        "ping",
                        CancellationToken.None,
                        () => throw new CommandStateConflictException(
                            "STATE_CONFLICT",
                            "State changed.",
                            new JObject { ["refresh_required"] = true }));
                }
                catch (Exception ex)
                {
                    backgroundException = ex;
                }
            });

            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
            Assert.IsNull(backgroundException);

            ProcessQueueMethod.Invoke(null, null);

            Assert.IsTrue(commandTask.IsCompletedSuccessfully);
            JObject response = JObject.Parse(commandTask.Result);
            Assert.AreEqual("error", response.Value<string>("status"));
            Assert.AreEqual("STATE_CONFLICT", response.Value<string>("code"));
            Assert.IsTrue(response["data"]?.Value<bool>("refresh_required") ?? false);
        }

        private static List<Task<string>> QueuePingCommandsFromBackgroundThread(int count)
        {
            return QueuePingCommandsFromBackgroundThread(count, runtimeManaged: false);
        }

        private static List<Task<string>> QueuePingCommandsFromBackgroundThread(
            int count,
            bool runtimeManaged)
        {
            var tasks = new List<Task<string>>(count);
            Exception backgroundException = null;
            var thread = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        tasks.Add(runtimeManaged
                            ? TransportCommandDispatcher.ExecuteRuntimeCommandJsonAsync(
                                "ping",
                                CancellationToken.None)
                            : TransportCommandDispatcher.ExecuteCommandJsonAsync(
                                "ping",
                                CancellationToken.None));
                    }
                }
                catch (Exception ex)
                {
                    backgroundException = ex;
                }
            });

            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "Background enqueue thread timed out.");
            Assert.IsNull(backgroundException);
            return tasks;
        }

        private static Exception EnqueueRuntimePingFromBackgroundThread()
        {
            Exception backgroundException = null;
            var thread = new Thread(() =>
            {
                try
                {
                    TransportCommandDispatcher.ExecuteRuntimeCommandJsonAsync(
                        "ping",
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    backgroundException = ex;
                }
            });

            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "Background enqueue thread timed out.");
            return backgroundException;
        }

        private static void DrainAllRuntimeCommands()
        {
            int pumps = 0;
            while (GetPendingCount() > 0 && pumps++ < 100)
            {
                ProcessQueueMethod.Invoke(null, null);
            }

            Assert.AreEqual(0, GetPendingCount(), "Runtime dispatcher did not drain within 100 pumps.");
        }

        private static int GetPendingCount()
        {
            object pending = PendingField.GetValue(null);
            PropertyInfo countProperty = pending.GetType().GetProperty("Count");
            return (int)countProperty.GetValue(pending);
        }

        private static FieldInfo GetRequiredField(string name)
        {
            FieldInfo field = DispatcherType.GetField(
                name,
                BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new MissingFieldException(DispatcherType.FullName, name);
            }

            return field;
        }

        private static MethodInfo GetRequiredMethod(string name)
        {
            MethodInfo method = DispatcherType.GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMethodException(DispatcherType.FullName, name);
            }

            return method;
        }

        private sealed class CapturingSynchronizationContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object state)
            {
                // Intentionally capture without executing. The test invokes one explicit
                // dispatcher pump after all background commands have been admitted.
            }
        }
    }
}
