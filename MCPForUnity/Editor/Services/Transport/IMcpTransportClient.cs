using System.Threading.Tasks;

namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>
    /// Abstraction for MCP transport implementations (e.g. WebSocket push, stdio).
    /// </summary>
    public interface IMcpTransportClient
    {
        bool IsConnected { get; }
        string TransportName { get; }
        TransportState State { get; }

        Task<bool> StartAsync();
        Task StopAsync();
        Task<bool> VerifyAsync();
        Task ReregisterToolsAsync();
    }

    /// <summary>
    /// Optional control path for transports that can announce an intentional
    /// Unity domain reload before their connection is torn down.
    /// </summary>
    public interface IReloadLifecycleTransport
    {
        bool NotifyReloading(string reason = null);
    }
}
