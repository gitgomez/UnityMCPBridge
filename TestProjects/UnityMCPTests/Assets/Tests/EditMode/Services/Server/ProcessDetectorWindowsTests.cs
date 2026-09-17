using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;
using UnityEngine;

namespace MCPForUnityTests.Editor.Services.Server
{
    [TestFixture]
    public class ProcessDetectorWindowsTests
    {
        [Test]
        public void ParseWindowsNetstatListeners_AcceptsLocalizedStateLabels()
        {
            const string output =
                "  TCP    127.0.0.1:8080    0.0.0.0:0       LISTENING       101\r\n" +
                "  TCP    0.0.0.0:8080      0.0.0.0:0       ABH\u00D6REN         202\r\n" +
                "  TCP    [::1]:8080        [::]:0          EN \u00C9COUTE      303\r\n";

            var result = ProcessDetector.ParseWindowsNetstatListeners(output, 8080);

            CollectionAssert.AreEquivalent(new[] { 101, 202, 303 }, result);
        }

        [Test]
        public void ParseWindowsNetstatListeners_RejectsConnectionsAndOtherPorts()
        {
            const string output =
                "  TCP    127.0.0.1:8080    127.0.0.1:51000  ESTABLISHED     101\r\n" +
                "  TCP    127.0.0.1:51000   127.0.0.1:8080   ESTABLISHED     202\r\n" +
                "  TCP    127.0.0.1:8081    0.0.0.0:0         LISTENING       303\r\n" +
                "  UDP    127.0.0.1:8080    *:*                               404\r\n";

            var result = ProcessDetector.ParseWindowsNetstatListeners(output, 8080);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseWindowsNetstatListeners_DeduplicatesListenerPid()
        {
            const string output =
                "  TCP    127.0.0.1:8080    0.0.0.0:0       LISTENING       101\r\n" +
                "  TCP    [::1]:8080        [::]:0          LISTENING       101\r\n";

            var result = ProcessDetector.ParseWindowsNetstatListeners(output, 8080);

            CollectionAssert.AreEqual(new[] { 101 }, result);
        }

        [TestCase("python.exe mcp-for-unity.exe --transport http --http-url http://127.0.0.1:8080", true)]
        [TestCase("uvx --from mcpforunityserver mcp-for-unity --transport http", true)]
        [TestCase("python.exe -m uvicorn example:app --transport http", true)]
        [TestCase("python.exe -m http.server 8080", false)]
        [TestCase("Unity.exe -projectPath D:/Game", false)]
        [TestCase("", false)]
        public void LooksLikeMcpServerCommandLine_ClassifiesSafely(string commandLine, bool expected)
        {
            Assert.AreEqual(expected, ProcessDetector.LooksLikeMcpServerCommandLine(commandLine));
        }

        [Test]
        public void GetListeningProcessIdsForPort_OnWindows_FindsCurrentListener()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Assert.Ignore("Windows netstat integration test.");
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                int currentPid = Process.GetCurrentProcess().Id;

                var result = new ProcessDetector().GetListeningProcessIdsForPort(port);

                Assert.That(result, Does.Contain(currentPid));
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void TryGetProcessCommandLine_OnWindows_ReadsCurrentProcessViaSupportedFallback()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Assert.Ignore("Windows process inspection integration test.");
            }

            var detector = new ProcessDetector();
            bool success = detector.TryGetProcessCommandLine(Process.GetCurrentProcess().Id, out string commandLine);

            Assert.IsTrue(success);
            Assert.IsNotEmpty(commandLine);
            Assert.IsFalse(commandLine.Any(char.IsWhiteSpace), "Command line should use the detector's normalized form.");
        }
    }
}
