// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.GodsEye;
#else
namespace Zeus.Server.GodsEye;
#endif

public interface IAisStreamClient
{
    Task RunAsync(string apiKey, GodsEyeBounds bounds, Func<string, Task> onMessage, CancellationToken cancellationToken);
}

public sealed class AisStreamClient : IAisStreamClient
{
    private static readonly Uri Endpoint = new("wss://stream.aisstream.io/v0/stream");
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    internal const int MaxMessageBytes = 512 * 1024;

    public async Task RunAsync(string apiKey, GodsEyeBounds bounds, Func<string, Task> onMessage, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(ConnectTimeout);
        await socket.ConnectAsync(Endpoint, connectCts.Token).ConfigureAwait(false);
        var subscription = CreateSubscription(apiKey, bounds);
        await socket.SendAsync(subscription, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

        await ReceiveMessagesAsync(socket, onMessage, cancellationToken).ConfigureAwait(false);
    }

    internal static byte[] CreateSubscription(string apiKey, GodsEyeBounds bounds)
    {
        // AISStream defines BoundingBoxes coordinates as [latitude, longitude].
        // See https://aisstream.io/documentation for the subscription convention.
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            APIKey = apiKey,
            BoundingBoxes = new[] { new[] { new[] { bounds.South, bounds.West }, new[] { bounds.North, bounds.East } } },
            FilterMessageTypes = new[] { "PositionReport" },
        });
    }

    internal static async Task ReceiveMessagesAsync(WebSocket socket, Func<string, Task> onMessage, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close) return;
                if (received.Count > MaxMessageBytes - message.Length) throw new InvalidDataException("AISStream message exceeds byte cap.");
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            if (received.MessageType is WebSocketMessageType.Text or WebSocketMessageType.Binary)
                await onMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length))).ConfigureAwait(false);
        }
    }
}
