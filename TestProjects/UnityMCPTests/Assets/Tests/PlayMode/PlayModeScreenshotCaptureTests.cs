using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using MCPForUnity.Runtime.Helpers;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MCPForUnityTests.PlayMode
{
    public class PlayModeScreenshotCaptureTests
    {
        private const string CaptureFolder =
            "Temp/MCPForUnityTests/ScreenshotCapture";

        [UnityTest]
        public IEnumerator CaptureScope_EnablesAndRestoresRunInBackground()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore(
                    "Composited Game View capture is unavailable in batch mode.");
            }

            bool originalRunInBackground = Application.runInBackground;
            IDisposable captureScope = null;
            try
            {
                Application.runInBackground = false;

                captureScope =
                    ScreenshotUtility.BeginEditorCompositedCaptureScope();

                Assert.IsTrue(
                    Application.runInBackground,
                    "The capture scope must keep the PlayerLoop active.");

                captureScope.Dispose();
                captureScope = null;

                Assert.IsFalse(
                    Application.runInBackground,
                    "The capture scope must restore the previous background setting.");
            }
            finally
            {
                captureScope?.Dispose();
                Application.runInBackground = originalRunInBackground;
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator CaptureCompositedAsync_CompletesAndRestoresBackgroundSetting()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore(
                    "Composited Game View capture is unavailable in batch mode.");
            }

            bool originalRunInBackground = Application.runInBackground;
            ScreenshotCaptureResult result = default;
            Task<ScreenshotCaptureResult> captureTask = null;

            try
            {
                Application.runInBackground = false;
                captureTask = ScreenshotUtility.CaptureCompositedAsync(
                    fileName: "playmode-composited-capture.png",
                    includeImage: true,
                    maxResolution: 128,
                    folderOverride: CaptureFolder,
                    timeoutSeconds: 5.0);

                while (!captureTask.IsCompleted)
                {
                    yield return null;
                }

                if (captureTask.IsFaulted)
                {
                    Assert.Fail(
                        captureTask.Exception?.Flatten().ToString() ??
                        "Composited capture faulted without an exception.");
                }

                Assert.IsFalse(
                    captureTask.IsCanceled,
                    "Composited capture should not be canceled.");

                result = captureTask.Result;
                Assert.IsTrue(File.Exists(result.FullPath), result.FullPath);
                Assert.Greater(
                    new FileInfo(result.FullPath).Length,
                    0,
                    "The captured PNG should contain image data.");
                Assert.That(result.ImageBase64, Is.Not.Empty);
                Assert.Greater(result.ImageWidth, 0);
                Assert.Greater(result.ImageHeight, 0);
                Assert.IsFalse(
                    Application.runInBackground,
                    "Capture completion must restore the previous background setting.");
            }
            finally
            {
                if (!string.IsNullOrEmpty(result.FullPath) &&
                    File.Exists(result.FullPath))
                {
                    File.Delete(result.FullPath);
                }

                Application.runInBackground = originalRunInBackground;
            }
        }
    }
}
