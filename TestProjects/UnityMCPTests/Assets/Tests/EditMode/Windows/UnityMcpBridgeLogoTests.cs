using MCPForUnity.Editor.Windows.Components.Branding;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnityTests.Editor.Windows
{
    public class UnityMcpBridgeLogoTests
    {
        [Test]
        public void Construct_LoadsPackageLogoAsDecorativeImage()
        {
            var logo = new UnityMcpBridgeLogo();

            Assert.IsInstanceOf<Texture2D>(logo.image);
            Assert.AreEqual(ScaleMode.ScaleToFit, logo.scaleMode);
            Assert.AreEqual(PickingMode.Ignore, logo.pickingMode);
            Assert.AreEqual("UnityMCPBridge", logo.tooltip);
        }

        [Test]
        public void PackageLogo_HasSquareHighResolutionTransparentTexture()
        {
            var texture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(
                UnityMcpBridgeLogo.AssetPath);

            Assert.IsNotNull(texture);
            Assert.AreEqual(texture.width, texture.height);
            Assert.GreaterOrEqual(texture.width, 512);
            Assert.IsTrue(texture.alphaIsTransparency);
        }
    }
}
