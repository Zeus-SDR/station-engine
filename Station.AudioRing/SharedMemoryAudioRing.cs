// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Douglas J. Cerrato (KB2UKA)

using System.IO.MemoryMappedFiles;

namespace Station.AudioRing;

/// <summary>Engine-owned side of a private bidirectional audio-ring session.</summary>
public sealed class AudioRingOwner : IDisposable
{
    private const int SlotCount = 8;
    private const int RingHeaderBytes = 64;
    private const int SlotHeaderBytes = 32;
    private const int SlotBytes = SlotHeaderBytes + AudioRingProtocol.MaxSamplesPerBlock * sizeof(float);
    private const int RingBytes = AudioRingProtocol.RingBytes;
    private const int MapBytes = AudioRingProtocol.MapBytes;

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly CrossProcessSignal _inputSignal;
    private readonly CrossProcessSignal _outputSignal;
    private readonly MappedRing _input;
    private readonly MappedRing _output;
    private readonly float[] _discard = new float[AudioRingProtocol.MaxSamplesPerBlock];
    private long _sequence;
    private bool _disposed;

    private AudioRingOwner(
        AudioRingEndpoint endpoint,
        MemoryMappedFile map,
        MemoryMappedViewAccessor view,
        CrossProcessSignal inputSignal,
        CrossProcessSignal outputSignal)
    {
        Endpoint = endpoint;
        _map = map;
        _view = view;
        _inputSignal = inputSignal;
        _outputSignal = outputSignal;
        var token = AudioRingProtocol.SessionToken(endpoint.SessionId);
        MappedRing? input = null;
        MappedRing? output = null;
        try
        {
            input = new MappedRing(view, 0);
            output = new MappedRing(view, RingBytes);
            input.Initialize(token);
            output.Initialize(token);
            _input = input;
            _output = output;
        }
        catch
        {
            output?.Dispose();
            input?.Dispose();
            throw;
        }
    }

    public AudioRingEndpoint Endpoint { get; }

    public static AudioRingOwner Create()
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var session = $"zar-{Environment.ProcessId}-{token}";
        var mapPath = Path.Combine(Path.GetTempPath(), $"{session}.ring");
        var inputSignalName = OperatingSystem.IsWindows()
            ? $"Local\\zar-{token}-i"
            : Path.Combine(Path.GetTempPath(), $"z{token}i.sock");
        var outputSignalName = OperatingSystem.IsWindows()
            ? $"Local\\zar-{token}-o"
            : Path.Combine(Path.GetTempPath(), $"z{token}o.sock");
        var endpoint = new AudioRingEndpoint(
            AudioRingProtocol.Version,
            session,
            mapPath,
            inputSignalName,
            outputSignalName,
            AudioRingProtocol.SampleRate,
            AudioRingProtocol.MaxSamplesPerBlock);

        FileStream? stream = null;
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        CrossProcessSignal? inputSignal = null;
        CrossProcessSignal? outputSignal = null;
        try
        {
            stream = new FileStream(
                mapPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            stream.SetLength(MapBytes);
            map = MemoryMappedFile.CreateFromFile(
                stream,
                null,
                MapBytes,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false);
            view = map.CreateViewAccessor(0, MapBytes, MemoryMappedFileAccess.ReadWrite);
            inputSignal = CrossProcessSignal.Create(inputSignalName, receives: false);
            outputSignal = CrossProcessSignal.Create(outputSignalName, receives: true);
            return new AudioRingOwner(endpoint, map, view, inputSignal, outputSignal);
        }
        catch
        {
            outputSignal?.Dispose();
            inputSignal?.Dispose();
            view?.Dispose();
            map?.Dispose();
            stream?.Dispose();
            TryDelete(mapPath);
            if (!OperatingSystem.IsWindows())
            {
                TryDelete(inputSignalName);
                TryDelete(outputSignalName);
            }
            throw;
        }
    }

    /// <summary>
    /// Offers one block without waiting for ring capacity, then polls only up
    /// to the caller's deadline for its matching response. Output is untouched
    /// unless a complete, sequence-matched block is available.
    /// </summary>
    public bool TryRoundTrip(
        ReadOnlySpan<float> input,
        Span<float> output,
        TimeSpan timeout,
        out long roundTripTicks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateBlock(input, output);
        if (timeout <= TimeSpan.Zero
            || timeout > TimeSpan.FromMilliseconds(AudioRingProtocol.BlockPeriodMilliseconds))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var started = AudioRingProtocol.Timestamp();
        var sequence = Interlocked.Increment(ref _sequence);
        DrainResponses(sequence, input.Length, output, out _);
        if (!_input.TryWrite(sequence, input) || !_inputSignal.TrySet())
        {
            roundTripTicks = AudioRingProtocol.ElapsedTicks(started);
            return false;
        }

        var timeoutTicks = timeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency;
        while (AudioRingProtocol.ElapsedTicks(started) < timeoutTicks)
        {
            if (DrainResponses(sequence, input.Length, output, out var found) && found)
            {
                _outputSignal.Drain();
                roundTripTicks = AudioRingProtocol.ElapsedTicks(started);
                return true;
            }

            Thread.SpinWait(32);
        }

        _outputSignal.Drain();
        roundTripTicks = AudioRingProtocol.ElapsedTicks(started);
        return false;
    }

    private bool DrainResponses(long wanted, int wantedSamples, Span<float> output, out bool found)
    {
        found = false;
        while (_output.TryRead(out var sequence, out var sampleCount, _discard))
        {
            if (sequence != wanted || sampleCount != wantedSamples)
                continue;
            _discard.AsSpan(0, sampleCount).CopyTo(output);
            found = true;
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _inputSignal.Dispose();
        _outputSignal.Dispose();
        _input.Dispose();
        _output.Dispose();
        _view.Dispose();
        _map.Dispose();
        TryDelete(Endpoint.MapPath);
        if (!OperatingSystem.IsWindows())
        {
            TryDelete(Endpoint.InputSignalName);
            TryDelete(Endpoint.OutputSignalName);
        }
    }

    private static void ValidateBlock(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.IsEmpty
            || input.Length > AudioRingProtocol.MaxSamplesPerBlock
            || output.Length < input.Length)
        {
            throw new ArgumentException(
                $"Audio-ring blocks must contain 1 through {AudioRingProtocol.MaxSamplesPerBlock} samples.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static OpenedRing OpenConsumer(AudioRingEndpoint endpoint)
    {
        if (endpoint.ProtocolVersion != AudioRingProtocol.Version
            || endpoint.SampleRate != AudioRingProtocol.SampleRate
            || endpoint.MaxSamplesPerBlock != AudioRingProtocol.MaxSamplesPerBlock)
        {
            throw new InvalidDataException("The negotiated audio-ring protocol is incompatible.");
        }

        FileStream? stream = null;
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        CrossProcessSignal? inputSignal = null;
        CrossProcessSignal? outputSignal = null;
        MappedRing? input = null;
        MappedRing? output = null;
        try
        {
            stream = new FileStream(
                endpoint.MapPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            map = MemoryMappedFile.CreateFromFile(
                stream,
                null,
                MapBytes,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false);
            view = map.CreateViewAccessor(0, MapBytes, MemoryMappedFileAccess.ReadWrite);
            inputSignal = CrossProcessSignal.Open(
                endpoint.InputSignalName,
                receives: true,
                TimeSpan.FromSeconds(5));
            outputSignal = CrossProcessSignal.Open(
                endpoint.OutputSignalName,
                receives: false,
                TimeSpan.FromSeconds(5));
            input = new MappedRing(view, 0);
            output = new MappedRing(view, RingBytes);
            var token = AudioRingProtocol.SessionToken(endpoint.SessionId);
            input.Validate(token);
            output.Validate(token);
            return new OpenedRing(map, view, inputSignal, outputSignal, input, output);
        }
        catch
        {
            output?.Dispose();
            input?.Dispose();
            outputSignal?.Dispose();
            inputSignal?.Dispose();
            view?.Dispose();
            map?.Dispose();
            stream?.Dispose();
            throw;
        }
    }

    internal sealed record OpenedRing(
        MemoryMappedFile Map,
        MemoryMappedViewAccessor View,
        CrossProcessSignal InputSignal,
        CrossProcessSignal OutputSignal,
        MappedRing Input,
        MappedRing Output);

    internal sealed unsafe class MappedRing : IDisposable
    {
        private readonly MemoryMappedViewAccessor _view;
        private readonly long _offset;
        private byte* _pointer;
        private bool _disposed;

        public MappedRing(MemoryMappedViewAccessor view, long offset)
        {
            _view = view;
            _offset = offset;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
            _pointer += view.PointerOffset;
        }

        public void Initialize(ulong sessionToken)
        {
            Volatile.Write(ref Int64At(0), 0);
            Volatile.Write(ref Int64At(8), 0);
            Volatile.Write(ref Int32At(16), unchecked((int)AudioRingProtocol.Magic));
            Volatile.Write(ref Int32At(20), AudioRingProtocol.Version | (SlotCount << 16));
            Volatile.Write(ref Int32At(24), AudioRingProtocol.MaxSamplesPerBlock);
            Volatile.Write(ref Int32At(28), SlotBytes);
            Volatile.Write(ref Int64At(32), unchecked((long)sessionToken));
            Interlocked.MemoryBarrier();
        }

        public void Validate(ulong sessionToken)
        {
            Interlocked.MemoryBarrier();
            var versionAndSlots = Volatile.Read(ref Int32At(20));
            if (unchecked((uint)Volatile.Read(ref Int32At(16))) != AudioRingProtocol.Magic
                || (ushort)versionAndSlots != AudioRingProtocol.Version
                || (ushort)(versionAndSlots >> 16) != SlotCount
                || Volatile.Read(ref Int32At(24)) != AudioRingProtocol.MaxSamplesPerBlock
                || Volatile.Read(ref Int32At(28)) != SlotBytes
                || unchecked((ulong)Volatile.Read(ref Int64At(32))) != sessionToken)
            {
                throw new InvalidDataException("The audio-ring shared-memory header is invalid.");
            }
        }

        public bool TryWrite(long sequence, ReadOnlySpan<float> samples)
        {
            var head = Volatile.Read(ref Int64At(0));
            var tail = Volatile.Read(ref Int64At(8));
            if (head < tail || head - tail >= SlotCount)
                return false;

            var slotOffset = RingHeaderBytes + (head & (SlotCount - 1)) * SlotBytes;
            Volatile.Write(ref Int64At(slotOffset + 16), 0);
            Int64At(slotOffset) = sequence;
            Int64At(slotOffset + 8) = AudioRingProtocol.Timestamp();
            Int32At(slotOffset + 24) = samples.Length;
            samples.CopyTo(new Span<float>(Address(slotOffset + SlotHeaderBytes), samples.Length));
            Interlocked.MemoryBarrier();
            Volatile.Write(ref Int64At(slotOffset + 16), sequence);
            Volatile.Write(ref Int64At(0), head + 1);
            return true;
        }

        public bool TryRead(out long sequence, out int sampleCount, Span<float> samples)
        {
            while (true)
            {
                var tail = Volatile.Read(ref Int64At(8));
                var head = Volatile.Read(ref Int64At(0));
                if (tail == head || head < tail || head - tail > SlotCount)
                {
                    sequence = 0;
                    sampleCount = 0;
                    return false;
                }

                Interlocked.MemoryBarrier();
                var slotOffset = RingHeaderBytes + (tail & (SlotCount - 1)) * SlotBytes;
                var candidateSequence = Int64At(slotOffset);
                var commit = Volatile.Read(ref Int64At(slotOffset + 16));
                var candidateCount = Int32At(slotOffset + 24);
                if (candidateSequence == commit
                    && candidateSequence != 0
                    && candidateCount > 0
                    && candidateCount <= AudioRingProtocol.MaxSamplesPerBlock
                    && candidateCount <= samples.Length)
                {
                    new ReadOnlySpan<float>(
                        Address(slotOffset + SlotHeaderBytes),
                        candidateCount).CopyTo(samples);
                    Interlocked.MemoryBarrier();
                    Volatile.Write(ref Int64At(8), tail + 1);
                    sequence = candidateSequence;
                    sampleCount = candidateCount;
                    return true;
                }

                // A producer that dies around publication must not wedge the
                // consumer forever. Discard the malformed committed position.
                Volatile.Write(ref Int64At(8), tail + 1);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _pointer -= _view.PointerOffset;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        private byte* Address(long relativeOffset) => _pointer + _offset + relativeOffset;
        private ref long Int64At(long relativeOffset) => ref *(long*)Address(relativeOffset);
        private ref int Int32At(long relativeOffset) => ref *(int*)Address(relativeOffset);
    }
}

/// <summary>Bundle-owned side of an engine-created audio-ring session.</summary>
public sealed class AudioRingConsumer : IDisposable
{
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly CrossProcessSignal _inputSignal;
    private readonly CrossProcessSignal _outputSignal;
    private readonly AudioRingOwner.MappedRing _input;
    private readonly AudioRingOwner.MappedRing _output;
    private readonly float[] _block = new float[AudioRingProtocol.MaxSamplesPerBlock];
    private bool _disposed;

    public AudioRingConsumer(AudioRingEndpoint endpoint)
    {
        var opened = AudioRingOwner.OpenConsumer(endpoint);
        _map = opened.Map;
        _view = opened.View;
        _inputSignal = opened.InputSignal;
        _outputSignal = opened.OutputSignal;
        _input = opened.Input;
        _output = opened.Output;
    }

    public void Run(AudioBlockProcessor processor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processor);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_inputSignal.Wait(TimeSpan.FromMilliseconds(50)))
                continue;
            while (_input.TryRead(out var sequence, out var sampleCount, _block))
            {
                processor(_block.AsSpan(0, sampleCount));
                if (_output.TryWrite(sequence, _block.AsSpan(0, sampleCount)))
                    _outputSignal.TrySet();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _inputSignal.Dispose();
        _outputSignal.Dispose();
        _input.Dispose();
        _output.Dispose();
        _view.Dispose();
        _map.Dispose();
    }
}
