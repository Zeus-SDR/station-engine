// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server;

/// <summary>
/// Supplies opaque, already encoded product-extension frames to the Station
/// Protocol stream core. Payload arrays must remain immutable while they are
/// being broadcast or retained in an attach snapshot.
/// </summary>
public interface IProductStreamSource
{
    event Action<byte[]>? FrameAvailable;

    IReadOnlyList<byte[]> GetAttachSnapshot();
}

public sealed class NullProductStreamSource : IProductStreamSource
{
    public static NullProductStreamSource Instance { get; } = new();

    private NullProductStreamSource() { }

    public event Action<byte[]>? FrameAvailable
    {
        add { }
        remove { }
    }

    public IReadOnlyList<byte[]> GetAttachSnapshot() => Array.Empty<byte[]>();
}

/// <summary>
/// Product-owned ingress for the raw JSON body of client diagnostic frame
/// 0x23. Parsing, rate limiting, and logging policy do not belong to the
/// engine stream core.
/// </summary>
public interface IClientDiagnosticSink
{
    void Handle(ReadOnlyMemory<byte> payload);
}

public sealed class NullClientDiagnosticSink : IClientDiagnosticSink
{
    public static NullClientDiagnosticSink Instance { get; } = new();

    private NullClientDiagnosticSink() { }

    public void Handle(ReadOnlyMemory<byte> payload) { }
}
