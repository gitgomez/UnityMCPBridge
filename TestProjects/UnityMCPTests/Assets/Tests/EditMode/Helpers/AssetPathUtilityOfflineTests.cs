using NUnit.Framework;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Constants;
using UnityEditor;

namespace MCPForUnityTests.Editor.Helpers
{
    public class AssetPathUtilityOfflineTests
    {
        private bool _originalForceRefresh;
        private bool _hadServerSourceOverride;
        private string _originalServerSourceOverride;

        [SetUp]
        public void SetUp()
        {
            _originalForceRefresh = EditorPrefs.GetBool(EditorPrefKeys.DevModeForceServerRefresh, false);
            _hadServerSourceOverride = EditorPrefs.HasKey(EditorPrefKeys.GitUrlOverride);
            _originalServerSourceOverride = EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride, string.Empty);
        }

        [TearDown]
        public void TearDown()
        {
            EditorPrefs.SetBool(EditorPrefKeys.DevModeForceServerRefresh, _originalForceRefresh);
            if (_hadServerSourceOverride)
            {
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, _originalServerSourceOverride);
            }
            else
            {
                EditorPrefs.DeleteKey(EditorPrefKeys.GitUrlOverride);
            }
        }

        [Test]
        public void ShouldUseUvxOffline_WhenForceRefreshEnabled_ReturnsFalse()
        {
            EditorPrefs.SetBool(EditorPrefKeys.DevModeForceServerRefresh, true);
            Assert.IsFalse(AssetPathUtility.ShouldUseUvxOffline());
        }

        [Test]
        public void ShouldUseUvxOffline_DoesNotThrow()
        {
            EditorPrefs.SetBool(EditorPrefKeys.DevModeForceServerRefresh, false);
            Assert.DoesNotThrow(() => AssetPathUtility.ShouldUseUvxOffline());
        }

        [Test]
        public void GetMcpServerPackageSource_WithoutOverride_UsesMatchingForkRelease()
        {
            EditorPrefs.DeleteKey(EditorPrefKeys.GitUrlOverride);
            string version = AssetPathUtility.GetPackageVersion();

            string source = AssetPathUtility.GetMcpServerPackageSource();

            Assert.AreEqual(
                $"git+https://github.com/gitgomez/UnityMCPBridge.git@v{version}#subdirectory=Server",
                source);
        }

        [Test]
        public void GetUvxDevFlags_LocalSource_UsesFreshIsolatedEnvironment()
        {
            Assert.AreEqual(
                "--isolated --reinstall --no-cache ",
                AssetPathUtility.GetUvxDevFlags(true, true, false));
            CollectionAssert.AreEqual(
                new[] { "--isolated", "--reinstall", "--no-cache" },
                AssetPathUtility.GetUvxDevFlagsList(true, true, false));
        }

        [Test]
        public void GetUvxDevFlags_RemoteForceRefresh_PreservesRefreshMode()
        {
            Assert.AreEqual(
                "--no-cache --refresh ",
                AssetPathUtility.GetUvxDevFlags(false, true, false));
            CollectionAssert.AreEqual(
                new[] { "--no-cache", "--refresh" },
                AssetPathUtility.GetUvxDevFlagsList(false, true, false));
        }

        [Test]
        public void GetUvxDevFlags_Offline_PreservesOfflineMode()
        {
            Assert.AreEqual(
                "--offline ",
                AssetPathUtility.GetUvxDevFlags(false, false, true));
            CollectionAssert.AreEqual(
                new[] { "--offline" },
                AssetPathUtility.GetUvxDevFlagsList(false, false, true));
        }
    }
}
