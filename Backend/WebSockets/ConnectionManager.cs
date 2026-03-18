using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace JarvisBackend.WebSockets;

/// <summary>Maps clientId → WebSocket for legacy /ws (RabbitMQ) flow.</summary>
public class ConnectionManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();

    public void Add(string clientId, WebSocket socket)
    {
        _connections[clientId] = socket;
    }

    public WebSocket? Get(string clientId)
    {
        return _connections.TryGetValue(clientId, out var socket) ? socket : null;
    }

    public void Remove(string clientId)
    {
        _connections.TryRemove(clientId, out _);
    }
}
