using System.Net.WebSockets;
using System.Net.Sockets;
using System.Text;
using JarvisBackend.Models;
using JarvisBackend.Services;

namespace JarvisBackend.WebSockets;

/// <summary>Handles /ws: register client, accumulate binary audio until "EOR" (end of recording), then publish one message to RabbitMQ.</summary>
public class AudioWebSocketHandler
{
    private readonly RabbitMqService _rabbit;
    private readonly ConnectionManager _manager;
    private readonly ILogger<AudioWebSocketHandler> _logger;

    public const string EndOfRecordingSignal = "EOR";

    /// <summary>Minimum audio bytes to publish (1 second at 16 kHz 16-bit). Avoids Whisper on fragments.</summary>
    public const int MinAudioBytes = 32000;

    public AudioWebSocketHandler(RabbitMqService rabbit, ConnectionManager manager, ILogger<AudioWebSocketHandler> logger)
    {
        _rabbit = rabbit;
        _manager = manager;
        _logger = logger;
    }

    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken = default)
    {
        var clientId = Guid.NewGuid().ToString();
        _manager.Add(clientId, socket);
        _logger.LogInformation("[WS] Client connected. ClientId={ClientId}", clientId);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("Connected. ClientId: " + clientId + ". Send binary (audio) chunks, then text 'EOR' to process."),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

        var buffer = new byte[4096];
        var audioBuffer = new List<byte>();

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogInformation("[WS] Client {ClientId} disconnected. Please say again when ready. ({Message})", clientId, ex.InnerException?.Message ?? ex.Message);
                    break;
                }
                catch (WebSocketException ex)
                {
                    _logger.LogInformation("[WS] Client {ClientId} disconnected. Please say again when ready. ({Message})", clientId, ex.Message);
                    break;
                }
                catch (IOException ex) when (ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset)
                {
                    _logger.LogInformation("[WS] Client {ClientId} disconnected. Please say again when ready.", clientId);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buffer.AsSpan(0, result.Count)).Trim();
                    if (text.Equals(EndOfRecordingSignal, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("[WS] EOR received. ClientId={ClientId}, AudioBufferBytes={Bytes}", clientId, audioBuffer.Count);
                        if (audioBuffer.Count >= MinAudioBytes)
                        {
                            var payload = audioBuffer.ToArray();
                            _logger.LogInformation("[WS] About to publish to RabbitMQ. ClientId={ClientId}, PayloadBytes={Bytes} (Whisper → DB + vector)", clientId, payload.Length);
                            try
                            {
                                _rabbit.Publish(new AudioMessage
                                {
                                    ClientId = clientId,
                                    AudioData = payload
                                });
                                _logger.LogInformation("[WS] Published to queue. ClientId={ClientId}", clientId);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[WS] RabbitMQ Publish failed. ClientId={ClientId}", clientId);
                            }
                            audioBuffer.Clear();
                        }
                        else if (audioBuffer.Count > 0)
                        {
                            _logger.LogWarning("[WS] EOR but audio too short ({Bytes} < {Min}). Skipping. ClientId={ClientId}", audioBuffer.Count, MinAudioBytes, clientId);
                            audioBuffer.Clear();
                        }
                        else
                        {
                            _logger.LogWarning("[WS] EOR but no audio in buffer (AudioBufferBytes=0). ClientId={ClientId}", clientId);
                        }
                        continue;
                    }
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0)
                {
                    var wasEmpty = audioBuffer.Count == 0;
                    for (int i = 0; i < result.Count; i++)
                        audioBuffer.Add(buffer[i]);
                    if (wasEmpty)
                        _logger.LogInformation("[WS] Audio received (first chunk). ClientId={ClientId}, ChunkBytes={Bytes}", clientId, result.Count);
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("[WS] Client {ClientId} disconnected. Please say again when ready. ({Message})", clientId, ex.InnerException?.Message ?? ex.Message);
        }
        catch (WebSocketException ex)
        {
            _logger.LogInformation("[WS] Client {ClientId} disconnected. Please say again when ready. ({Message})", clientId, ex.Message);
        }
        catch (IOException ex) when (ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset)
        {
            _logger.LogInformation("[WS] Client {ClientId} disconnected. Please say again when ready.", clientId);
        }
        catch (Exception ex) when (ex.InnerException is SocketException se2 && se2.SocketErrorCode == SocketError.ConnectionReset)
        {
            _logger.LogInformation("[WS] Client {ClientId} disconnected. Please say again when ready.", clientId);
        }
        finally
        {
            _manager.Remove(clientId);
        }
    }
}
