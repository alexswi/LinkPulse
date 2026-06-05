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
    /// <param name="socket">The accepted probe socket.</param>
    /// <param name="registry">The registry to record validated snapshots into.</param>
    /// <param name="timeProvider">The clock used for the &#167;11 ping/snapshot throttle and snapshot timestamps.</param>
    /// <param name="userAgent">The connection's already-bounded <c>User-Agent</c> (&#167;10), or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Fires when the request is aborted or the server is shutting down.</param>
    public static async Task RunAsync(
        WebSocket socket,
        LinkPulseRegistry registry,
        TimeProvider timeProvider,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var minInterval = TimeSpan.FromMilliseconds(LinkPulseOptions.MinPingIntervalMs);
        // Nullable sentinels — not 0 — so the first frame is always let through even under a fake
        // TimeProvider whose GetTimestamp() starts at 0 (the engine/registry are built for fake clocks).
        long? lastAcceptedPingTimestamp = null;
        long? lastAcceptedSnapshotTimestamp = null;

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
                        // §11 throttle: only echo pings no faster than the server minimum. A flooding
                        // client's excess pings are dropped (it will see them as loss on timeout)
                        // rather than amplified back onto the wire.
                        if (lastAcceptedPingTimestamp is { } lastPing &&
                            timeProvider.GetElapsedTime(lastPing) < minInterval)
                        {
                            break;
                        }

                        lastAcceptedPingTimestamp = timeProvider.GetTimestamp();
                        // message.Payload aliases the rented receive buffer in the common single-frame
                        // case; it must be fully sent here before the next ReceiveAsync overwrites it.
                        await socket.SendAsync(
                            message.Payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case FrameType.Snapshot:
                        // §11 abuse bound: rate-limit snapshot ingestion the same way, so a single
                        // socket cannot flood the registry with parse + validate + record work (and a
                        // Changed-event storm). Legitimate snapshots arrive seconds apart, far above this.
                        if (lastAcceptedSnapshotTimestamp is { } lastSnap &&
                            timeProvider.GetElapsedTime(lastSnap) < minInterval)
                        {
                            break;
                        }

                        lastAcceptedSnapshotTimestamp = timeProvider.GetTimestamp();
                        if (TryReadSnapshot(message.Payload.Span, out var frame) &&
                            SnapshotValidator.TryValidate(frame, out var cid, out var sid, out var snapshot))
                        {
                            clientId = cid;
                            sessionId = sid;
                            registry.RecordSnapshot(cid, sid, snapshot, timeProvider.GetUtcNow(), userAgent);
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
