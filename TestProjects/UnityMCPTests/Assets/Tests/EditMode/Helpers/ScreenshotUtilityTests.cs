using System;
using System.Collections.Generic;
using MCPForUnity.Runtime.Helpers;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Helpers
{
    public class ScreenshotUtilityTests
    {
        private Func<bool> _originalGetter;
        private Action<bool> _originalSetter;

        [SetUp]
        public void SetUp()
        {
            _originalGetter = ScreenshotUtility.RunInBackgroundGetter;
            _originalSetter = ScreenshotUtility.RunInBackgroundSetter;
        }

        [TearDown]
        public void TearDown()
        {
            ScreenshotUtility.RunInBackgroundGetter = _originalGetter;
            ScreenshotUtility.RunInBackgroundSetter = _originalSetter;
        }

        [Test]
        public void BackgroundOverride_EnablesAndRestoresPreviousValue()
        {
            bool currentValue = false;
            var assignedValues = new List<bool>();
            ScreenshotUtility.RunInBackgroundGetter = () => currentValue;
            ScreenshotUtility.RunInBackgroundSetter = value =>
            {
                assignedValues.Add(value);
                currentValue = value;
            };

            using (ScreenshotUtility.BeginRunInBackgroundOverride())
            {
                Assert.IsTrue(currentValue);
            }

            Assert.IsFalse(currentValue);
            CollectionAssert.AreEqual(
                new[] { true, false },
                assignedValues);
        }

        [Test]
        public void BackgroundOverride_PreservesEnabledValueWithoutWrites()
        {
            bool currentValue = true;
            var assignedValues = new List<bool>();
            ScreenshotUtility.RunInBackgroundGetter = () => currentValue;
            ScreenshotUtility.RunInBackgroundSetter = value =>
            {
                assignedValues.Add(value);
                currentValue = value;
            };

            using (ScreenshotUtility.BeginRunInBackgroundOverride())
            {
                Assert.IsTrue(currentValue);
            }

            Assert.IsTrue(currentValue);
            CollectionAssert.IsEmpty(assignedValues);
        }
    }
}
