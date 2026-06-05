using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using LinkPulse.Abstractions;

namespace LinkPulse.Server;

/// <summary>
/// Drives one accepted probe WebSocket for its lifetime (v1 spec, &#167;3.5): it echoes ping frames
/// back <em>verbatim</em> &#8212; never parsing or rewriting the opaque client send-stamp, so RTT
/// stays a pure client-clock measurement (&#167;3.1) &#8212; and ingests snapshot frames into the
/// registry after validation, tagging each with the server's receive time for last-seen bookkeeping
/// only (&#167;3.1). It also enforces the server-side minimum ping interval (&#167;11) by dropping
/// pings that arrive faster than allowed.
/// </summary>
internal static class ProbeConnectionHandler
{
    // Probe frames are tiny JSON objects; this is the initial receive buffer. Larger messages are
    // reassembled up to MaxMessageBytes, beyond which the peer is misbehaving and the socket is closed.
    private const int ReceiveBufferBytes = 4 * 1024;
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>
    /// Runs the receive loop until the socket closes, the request is aborted, or the peer floods us
    /// past the message-size guard. On exit, any session this connection registered is removed from
    /// the registry so the dashboard's active-session count stays accurate.
    /// </summary>
    public static async Task RunAsync(
        WebSocket socket,
        LinkPulseRegistry registry,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var minPingInterval = TimeSpan.FromMilliseconds(LinkPulseOptions.MinPingIntervalMs);
        long lastAcceptedPingTimestamp = 0;

        // The (clientId, sessionId) of the latest snapshot, so we can deregister on close.
        Guid clientId = default;
        Guid sessionId = default;

        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferBytes);
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
                if (message.CloseRequested)
                {
                    await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (message.TooLarge)
                {
                    await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (message.Type != WebSocketMessageType.Text || message.Payload.IsEmpty)
                {
                    continue;
                }

                switch (ReadFrameType(message.Payload.Span))
                {
                    case FrameType.Ping:
                        // §11 throttle: only echo pings no faster than the server minimum.
                        if (lastAcceptedPingTimestamp != 0 &&
                            timeProvider.GetElapsedTime(lastAcceptedPingTimestamp) < minPingInterval)
                        {
                            break;
                        }

                        lastAcceptedPingTimestamp = timeProvider.GetTimestamp();
                        await socket.SendAsync(
                            message.Payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case FrameType.Snapshot:
                        if (TryReadSnapshot(message.Payload.Span, out var frame) &&
                            SnapshotValidator.TryValidate(frame, out var cid, out var sid, out var snapshot))
                        {
                            clientId = cid;
                            sessionId = sid;
                            registry.RecordSnapshot(cid, sid, snapshot, timeProvider.GetUtcNow());
                        }

                        break;

                    case FrameType.Unknown:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Request aborted / server shutting down — fall through to cleanup.
        }
        catch (WebSocketException)
        {
            // Abrupt client disconnect (no close handshake) — fall through to cleanup.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (clientId != default && sessionId != default)
            {
                registry.RemoveSession(clientId, sessionId);
            }
        }
    }

    private static async Task<ReceivedMessage> ReceiveMessageAsync(
        WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        var first = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (first.MessageType == WebSocketMessageType.Close)
        {
            return ReceivedMessage.Close;
        }

        if (first.EndOfMessage)
        {
            // Common case: the whole frame fit in one receive — no copy, hand back the buffer slice.
            return new ReceivedMessage(first.MessageType, buffer.AsMemory(0, first.Count));
        }

        // Fragmented frame: accumulate the rest, bounded by MaxMessageBytes.
        var assembled = new ArrayBufferWriter<byte>(first.Count * 2);
        assembled.Write(buffer.AsSpan(0, first.Count));
        var result = first;
        while (!result.EndOfMessage)
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return ReceivedMessage.Close;
            }

            if (assembled.WrittenCount + result.Count > MaxMessageBytes)
            {
                return ReceivedMessage.OversizedOf(first.MessageType);
            }

            assembled.Write(buffer.AsSpan(0, result.Count));
        }

        return new ReceivedMessage(first.MessageType, assembled.WrittenMemory);
    }

    private static FrameType ReadFrameType(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return FrameType.Unknown;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var isType = reader.ValueTextEquals("type");
                if (!reader.Read())
                {
                    break;
                }

                if (isType && reader.TokenType == JsonTokenType.String)
                {
                    if (reader.ValueTextEquals("ping"))
                    {
                        return FrameType.Ping;
                    }

                    if (reader.ValueTextEquals("snapshot"))
                    {
                        return FrameType.Snapshot;
                    }

                    return FrameType.Unknown;
                }

                reader.Skip(); // value of a property we don't care about
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — treat as an unknown frame and ignore it.
        }

        return FrameType.Unknown;
    }

    private static bool TryReadSnapshot(ReadOnlySpan<byte> json, out SnapshotFrame? frame)
    {
        try
        {
            // Deserialized as the concrete type: the polymorphic "type" discriminator is simply an
            // unmapped property here and is ignored, leaving the camelCase metric fields to bind.
            frame = JsonSerializer.Deserialize(json, LinkPulseJsonContext.Default.SnapshotFrame);
            return frame is not null;
        }
        catch (JsonException)
        {
            frame = null;
            return false;
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(status, statusDescription: null, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Peer already gone; nothing to close gracefully.
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }

    private enum FrameType
    {
        Unknown,
        Ping,
        Snapshot,
    }

    private readonly struct ReceivedMessage
    {
        public ReceivedMessage(WebSocketMessageType type, ReadOnlyMemory<byte> payload)
        {
            Type = type;
            Payload = payload;
            CloseRequested = false;
            TooLarge = false;
        }

        private ReceivedMessage(bool closeRequested, bool tooLarge, WebSocketMessageType type)
        {
            Type = type;
            Payload = default;
            CloseRequested = closeRequested;
            TooLarge = tooLarge;
        }

        public WebSocketMessageType Type { get; }

        public ReadOnlyMemory<byte> Payload { get; }

        public bool CloseRequested { get; }

        public bool TooLarge { get; }

        public static ReceivedMessage Close => new(closeRequested: true, tooLarge: false, WebSocketMessageType.Close);

        public static ReceivedMessage OversizedOf(WebSocketMessageType type) => new(closeRequested: false, tooLarge: true, type);
    }
}
