using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.Storage.Streams;

namespace BtNotifier;

// ───────────────────────── Helpers ─────────────────────────

static class Util
{
    /// Runs WinRT/IO work with a timeout so one stuck call can never freeze the app.
    public static async Task<T> Timed<T>(Func<Task<T>> work, int seconds = 10)
    {
        var task = Task.Run(work);
        try { return await task.WaitAsync(TimeSpan.FromSeconds(seconds)); }
        catch (TimeoutException)
        {
            // The call may still finish later; make sure its result (a device, a service...) gets released
            _ = task.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result is IDisposable d) d.Dispose();
            }, TaskScheduler.Default);
            throw;
        }
    }
}

// ───────────────────────── Discovery (classic + Low Energy) ─────────────────────────

sealed record FoundDevice(string Name, ulong Address, bool LowEnergy, bool Connected);

static class Discovery
{
    static readonly string[] Props = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" };

    /// All paired classic + Low Energy devices, identified by Bluetooth address.
    public static async Task<List<FoundDevice>> ListPairedAsync()
    {
        var found = new Dictionary<ulong, FoundDevice>();
        // Classic first, so a dual-mode device stays classic (better audio/battery support)
        await Collect(BluetoothDevice.GetDeviceSelectorFromPairingState(true), false, found);
        await Collect(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true, found);
        return found.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static async Task Collect(string selector, bool lowEnergy, Dictionary<ulong, FoundDevice> into)
    {
        try
        {
            var infos = await Util.Timed(async () => await DeviceInformation.FindAllAsync(selector, Props));
            foreach (var i in infos)
            {
                if (string.IsNullOrWhiteSpace(i.Name) || !TryAddress(i, out var addr)) continue;
                bool connected = i.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is bool b && b;
                into.TryAdd(addr, new FoundDevice(i.Name, addr, lowEnergy, connected));
            }
        }
        catch (Exception e) { Log.Info($"Device list ({(lowEnergy ? "LE" : "classic")}) failed: {e.Message}"); }
    }

    static bool TryAddress(DeviceInformation info, out ulong addr)
    {
        addr = 0;
        string? s = null;
        if (info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var v) && v is string str) s = str;
        // Fallback: the device id ends with "-<address>"
        if (string.IsNullOrEmpty(s) && info.Id.Contains('-')) s = info.Id[(info.Id.LastIndexOf('-') + 1)..];
        if (string.IsNullOrEmpty(s)) return false;

        var hex = new string(s.Where(Uri.IsHexDigit).ToArray());
        return hex.Length is > 0 and <= 12 &&
               ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr);
    }
}

// ───────────────────────── Connection links (classic vs LE) ─────────────────────────

interface ILink : IDisposable
{
    string Name { get; }
    ulong Address { get; }
    bool Connected { get; }
    bool IsLowEnergy { get; }
    bool LooksLikeAudio { get; }
    event Action? Changed;
    Task<int?> GattBatteryAsync();
}

sealed class ClassicLink : ILink
{
    readonly BluetoothDevice _d;

    public ClassicLink(BluetoothDevice d) { _d = d; _d.ConnectionStatusChanged += OnChanged; }

    void OnChanged(BluetoothDevice sender, object args) => Changed?.Invoke();

    public string Name => _d.Name;
    public ulong Address => _d.BluetoothAddress;
    public bool Connected => _d.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public bool IsLowEnergy => false;
    public bool LooksLikeAudio
    {
        get
        {
            try { return _d.ClassOfDevice.MajorClass == BluetoothMajorClass.AudioVideo; }
            catch { return false; }
        }
    }
    public event Action? Changed;
    public Task<int?> GattBatteryAsync() => Task.FromResult<int?>(null);

    public void Dispose() { _d.ConnectionStatusChanged -= OnChanged; _d.Dispose(); }
}

sealed class LeLink : ILink
{
    readonly BluetoothLEDevice _d;

    public LeLink(BluetoothLEDevice d) { _d = d; _d.ConnectionStatusChanged += OnChanged; }

    void OnChanged(BluetoothLEDevice sender, object args) => Changed?.Invoke();

    public string Name => _d.Name;
    public ulong Address => _d.BluetoothAddress;
    public bool Connected => _d.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public bool IsLowEnergy => true;
    public bool LooksLikeAudio => false;
    public event Action? Changed;

    /// Standard Bluetooth "Battery Service" (0x180F) / "Battery Level" (0x2A19).
    public async Task<int?> GattBatteryAsync()
    {
        try
        {
            var svcs = await Util.Timed(async () =>
                await _d.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached), 6);
            if (svcs.Status != GattCommunicationStatus.Success || svcs.Services.Count == 0) return null;

            try
            {
                var svc = svcs.Services[0];
                var chars = await Util.Timed(async () =>
                    await svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached), 6);
                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) return null;

                var read = await Util.Timed(async () =>
                    await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached), 6);
                if (read.Status != GattCommunicationStatus.Success) return null;

                byte value = DataReader.FromBuffer(read.Value).ReadByte();
                return value <= 100 ? (int)value : (int?)null;
            }
            finally
            {
                foreach (var s in svcs.Services) s.Dispose(); // closes the GATT session
            }
        }
        catch (Exception e)
        {
            Log.Info("GATT battery read failed: " + e.Message);
            return null;
        }
    }

    public void Dispose() { _d.ConnectionStatusChanged -= OnChanged; _d.Dispose(); }
}

// ───────────────────────── Battery via PnP devnode property ─────────────────────────
// Windows stores the battery % of Bluetooth devices as devnode property
// {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, pid 2 (same value Settings / the flyout shows).

[StructLayout(LayoutKind.Sequential)]
struct DEVPROPKEY
{
    public Guid fmtid;
    public uint pid;
}

static class BatteryReader
{
    const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    static readonly Guid BatteryFmt = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out uint len, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, uint bufferLen, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type,
        byte[] buffer, ref uint size, uint flags);

    static List<string> ListIds(string enumerator)
    {
        var result = new List<string>();
        const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        if (CM_Get_Device_ID_List_SizeW(out uint len, enumerator, flags) != 0 || len == 0) return result;
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(enumerator, buf, len, flags) != 0) return result;
        foreach (var id in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            result.Add(id);
        return result;
    }

    static int? ReadNode(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint inst, instanceId, 0) != 0) return null;
        var key = new DEVPROPKEY { fmtid = BatteryFmt, pid = 2 };
        var buf = new byte[8];
        uint size = (uint)buf.Length;
        if (CM_Get_DevNode_PropertyW(inst, ref key, out _, buf, ref size, 0) != 0 || size < 1) return null;
        return buf[0] <= 100 ? buf[0] : null;
    }

    /// Every devnode belonging to this Bluetooth address, with its battery value (if any).
    public static List<(string Id, int? Value)> ReadAll(ulong address)
    {
        var addr = address.ToString("X12");
        var list = new List<(string, int?)>();
        foreach (var en in new[] { "BTHENUM", "BTHLEDEVICE" })
            foreach (var id in ListIds(en))
                if (id.Contains(addr, StringComparison.OrdinalIgnoreCase))
                    list.Add((id, ReadNode(id)));
        return list;
    }

    public static int? Read(ulong address)
    {
        try { return ReadAll(address).Select(x => x.Value).FirstOrDefault(v => v != null); }
        catch (Exception e) { Log.Info("Battery read failed: " + e.Message); return null; }
    }
}

// ───────────────────────── Audio / mic endpoints (WinRT, no extra libraries) ─────────────────────────

readonly record struct AudioInfo(bool OutputActive, bool MicActive, bool IsDefaultOutput, bool HasEndpoints);

static class AudioProbe
{
    // Windows names endpoints like "Headphones (Device Name)" or "Headset (2- Device Name)"
    static bool IsExact(string endpoint, string device)
    {
        if (string.Equals(endpoint, device, StringComparison.OrdinalIgnoreCase)) return true;
        if (!endpoint.EndsWith(device + ")", StringComparison.OrdinalIgnoreCase)) return false;

        int i = endpoint.Length - device.Length - 2; // character just before the device name
        while (i >= 0 && endpoint[i] == ' ') i--;
        if (i >= 0 && endpoint[i] == '(') return true;
        if (i >= 0 && endpoint[i] == '-')             // "(2- Name)" duplicate-device prefix
        {
            i--;
            int digits = 0;
            while (i >= 0 && char.IsDigit(endpoint[i])) { i--; digits++; }
            return digits > 0 && i >= 0 && endpoint[i] == '(';
        }
        return false;
    }

    static List<(string Id, string Name)> Mine(List<(string Id, string Name)> all, string device)
    {
        var exact = all.Where(e => IsExact(e.Name, device)).ToList();
        if (exact.Count > 0) return exact;
        // fallback for devices whose endpoint name differs slightly from the Bluetooth name
        return all.Where(e => e.Name.Contains(device, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    static async Task<List<(string Id, string Name)>> ListAsync(string selector)
    {
        var infos = await Util.Timed(async () => await DeviceInformation.FindAllAsync(selector), 5);
        return infos.Select(i => (i.Id, i.Name)).ToList();
    }

    /// Active (enabled, present) audio endpoints that belong to this device.
    public static async Task<AudioInfo> GetAsync(string device)
    {
        try
        {
            var outs = Mine(await ListAsync(MediaDevice.GetAudioRenderSelector()), device);
            var mics = Mine(await ListAsync(MediaDevice.GetAudioCaptureSelector()), device);

            bool isDefault = false;
            try
            {
                var defaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                isDefault = outs.Any(o => o.Id == defaultId);
            }
            catch { /* no default output device */ }

            return new AudioInfo(outs.Count > 0, mics.Count > 0, isDefault, outs.Count > 0 || mics.Count > 0);
        }
        catch (Exception e)
        {
            Log.Info("Audio probe failed: " + e.Message);
            return new AudioInfo(false, false, false, false);
        }
    }

    public static async Task<string> DescribeAsync(string device)
    {
        var sb = new StringBuilder();
        try
        {
            foreach (var e in Mine(await ListAsync(MediaDevice.GetAudioRenderSelector()), device))
                sb.AppendLine($"  Output: {e.Name}");
            foreach (var e in Mine(await ListAsync(MediaDevice.GetAudioCaptureSelector()), device))
                sb.AppendLine($"  Mic: {e.Name}");
        }
        catch (Exception e) { sb.AppendLine("  error: " + e.Message); }
        return sb.Length == 0 ? "  (no active audio endpoints - not an audio device, or not connected)\n" : sb.ToString();
    }
}

// ───────────────────────── Per-device tracker ─────────────────────────

sealed class DeviceTracker : IDisposable
{
    static readonly TimeSpan ReResolveEvery = TimeSpan.FromMinutes(15);

    readonly TrackedDevice _dev;
    readonly Config _cfg;
    readonly string _tag;
    readonly SemaphoreSlim _gate = new(1, 1);

    ILink? _link;
    volatile bool _disposed;
    int _evalQueued;           // 1 = a change-triggered evaluation is already waiting
    bool? _lastState;          // null = unknown, true = connected
    int? _lastBattery;
    bool _lowSent;
    DateTime _nextBatteryCheck = DateTime.MaxValue;
    DateTime _lastResolve = DateTime.MinValue;

    public DeviceTracker(TrackedDevice dev, Config cfg)
    {
        _dev = dev;
        _cfg = cfg;
        _tag = "bt" + dev.Address.ToString("X12");
    }

    string Label => _link?.Name ?? _dev.Name;
    bool IsConnected() => _link?.Connected == true;

    // ---- setup ----

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!await ResolveAsync()) { Log.Info($"[{_dev.Name}] not reachable yet"); return; }
            _lastState = IsConnected();
            Log.Info($"[{Label}] started ({(_dev.LowEnergy ? "LE" : "classic")}, {_dev.Address:X12}), connected={_lastState}");
            if (_lastState == true)
            {
                _lastBattery = await ReadBatteryAsync();
                ScheduleBattery();
            }
        }
        finally { _gate.Release(); }
    }

    /// Opens the device by Bluetooth address (not by name) and subscribes to connection changes.
    /// The new link replaces the old one atomically, so a link is never orphaned (and never leaks its subscription).
    async Task<bool> ResolveAsync()
    {
        if (_disposed) return false;
        _lastResolve = DateTime.UtcNow;
        ILink? link = null;
        try
        {
            if (_dev.LowEnergy)
            {
                var d = await Util.Timed(async () => await BluetoothLEDevice.FromBluetoothAddressAsync(_dev.Address));
                if (d != null) link = new LeLink(d);
            }
            else
            {
                var d = await Util.Timed(async () => await BluetoothDevice.FromBluetoothAddressAsync(_dev.Address));
                if (d != null) link = new ClassicLink(d);
            }
            if (link == null) return false; // keep the previous link, if any

            link.Changed += OnChanged;
            Interlocked.Exchange(ref _link, link)?.Dispose();
            link = null; // ownership moved to _link

            if (_disposed) Interlocked.Exchange(ref _link, null)?.Dispose(); // Dispose() ran while we were resolving
            return !_disposed;
        }
        catch (Exception e)
        {
            Log.Info($"[{_dev.Name}] resolve failed: {e.Message}");
            return false;
        }
        finally { link?.Dispose(); } // only still set if something threw before the hand-over
    }

    // ---- state machine ----

    // A burst of connection events produces one evaluation, not a queue of them
    void OnChanged()
    {
        if (Interlocked.Exchange(ref _evalQueued, 1) == 0) _ = EvaluateAsync(settle: true);
    }

    async Task EvaluateAsync(bool settle)
    {
        await _gate.WaitAsync();
        try
        {
            Volatile.Write(ref _evalQueued, 0); // from here on, new events queue a fresh evaluation
            if (_disposed) return;
            if (_link == null && !await ResolveAsync()) return;
            if (settle) await Task.Delay(500); // let flapping connections settle

            bool connected = IsConnected();
            if (_lastState == connected) return; // state-based dedupe, no timers needed
            _lastState = connected;

            if (connected) await NotifyConnectedAsync();
            else NotifyDisconnected();
        }
        catch (Exception e) { Log.Info($"[{Label}] evaluate failed: {e}"); }
        finally { _gate.Release(); }
    }

    public async Task TickAsync()
    {
        await EvaluateAsync(settle: false); // safety net in case an event was missed
        if (_disposed) return;

        // Periodically re-open the device: guards against the OS silently dropping event subscriptions
        if (DateTime.UtcNow - _lastResolve > ReResolveEvery)
        {
            await _gate.WaitAsync();
            try { await ResolveAsync(); }
            finally { _gate.Release(); }
            await EvaluateAsync(settle: false);
        }

        if (_lastState == true && DateTime.UtcNow >= _nextBatteryCheck)
            await CheckBatteryAsync();
    }

    public async Task OnResumeAsync()
    {
        await _gate.WaitAsync();
        try { await ResolveAsync(); }
        finally { _gate.Release(); }
        await EvaluateAsync(settle: false);
    }

    // ---- battery ----

    void ScheduleBattery() => _nextBatteryCheck = DateTime.UtcNow.AddMinutes(Math.Max(1, _cfg.BatteryPollMinutes));

    async Task<int?> ReadBatteryAsync()
    {
        var b = BatteryReader.Read(_dev.Address);
        if (b == null && _link is { IsLowEnergy: true } le) b = await le.GattBatteryAsync();
        return b;
    }

    async Task CheckBatteryAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || _link == null || !IsConnected()) return;
            var b = await ReadBatteryAsync();
            ScheduleBattery();
            if (b == null) return;

            _lastBattery = b;
            if (b <= _cfg.LowBatteryPercent && !_lowSent)
            {
                _lowSent = true;
                Notifier.Show(_cfg, _tag, Label, $"Low battery: {b}%", $"Low battery  ·  {b}%", b, "Low", null,
                              _link?.LooksLikeAudio == true, FlyoutTone.Warn);
            }
            else if (b > _cfg.LowBatteryPercent + 5) _lowSent = false;
        }
        catch (Exception e) { Log.Info($"[{Label}] battery check failed: {e.Message}"); }
        finally { _gate.Release(); }
    }

    // ---- notifications ----

    async Task NotifyConnectedAsync()
    {
        var link = _link!;
        var name = link.Name;
        var sw = Stopwatch.StartNew();
        int? battery = link.IsLowEnergy ? await link.GattBatteryAsync() : null;
        AudioInfo audio;
        bool isAudio;

        // Battery and audio endpoints show up a moment after the link comes up; wait briefly.
        while (true)
        {
            battery ??= BatteryReader.Read(_dev.Address);
            audio = await AudioProbe.GetAsync(name);
            isAudio = link.LooksLikeAudio || audio.HasEndpoints;

            double limit = isAudio ? _cfg.SettleSeconds : Math.Min(_cfg.SettleSeconds, 2);
            if ((!isAudio || audio.OutputActive) && battery != null) break;
            if (sw.Elapsed.TotalSeconds >= limit) break;
            await Task.Delay(400);
        }

        _lastBattery = battery;
        _lowSent = false;
        ScheduleBattery();
        Log.Info($"[{name}] CONNECTED battery={battery} audio={isAudio} output={audio.OutputActive} mic={audio.MicActive} default={audio.IsDefaultOutput} ({sw.ElapsedMilliseconds} ms)");
        ShowConnected(name, battery, audio, isAudio);
    }

    void NotifyDisconnected()
    {
        _nextBatteryCheck = DateTime.MaxValue;
        var name = Label;
        Log.Info($"[{name}] DISCONNECTED (last battery={_lastBattery})");

        Notifier.Show(_cfg, _tag, name, "Disconnected",
                      _lastBattery is int p ? $"Disconnected  ·  last battery {p}%" : "Disconnected",
                      null, null, _lastBattery is int q ? $"Last battery: {q}%" : null,
                      _link?.LooksLikeAudio == true, FlyoutTone.Dim);
    }

    void ShowConnected(string name, int? battery, AudioInfo a, bool isAudio)
    {
        var parts = new List<string>();
        if (battery == null) parts.Add("Battery: not reported");

        // Audio/mic info only for devices that actually have audio endpoints
        if (isAudio)
        {
            var audio = a.OutputActive ? (a.IsDefaultOutput ? "Active (default)" : "Active") : "Not ready";
            parts.Add($"Audio: {audio}");
            parts.Add($"Mic: {(a.MicActive ? "Available" : "Not available")}");
        }

        Notifier.Show(_cfg, _tag, name, "Connected",
                      battery is int p ? $"Connected  ·  {p}%" : "Connected",
                      battery, battery is int b ? BatteryLabel(b) : null,
                      parts.Count > 0 ? string.Join("  •  ", parts) : null, isAudio, FlyoutTone.Normal);
    }

    static string BatteryLabel(int p) => p >= 80 ? "Full" : p >= 40 ? "Good" : p >= 20 ? "Low" : "Very low";

    // ---- tray helpers ----

    public async Task TestAsync()
    {
        if (_link == null) await ResolveAsync();
        var name = Label;
        var battery = await ReadBatteryAsync();
        var audio = await AudioProbe.GetAsync(name);
        ShowConnected(name, battery, audio, (_link?.LooksLikeAudio ?? false) || audio.HasEndpoints);
    }

    public async Task<string> DiagnosticsAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Saved device: {_dev.Name}  ({(_dev.LowEnergy ? "Low Energy" : "classic")}, {_dev.Address:X12})");
        if (_link == null && !await ResolveAsync())
        {
            sb.AppendLine("Could not open the device (not paired any more, or Bluetooth is off).");
            return sb.ToString();
        }
        sb.AppendLine($"Windows name: {_link!.Name}");
        sb.AppendLine($"Status: {(_link.Connected ? "Connected" : "Disconnected")}");
        sb.AppendLine("Devnodes (battery value):");
        foreach (var (id, v) in BatteryReader.ReadAll(_dev.Address))
            sb.AppendLine($"  {id}  ->  {(v?.ToString() ?? "-")}");
        if (_link.IsLowEnergy && _link.Connected)
            sb.AppendLine($"GATT battery: {(await _link.GattBatteryAsync())?.ToString() ?? "-"}");
        sb.AppendLine("Active audio endpoints:");
        sb.Append(await AudioProbe.DescribeAsync(_link.Name));
        return sb.ToString();
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Exchange(ref _link, null)?.Dispose();
    }
}
