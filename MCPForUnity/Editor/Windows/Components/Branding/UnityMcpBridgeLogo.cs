using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Windows.Components.Branding
{
    /// <summary>
    /// Displays the UnityMCPBridge cube logo from the package icon asset.
    /// </summary>
    public sealed class UnityMcpBridgeLogo : Image
    {
        internal const string AssetPath =
            "Packages/com.coplaydev.unity-mcp/package-icon.png";

        public UnityMcpBridgeLogo()
        {
            image = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetPath);
            scaleMode = ScaleMode.ScaleToFit;
            pickingMode = PickingMode.Ignore;
            tooltip = "UnityMCPBridge";
        }
    }
}
