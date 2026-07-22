// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.MemoryMappedFiles;

namespace Station.AudioIpc.Spike;

public sealed class SharedMemoryAudioClient : IAudioBlockRoundTripClient
{
    private const int SlotCount = 8;
    private const int RingHeaderBytes = 64;
    private const int SlotHeaderBytes = 32;
    private const int SlotBytes = SlotHeaderBytes + AudioIpcProtocol.PayloadBytes;
    private const int RingBytes = 32_768;
    private const int MapBytes = RingBytes * 2;

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly CrossProcessSignal _inputSignal;
    private readonly CrossProcessSignal _outputSignal;
    private readonly MappedRing _input;
    private readonly MappedRing _output;
    private readonly float[] _discard = new float[AudioIpcProtocol.SamplesPerBlock];
    private long _sequence;
    private bool _disposed;

    private SharedMemoryAudioClient(AudioIpcEndpoint endpoint, MemoryMappedFile map,
        MemoryMappedViewAccessor view, CrossProcessSignal inputSignal, CrossProcessSignal outputSignal)
    {
        Endpoint = endpoint;
        _map = map;
        _view = view;
        _inputSignal = inputSignal;
        _outputSignal = outputSignal;
        _input = new MappedRing(view, 0);
        _output = new MappedRing(view, RingBytes);
        _input.Initialize();
        _output.Initialize();
    }

    public AudioIpcEndpoint Endpoint { get; }

    public static SharedMemoryAudioClient Create()
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var session = $"zph-{Environment.ProcessId}-{token}";
        var mapPath = Path.Combine(Path.GetTempPath(), $"{session}.audio-ipc");
        var inputSignalName = OperatingSystem.IsWindows()
            ? $"Local\\zph-{token}-i"
            : Path.Combine(Path.GetTempPath(), $"z{token}i.sock");
        var outputSignalName = OperatingSystem.IsWindows()
            ? $"Local\\zph-{token}-o"
            : Path.Combine(Path.GetTempPath(), $"z{token}o.sock");
        var endpoint = new AudioIpcEndpoint(AudioIpcTransportKind.SharedMemory, session,
            mapPath, inputSignalName, outputSignalName);

        var stream = new FileStream(mapPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.SetLength(MapBytes);
        var map = MemoryMappedFile.CreateFromFile(stream, null, MapBytes,
            MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        var view = map.CreateViewAccessor(0, MapBytes, MemoryMappedFileAccess.ReadWrite);
        var inputSignal = CrossProcessSignal.Create(inputSignalName, receives: false);
        var outputSignal = CrossProcessSignal.Create(outputSignalName, receives: true);
        return new SharedMemoryAudioClient(endpoint, map, view, inputSignal, outputSignal);
    }

    public bool TryRoundTrip(ReadOnlySpan<float> input, Span<float> output, TimeSpan timeout, out long roundTripTicks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.Length != AudioIpcProtocol.SamplesPerBlock || output.Length < input.Length)
            throw new ArgumentException("Audio IPC blocks must contain exactly 960 samples.");

        var started = AudioIpcProtocol.Timestamp();
        var sequence = Interlocked.Increment(ref _sequence);
        DrainResponses(sequence, output, out _);
        if (!_input.TryWrite(sequence, input))
        {
            // The producer never writes the consumer-owned tail. A wakeup here
            // lets a newly restarted server drain a ring that filled while it
            // was absent; the next scheduled block can then attach normally.
            _inputSignal.TrySet();
            roundTripTicks = AudioIpcProtocol.ElapsedTicks(started);
            return false;
        }

        if (!_inputSignal.TrySet())
        {
            roundTripTicks = AudioIpcProtocol.ElapsedTicks(started);
            return false;
        }
        while (AudioIpcProtocol.ElapsedTicks(started) < timeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency)
        {
            var remainingTicks = timeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency - AudioIpcProtocol.ElapsedTicks(started);
            if (remainingTicks <= 0 || !_outputSignal.Wait(TimeSpan.FromSeconds(remainingTicks / System.Diagnostics.Stopwatch.Frequency)))
                break;

            if (DrainResponses(sequence, output, out var found) && found)
            {
                roundTripTicks = AudioIpcProtocol.ElapsedTicks(started);
                return true;
            }
        }

        roundTripTicks = AudioIpcProtocol.ElapsedTicks(started);
        return false;
    }

    private bool DrainResponses(long wanted, Span<float> output, out bool found)
    {
        found = false;
        while (_output.TryRead(out var sequence, _discard))
        {
            if (sequence == wanted)
            {
                _discard.AsSpan().CopyTo(output);
                found = true;
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inputSignal.Dispose();
        _outputSignal.Dispose();
        _view.Dispose();
        _map.Dispose();
        TryDelete(Endpoint.MapPath);
        if (!OperatingSystem.IsWindows())
        {
            // The killed stub cannot unlink its input listener. The engine
            // owns the session lifetime and sweeps both private socket paths.
            TryDelete(Endpoint.InputSignalName);
            TryDelete(Endpoint.OutputSignalName);
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal sealed class MappedRing(MemoryMappedViewAccessor view, long offset)
    {
        private readonly float[] _scratch = new float[AudioIpcProtocol.SamplesPerBlock];

        public void Initialize()
        {
            view.Write(offset + 0, 0L);
            view.Write(offset + 8, 0L);
            view.Write(offset + 16, AudioIpcProtocol.Magic);
            view.Write(offset + 20, AudioIpcProtocol.Version);
            view.Write(offset + 22, (ushort)SlotCount);
            view.Write(offset + 24, AudioIpcProtocol.SamplesPerBlock);
        }

        public bool TryWrite(long sequence, ReadOnlySpan<float> samples)
        {
            var head = view.ReadInt64(offset);
            var tail = view.ReadInt64(offset + 8);
            if (head - tail >= SlotCount) return false;
            var slot = offset + RingHeaderBytes + (head & (SlotCount - 1)) * SlotBytes;
            view.Write(slot, sequence);
            view.Write(slot + 8, AudioIpcProtocol.Timestamp());
            samples.CopyTo(_scratch);
            view.WriteArray(slot + SlotHeaderBytes, _scratch, 0, _scratch.Length);
            Interlocked.MemoryBarrier();
            view.Write(offset, head + 1);
            return true;
        }

        public bool TryRead(out long sequence, Span<float> samples)
        {
            var tail = view.ReadInt64(offset + 8);
            var head = view.ReadInt64(offset);
            if (tail == head)
            {
                sequence = 0;
                return false;
            }

            Interlocked.MemoryBarrier();
            var slot = offset + RingHeaderBytes + (tail & (SlotCount - 1)) * SlotBytes;
            sequence = view.ReadInt64(slot);
            view.ReadArray(slot + SlotHeaderBytes, _scratch, 0, _scratch.Length);
            _scratch.AsSpan().CopyTo(samples);
            Interlocked.MemoryBarrier();
            view.Write(offset + 8, tail + 1);
            return true;
        }
    }

    internal static (MemoryMappedFile Map, MemoryMappedViewAccessor View, CrossProcessSignal InputSignal,
        CrossProcessSignal OutputSignal, MappedRing Input, MappedRing Output) OpenServer(AudioIpcEndpoint endpoint)
    {
        var stream = new FileStream(endpoint.MapPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var map = MemoryMappedFile.CreateFromFile(stream, null, MapBytes,
            MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        var view = map.CreateViewAccessor(0, MapBytes, MemoryMappedFileAccess.ReadWrite);
        var inputSignal = CrossProcessSignal.Open(endpoint.InputSignalName!, receives: true, TimeSpan.FromSeconds(5));
        var outputSignal = CrossProcessSignal.Open(endpoint.OutputSignalName!, receives: false, TimeSpan.FromSeconds(5));
        return (map, view, inputSignal, outputSignal, new MappedRing(view, 0), new MappedRing(view, RingBytes));
    }
}

public sealed class SharedMemoryAudioServer : IAudioBlockServer
{
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly CrossProcessSignal _inputSignal;
    private readonly CrossProcessSignal _outputSignal;
    private readonly SharedMemoryAudioClient.MappedRing _input;
    private readonly SharedMemoryAudioClient.MappedRing _output;
    private readonly float[] _block = new float[AudioIpcProtocol.SamplesPerBlock];

    public SharedMemoryAudioServer(AudioIpcEndpoint endpoint)
    {
        (_map, _view, _inputSignal, _outputSignal, _input, _output) = SharedMemoryAudioClient.OpenServer(endpoint);
    }

    public void Run(float gain, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_inputSignal.Wait(TimeSpan.FromMilliseconds(50))) continue;
            while (_input.TryRead(out var sequence, _block))
            {
                if (gain != 1f)
                    for (var i = 0; i < _block.Length; i++) _block[i] *= gain;
                if (_output.TryWrite(sequence, _block)) _outputSignal.TrySet();
            }
        }
    }

    public void Dispose()
    {
        _inputSignal.Dispose();
        _outputSignal.Dispose();
        _view.Dispose();
        _map.Dispose();
    }
}
