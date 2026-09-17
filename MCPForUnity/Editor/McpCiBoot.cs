using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport.Transports;

namespace MCPForUnity.Editor
{
    public static class McpCiBoot
    {
        public static void StartStdioForCi()
        {
            // Keep the in-memory configuration cache in sync with EditorPrefs. Writing the
            // preference directly is not sufficient when another InitializeOnLoad class has
            // already materialized the cache; the reload handler would then still regard HTTP
            // as selected and deliberately decline to restart this bridge after compilation.
            EditorConfigurationCache.Instance.SetUseHttpTransport(false);

            StdioBridgeHost.StartAutoConnect();
        }
    }
}
