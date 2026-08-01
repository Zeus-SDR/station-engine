// SPDX-License-Identifier: GPL-3.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// SPE Expert 1.5K Taurus amplifier support. This file is GPL-3.0-or-later
// (see Station.Engine.Hosting/SpeTaurus/SOURCE.md); the rest of the engine is
// GPL-2.0-or-later, whose "or later" option permits the combination. The
// resulting engine binary is distributed as GPL-3.0-or-later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Runtime.InteropServices;
using System.Text;

namespace Zeus.Server.SpeTaurus;

internal sealed record SpeD2xxDevice(
    string Serial,
    string Description,
    uint Type,
    uint Id,
    uint Location,
    bool IsOpen);

internal sealed record SpeD2xxDiagnostic(
    bool Available,
    string? Library,
    string? Version,
    string? Error);

internal sealed record SpeD2xxScan(
    IReadOnlyList<SpeD2xxDevice> Devices,
    SpeD2xxDiagnostic Diagnostic)
{
    internal static SpeD2xxScan NotProbed { get; } = new(
        [],
        new(false, null, null, "D2XX discovery is inactive until devices are refreshed."));
}

internal interface ID2xxApi : IDisposable
{
    string LibraryName { get; }
    string? LibraryVersion { get; }
    IReadOnlyList<SpeD2xxDevice> Enumerate();
    IntPtr OpenBySerial(string serial);
    void SetBaudRate(IntPtr handle, uint baudRate);
    void SetDataCharacteristics(IntPtr handle, byte wordLength, byte stopBits, byte parity);
    void SetFlowControl(IntPtr handle, ushort flowControl, byte xon, byte xoff);
    void ClearDtr(IntPtr handle);
    void ClearRts(IntPtr handle);
    void SetTimeouts(IntPtr handle, uint readTimeoutMs, uint writeTimeoutMs);
    void Purge(IntPtr handle, uint mask);
    uint GetQueueStatus(IntPtr handle);
    uint Read(IntPtr handle, Memory<byte> buffer);
    uint Write(IntPtr handle, ReadOnlyMemory<byte> buffer);
    void Close(IntPtr handle);
}

internal sealed class SpeD2xxTransport : ISpeTransport
{
    internal const uint ReadTimeoutMs = 100;
    private const uint PurgeRxTx = 3;
    private readonly object _gate = new();
    private readonly Func<ID2xxApi> _apiFactory;
    private ID2xxApi? _api;
    private IntPtr _handle;

    internal SpeD2xxTransport(Func<ID2xxApi>? apiFactory = null) =>
        _apiFactory = apiFactory ?? NativeD2xxApi.Load;

    public bool IsOpen => Volatile.Read(ref _handle) != IntPtr.Zero;

    public Task OpenAsync(SpeTaurusConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_handle != IntPtr.Zero)
                throw new InvalidOperationException("D2XX device is already open.");
            var requested = SpeTaurusService.SanitizeD2xxSerial(config.D2xxSerial);
            if (requested.Length == 0)
                throw new InvalidOperationException(
                    "Select the Taurus FTDI device by its exact serial number before connecting.");

            var api = _api ??= _apiFactory();
            var matches = api.Enumerate()
                .Where(device => string.Equals(device.Serial, requested, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            var device = matches.Length switch
            {
                0 => throw new InvalidOperationException(
                    "The selected FTDI serial was not found. Refresh devices and select it again."),
                1 => matches[0],
                _ => throw new InvalidOperationException(
                    "Multiple FTDI devices reported the selected serial; selection is ambiguous."),
            };
            if (!string.Equals(
                    SpeTaurusService.SanitizeD2xxSerial(device.Serial),
                    device.Serial,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("The selected FTDI device reported an unusable serial.");

            var handle = IntPtr.Zero;
            try
            {
                handle = api.OpenBySerial(device.Serial);
                api.SetBaudRate(handle, 115_200);
                api.SetDataCharacteristics(handle, 8, 0, 0);
                api.SetFlowControl(handle, 0, 0, 0);
                api.ClearDtr(handle);
                api.ClearRts(handle);
                api.SetTimeouts(handle, ReadTimeoutMs, checked((uint)config.ResponseTimeoutMs));
                api.Purge(handle, PurgeRxTx);
                Volatile.Write(ref _handle, handle);
                return Task.CompletedTask;
            }
            catch
            {
                if (handle != IntPtr.Zero)
                {
                    try { api.Close(handle); }
                    catch { }
                }
                throw;
            }
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty) return 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var (api, handle) = OpenState();
                var queued = api.GetQueueStatus(handle);
                if (queued > 0)
                {
                    var requested = (int)Math.Min(queued, checked((uint)buffer.Length));
                    var read = api.Read(handle, buffer[..requested]);
                    if (read > requested)
                        throw new IOException("D2XX returned a byte count larger than the supplied buffer.");
                    if (read > 0) return checked((int)read);
                }
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (bytes.IsEmpty) return ValueTask.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var (api, handle) = OpenState();
            var written = api.Write(handle, bytes);
            if (written != bytes.Length)
                throw new IOException(
                    $"D2XX reported a partial write ({written} of {bytes.Length}); "
                    + "the command outcome is ambiguous and was not retried.");
        }
        return ValueTask.CompletedTask;
    }

    public Task CloseAsync()
    {
        lock (_gate)
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero && _api is not null) _api.Close(handle);
            _api?.Dispose();
            _api = null;
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private (ID2xxApi Api, IntPtr Handle) OpenState()
    {
        var api = _api;
        var handle = Volatile.Read(ref _handle);
        if (api is null || handle == IntPtr.Zero) throw new IOException("D2XX device is not open.");
        return (api, handle);
    }
}

internal static class SpeD2xxDiscovery
{
    internal static SpeD2xxScan Scan(Func<ID2xxApi>? apiFactory = null)
    {
        try
        {
            using var api = (apiFactory ?? NativeD2xxApi.Load)();
            var devices = api.Enumerate();
            string? error = null;
            if (devices.Count == 0)
                error = OperatingSystem.IsLinux()
                    ? "The runtime loaded, but no devices were found. ftdi_sio/usbserial may own "
                      + "the device; Zeus will not unload kernel modules. Use VCP or configure "
                      + "the official D2XX driver manually."
                    : "The runtime loaded, but no FTDI devices were found.";
            return new(devices, new(true, api.LibraryName, api.LibraryVersion, error));
        }
        catch (Exception ex)
        {
            return new([], new(false, null, null, ex.Message));
        }
    }
}

internal sealed class D2xxException(string operation, uint status)
    : IOException($"{operation} failed: {D2xxStatus.Name(status)} ({status}). {D2xxStatus.Advice(status)}")
{
    public uint Status { get; } = status;
}

internal static class D2xxStatus
{
    internal static void Check(string operation, uint status)
    {
        if (status != 0) throw new D2xxException(operation, status);
    }

    internal static string Name(uint status) => status switch
    {
        0 => "FT_OK",
        1 => "FT_INVALID_HANDLE",
        2 => "FT_DEVICE_NOT_FOUND",
        3 => "FT_DEVICE_NOT_OPENED",
        4 => "FT_IO_ERROR",
        5 => "FT_INSUFFICIENT_RESOURCES",
        6 => "FT_INVALID_PARAMETER",
        7 => "FT_INVALID_BAUD_RATE",
        9 => "FT_DEVICE_NOT_OPENED_FOR_WRITE",
        10 => "FT_FAILED_TO_WRITE_DEVICE",
        17 => "FT_NOT_SUPPORTED",
        19 => "FT_DEVICE_LIST_NOT_READY",
        _ => "FT_OTHER_ERROR",
    };

    internal static string Advice(uint status) => status switch
    {
        2 => "Refresh devices and verify the selected serial.",
        3 or 9 => "Close any VCP or other D2XX application using this device.",
        4 or 10 => "The device may have been unplugged; reconnect it before retrying.",
        7 => "This transport requires 115200 baud.",
        19 => "Device discovery is not ready; refresh and retry.",
        _ => "Check the installed FTDI runtime and USB connection.",
    };
}

internal sealed class NativeD2xxApi : ID2xxApi
{
    private const uint OpenBySerialFlag = 1;
    private const uint OpenFlag = 1;
    private const int SerialCapacity = 16;
    private const int DescriptionCapacity = 64;
    private static readonly object LoadGate = new();
    private static NativeD2xxApi? s_cached;
    private static InvalidOperationException? s_incompatible;

    private readonly IntPtr _library;
    private readonly CreateDeviceInfoList _createList;
    private readonly GetDeviceInfoDetail _deviceDetail;
    private readonly OpenEx _open;
    private readonly SetBaud _baud;
    private readonly SetData _data;
    private readonly SetFlow _flow;
    private readonly HandleCall _clearDtr;
    private readonly HandleCall _clearRts;
    private readonly SetTimeoutsCall _timeouts;
    private readonly PurgeCall _purge;
    private readonly GetQueue _queue;
    private readonly BufferCall _read;
    private readonly BufferCall _write;
    private readonly HandleCall _close;

    private NativeD2xxApi(IntPtr library, string name)
    {
        _library = library;
        LibraryName = name;
        _createList = Export<CreateDeviceInfoList>("FT_CreateDeviceInfoList");
        _deviceDetail = Export<GetDeviceInfoDetail>("FT_GetDeviceInfoDetail");
        _open = Export<OpenEx>("FT_OpenEx");
        _baud = Export<SetBaud>("FT_SetBaudRate");
        _data = Export<SetData>("FT_SetDataCharacteristics");
        _flow = Export<SetFlow>("FT_SetFlowControl");
        _clearDtr = Export<HandleCall>("FT_ClrDtr");
        _clearRts = Export<HandleCall>("FT_ClrRts");
        _timeouts = Export<SetTimeoutsCall>("FT_SetTimeouts");
        _purge = Export<PurgeCall>("FT_Purge");
        _queue = Export<GetQueue>("FT_GetQueueStatus");
        _read = Export<BufferCall>("FT_Read");
        _write = Export<BufferCall>("FT_Write");
        _close = Export<HandleCall>("FT_Close");
        LibraryVersion = ReadVersion();
    }

    public string LibraryName { get; }
    public string? LibraryVersion { get; }

    internal static ID2xxApi Load()
    {
        lock (LoadGate)
        {
            if (s_cached is not null) return s_cached;
            if (s_incompatible is not null) throw s_incompatible;
            var names = OperatingSystem.IsWindows()
                ? new[] { "ftd2xx.dll" }
                : OperatingSystem.IsMacOS()
                    ? new[] { "libftd2xx.dylib" }
                    : OperatingSystem.IsLinux()
                        ? new[] { "libftd2xx.so", "libftd2xx.so.1" }
                        : [];
            if (names.Length == 0)
                throw new PlatformNotSupportedException("D2XX is supported on Windows, Linux, and macOS.");
            var search = DllImportSearchPath.SafeDirectories;
            if (OperatingSystem.IsWindows()) search |= DllImportSearchPath.System32;
            foreach (var name in names)
            {
                if (!NativeLibrary.TryLoad(name, typeof(NativeD2xxApi).Assembly, search, out var library))
                    continue;
                try
                {
                    s_cached = new NativeD2xxApi(library, name);
                    return s_cached;
                }
                catch (Exception ex)
                {
                    s_incompatible = new InvalidOperationException(
                        $"The installed D2XX runtime '{name}' is incompatible or missing required exports. "
                        + "Install a current runtime matching the Zeus process architecture.",
                        ex);
                    throw s_incompatible;
                }
            }
            throw new DllNotFoundException(
                $"FTDI D2XX runtime is not installed. Tried: {string.Join(", ", names)}.");
        }
    }

    public IReadOnlyList<SpeD2xxDevice> Enumerate()
    {
        D2xxStatus.Check("FT_CreateDeviceInfoList", _createList(out var count));
        var devices = new List<SpeD2xxDevice>(checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            var serial = Marshal.AllocHGlobal(SerialCapacity);
            var description = Marshal.AllocHGlobal(DescriptionCapacity);
            try
            {
                Zero(serial, SerialCapacity);
                Zero(description, DescriptionCapacity);
                D2xxStatus.Check("FT_GetDeviceInfoDetail", _deviceDetail(
                    index, out var flags, out var type, out var id, out var location,
                    serial, description, out _));
                devices.Add(new(
                    Decode(serial, SerialCapacity),
                    Decode(description, DescriptionCapacity),
                    type,
                    id,
                    location,
                    (flags & OpenFlag) != 0));
            }
            finally
            {
                Marshal.FreeHGlobal(serial);
                Marshal.FreeHGlobal(description);
            }
        }
        return devices;
    }

    public IntPtr OpenBySerial(string serial)
    {
        var value = Marshal.StringToHGlobalAnsi(serial);
        try
        {
            D2xxStatus.Check("FT_OpenEx", _open(value, OpenBySerialFlag, out var handle));
            if (handle == IntPtr.Zero) throw new IOException("FT_OpenEx returned an empty handle.");
            return handle;
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    public void SetBaudRate(IntPtr h, uint value) => D2xxStatus.Check("FT_SetBaudRate", _baud(h, value));
    public void SetDataCharacteristics(IntPtr h, byte bits, byte stops, byte parity) =>
        D2xxStatus.Check("FT_SetDataCharacteristics", _data(h, bits, stops, parity));
    public void SetFlowControl(IntPtr h, ushort flow, byte xon, byte xoff) =>
        D2xxStatus.Check("FT_SetFlowControl", _flow(h, flow, xon, xoff));
    public void ClearDtr(IntPtr h) => D2xxStatus.Check("FT_ClrDtr", _clearDtr(h));
    public void ClearRts(IntPtr h) => D2xxStatus.Check("FT_ClrRts", _clearRts(h));
    public void SetTimeouts(IntPtr h, uint readMs, uint writeMs) =>
        D2xxStatus.Check("FT_SetTimeouts", _timeouts(h, readMs, writeMs));
    public void Purge(IntPtr h, uint mask) => D2xxStatus.Check("FT_Purge", _purge(h, mask));
    public uint GetQueueStatus(IntPtr h)
    {
        D2xxStatus.Check("FT_GetQueueStatus", _queue(h, out var count));
        return count;
    }

    public unsafe uint Read(IntPtr h, Memory<byte> buffer)
    {
        using var pin = buffer.Pin();
        D2xxStatus.Check("FT_Read", _read(h, (IntPtr)pin.Pointer, checked((uint)buffer.Length), out var count));
        return count;
    }

    public unsafe uint Write(IntPtr h, ReadOnlyMemory<byte> buffer)
    {
        using var pin = buffer.Pin();
        D2xxStatus.Check("FT_Write", _write(h, (IntPtr)pin.Pointer, checked((uint)buffer.Length), out var count));
        return count;
    }

    public void Close(IntPtr h) => D2xxStatus.Check("FT_Close", _close(h));
    public void Dispose() { }

    internal static string FormatVersion(uint version) =>
        $"{(version >> 16) & 0xFF:X}.{(version >> 8) & 0xFF:X2}.{version & 0xFF:X2}";

    private string? ReadVersion()
    {
        if (!NativeLibrary.TryGetExport(_library, "FT_GetLibraryVersion", out var address)) return null;
        var call = Marshal.GetDelegateForFunctionPointer<GetVersion>(address);
        return call(out var version) == 0 ? FormatVersion(version) : null;
    }

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private static void Zero(IntPtr address, int length)
    {
        for (var index = 0; index < length; index++) Marshal.WriteByte(address, index, 0);
    }

    internal static string Decode(IntPtr address, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(address, bytes, 0, length);
        var terminator = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, terminator < 0 ? length : terminator);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint CreateDeviceInfoList(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetDeviceInfoDetail(uint index, out uint flags, out uint type, out uint id, out uint location, IntPtr serial, IntPtr description, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint OpenEx(IntPtr value, uint flags, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint HandleCall(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint SetBaud(IntPtr handle, uint baud);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint SetData(IntPtr handle, byte bits, byte stops, byte parity);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint SetFlow(IntPtr handle, ushort flow, byte xon, byte xoff);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint SetTimeoutsCall(IntPtr handle, uint readMs, uint writeMs);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint PurgeCall(IntPtr handle, uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetQueue(IntPtr handle, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint BufferCall(IntPtr handle, IntPtr buffer, uint requested, out uint transferred);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetVersion(out uint version);
}
