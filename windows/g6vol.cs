// g6vol for Windows — make the volume slider reach the optical (S/PDIF) output of
// a USB device whose hardware volume doesn't, e.g. Sound BlasterX G6.
//
// The G6 reports a hardware volume control, so Windows moves the DAC's attenuator
// instead of scaling the audio itself. That attenuator only affects the analog
// outputs; S/PDIF always gets the full-level stream.
//
// Windows has no muting process tap like macOS, but every shared-mode stream goes
// through its session volume (the per-app sliders in the volume mixer), which the
// audio engine applies in software before the audio reaches the device. So g6vol
// mirrors the device's volume (in dB) into the session volume of every app playing
// to the device. Each app keeps its own mixer level, relative to the master volume.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class G6Vol
{
    static readonly string DeviceMatch = Environment.GetEnvironmentVariable("G6VOL_DEVICE") ?? "Sound BlasterX G6";
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "g6vol");
    static readonly string LogPath = Path.Combine(DataDir, "g6vol.log");
    static readonly string StorePath = Path.Combine(DataDir, "sessions.tsv");
    const string InstanceMutexName = @"Local\g6vol";
    const string StopEventName = @"Local\g6vol-stop";
    const long MaxLogBytes = 1 << 20;
    const int TickSeconds = 5;
    // Session volumes closer than this count as unchanged (mixer sliders move in 0.01 steps).
    const float Eps = 0.0005f;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Tags our own session volume changes, so the notifications they cause can be ignored.
    internal static Guid OurContext = new Guid("5f0c3e0a-7b1d-4e6a-9c1f-3a8e2b6d4c90");

    static bool debug;

    // MARK: - Logging

    static readonly object logLock = new object();
    static StreamWriter logWriter;

    static void Log(string msg)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", Inv) + " " + msg;
        lock (logLock)
        {
            if (logWriter == null) return;
            try { logWriter.WriteLine(line); } catch (IOException) { }
        }
    }

    static void RotateLogIfLarge()
    {
        lock (logLock)
        {
            try
            {
                if (logWriter != null && logWriter.BaseStream.Length < MaxLogBytes) return;
                if (logWriter != null) { logWriter.Dispose(); logWriter = null; }
                Directory.CreateDirectory(DataDir);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length >= MaxLogBytes)
                {
                    File.Delete(LogPath + ".old");
                    File.Move(LogPath, LogPath + ".old");
                }
                var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                logWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // MARK: - Signals from COM callbacks (any thread) to the main loop

    static readonly AutoResetEvent wake = new AutoResetEvent(false);
    static int volumeDirty;
    static int devicesDirty;

    internal static void VolumeChanged() { Interlocked.Exchange(ref volumeDirty, 1); wake.Set(); }
    internal static void DevicesChanged() { Interlocked.Exchange(ref devicesDirty, 1); wake.Set(); }

    // MARK: - Per-app levels
    //
    // Keyed by session identifier, which is stable per app and device across runs.
    //   Own      the app's own level (what the user set in the volume mixer)
    //   Applied  the session volume we last set: Own × Gain
    //   Gain     the master gain at that time
    //
    // Windows remembers each app's session volume and restores it when the app starts
    // again, so a scaled volume would come back looking like the app's own level. Apps
    // whose Applied differs from Own are saved to disk so that survives restarts of
    // g6vol too.

    class Level { public float Own, Applied, Gain; }

    static readonly Dictionary<string, Level> levels = new Dictionary<string, Level>();

    static void LoadStore()
    {
        if (!File.Exists(StorePath)) return;
        try
        {
            foreach (string line in File.ReadAllLines(StorePath))
            {
                string[] f = line.Split(new[] { '\t' }, 4);
                float own, applied, gain;
                if (f.Length == 4 &&
                    float.TryParse(f[0], NumberStyles.Float, Inv, out own) &&
                    float.TryParse(f[1], NumberStyles.Float, Inv, out applied) &&
                    float.TryParse(f[2], NumberStyles.Float, Inv, out gain))
                {
                    levels[f[3]] = new Level { Own = own, Applied = applied, Gain = gain };
                }
            }
        }
        catch (IOException e) { Log("couldn't read " + StorePath + ": " + e.Message); }
        catch (UnauthorizedAccessException e) { Log("couldn't read " + StorePath + ": " + e.Message); }
    }

    static void SaveStore()
    {
        var sb = new StringBuilder();
        foreach (var kv in levels)
        {
            Level l = kv.Value;
            if (Math.Abs(l.Applied - l.Own) <= Eps) continue;
            sb.Append(l.Own.ToString("R", Inv)).Append('\t')
              .Append(l.Applied.ToString("R", Inv)).Append('\t')
              .Append(l.Gain.ToString("R", Inv)).Append('\t')
              .Append(kv.Key).Append('\n');
        }
        try
        {
            string tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(StorePath)) File.Replace(tmp, StorePath, null);
            else File.Move(tmp, StorePath);
        }
        catch (IOException e) { Log("couldn't save " + StorePath + ": " + e.Message); }
        catch (UnauthorizedAccessException e) { Log("couldn't save " + StorePath + ": " + e.Message); }
    }

    // MARK: - Device

    const int ERender = 0;
    const int DeviceStateActive = 1;
    const int ClsCtxAll = 23;
    const int AudioSessionStateExpired = 2;
    const ushort VT_LPWSTR = 31;
    static readonly PROPERTYKEY PKEY_Device_FriendlyName =
        new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PROPVARIANT value);

    static IMMDeviceEnumerator enumerator;
    static DeviceEvents deviceEvents;

    static string deviceId;
    static IAudioEndpointVolume endpoint;
    static VolumeEvents volumeEvents;
    static IAudioSessionManager2 sessions;
    static SessionCreatedEvents sessionCreatedEvents;
    static float minDb;
    static float lastGain = -1;
    static bool waitingLogged;

    class Tracked { public IAudioSessionControl2 Control; public SessionEvents Events; }
    // Sessions we listen to, by session instance identifier.
    static readonly Dictionary<string, Tracked> tracked = new Dictionary<string, Tracked>();

    static T Activate<T>(IMMDevice device) where T : class
    {
        Guid iid = typeof(T).GUID;
        object instance;
        device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out instance);
        return (T)instance;
    }

    static string FriendlyName(IMMDevice device)
    {
        IPropertyStore props;
        device.OpenPropertyStore(0, out props);
        PROPERTYKEY key = PKEY_Device_FriendlyName;
        PROPVARIANT value;
        props.GetValue(ref key, out value);
        try { return value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    static IMMDevice FindDevice(out string id, out string name)
    {
        IMMDeviceCollection all;
        enumerator.EnumAudioEndpoints(ERender, DeviceStateActive, out all);
        int count;
        all.GetCount(out count);
        for (int i = 0; i < count; i++)
        {
            IMMDevice device;
            all.Item(i, out device);
            string n = FriendlyName(device);
            if (n == null || n.IndexOf(DeviceMatch, StringComparison.OrdinalIgnoreCase) < 0) continue;
            device.GetId(out id);
            name = n;
            return device;
        }
        id = null;
        name = null;
        return null;
    }

    static void EnsureEnumerator()
    {
        if (enumerator != null) return;
        enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        deviceEvents = new DeviceEvents();
        enumerator.RegisterEndpointNotificationCallback(deviceEvents);
    }

    static void ResetEnumerator()
    {
        if (enumerator != null && deviceEvents != null)
        {
            try { enumerator.UnregisterEndpointNotificationCallback(deviceEvents); } catch (Exception) { }
        }
        enumerator = null;
        deviceEvents = null;
    }

    static void Attach()
    {
        EnsureEnumerator();
        string id, name;
        IMMDevice device = FindDevice(out id, out name);
        if (device == null)
        {
            if (!waitingLogged) Log(DeviceMatch + " not connected, waiting");
            waitingLogged = true;
            return;
        }
        waitingLogged = false;

        endpoint = Activate<IAudioEndpointVolume>(device);
        float maxDb, stepDb;
        endpoint.GetVolumeRange(out minDb, out maxDb, out stepDb);
        volumeEvents = new VolumeEvents();
        endpoint.RegisterControlChangeNotify(volumeEvents);

        sessions = Activate<IAudioSessionManager2>(device);
        // New-session notifications only start once the session list has been enumerated.
        IAudioSessionEnumerator unused;
        sessions.GetSessionEnumerator(out unused);
        sessionCreatedEvents = new SessionCreatedEvents();
        sessions.RegisterSessionNotification(sessionCreatedEvents);

        deviceId = id;
        lastGain = -1;
        Log(string.Format(Inv, "running on \"{0}\" (hardware volume {1}..{2} dB)", name, minDb, maxDb));
        SyncSessions(ReadGain());
    }

    static void Detach()
    {
        foreach (Tracked t in tracked.Values)
        {
            try { t.Control.UnregisterAudioSessionNotification(t.Events); } catch (Exception) { }
        }
        tracked.Clear();
        if (sessions != null && sessionCreatedEvents != null)
        {
            try { sessions.UnregisterSessionNotification(sessionCreatedEvents); } catch (Exception) { }
        }
        if (endpoint != null && volumeEvents != null)
        {
            try { endpoint.UnregisterControlChangeNotify(volumeEvents); } catch (Exception) { }
        }
        sessions = null;
        sessionCreatedEvents = null;
        endpoint = null;
        volumeEvents = null;
        deviceId = null;
    }

    // MARK: - Volume

    static float ReadGain()
    {
        bool muted;
        float db;
        endpoint.GetMute(out muted);
        endpoint.GetMasterVolumeLevel(out db);
        // The G6 bottoms out at -64 dB; treat the floor as silence.
        float gain = (muted || db <= minDb + 0.1f) ? 0f : (float)Math.Pow(10, db / 20);
        if (gain != lastGain)
        {
            Log(string.Format(Inv, "volume: {0:F2} dB -> gain {1:F3}{2}", db, gain, muted ? " (muted)" : ""));
            lastGain = gain;
        }
        return gain;
    }

    // Sets every session on the device to its app's own level × gain.
    static void SyncSessions(float gain)
    {
        IAudioSessionEnumerator list;
        sessions.GetSessionEnumerator(out list);
        int count;
        list.GetCount(out count);
        var present = new HashSet<string>();
        var reconciled = new HashSet<string>();
        bool changed = false;

        for (int i = 0; i < count; i++)
        {
            IAudioSessionControl2 control;
            list.GetSession(i, out control);
            // One session going away mid-pass shouldn't stop the others; device-level
            // failures surface through the endpoint calls instead.
            try { changed |= SyncSession(control, gain, present, reconciled); }
            catch (Exception e) { if (debug) Log("session skipped: " + e.Message.Trim()); }
        }

        var gone = new List<string>();
        foreach (string instance in tracked.Keys) if (!present.Contains(instance)) gone.Add(instance);
        foreach (string instance in gone) Untrack(instance);

        if (changed) ScheduleSave();
    }

    // Returns whether the app's saved level changed.
    static bool SyncSession(IAudioSessionControl2 control, float gain, HashSet<string> present, HashSet<string> reconciled)
    {
        int state;
        control.GetState(out state);
        if (state == AudioSessionStateExpired) return false;
        string instance, app;
        control.GetSessionInstanceIdentifier(out instance);
        control.GetSessionIdentifier(out app);
        present.Add(instance);
        if (!tracked.ContainsKey(instance)) Track(instance, control);

        var volume = (ISimpleAudioVolume)control;
        float current;
        volume.GetMasterVolume(out current);

        bool changed = false;
        Level level;
        if (!levels.TryGetValue(app, out level))
        {
            // First time we see this app: its current volume is its own level.
            level = new Level { Own = current, Applied = current, Gain = 1 };
            levels[app] = level;
        }
        // An app can have several sessions; reconcile its level once per pass, from the first.
        if (reconciled.Add(app))
        {
            if (Math.Abs(current - level.Applied) > Eps)
            {
                // Changed by someone else (volume mixer, the app itself): that's the app's new own level.
                level.Own = level.Gain > 0 ? Math.Min(1f, current / level.Gain) : current;
                if (debug) Log(string.Format(Inv, "{0}: own level now {1:F3}", SessionName(control), level.Own));
            }
            float target = level.Own * gain;
            if (target != level.Applied || gain != level.Gain)
            {
                level.Applied = target;
                level.Gain = gain;
                changed = true;
            }
        }
        if (Math.Abs(current - level.Applied) > 1e-6f)
        {
            volume.SetMasterVolume(level.Applied, ref OurContext);
            if (debug) Log(string.Format(Inv, "{0}: {1:F3} -> {2:F3}", SessionName(control), current, level.Applied));
        }
        return changed;
    }

    static void Track(string instance, IAudioSessionControl2 control)
    {
        var events = new SessionEvents();
        control.RegisterAudioSessionNotification(events);
        tracked[instance] = new Tracked { Control = control, Events = events };
        if (debug) Log("session added: " + SessionName(control));
    }

    static void Untrack(string instance)
    {
        Tracked t = tracked[instance];
        tracked.Remove(instance);
        try { t.Control.UnregisterAudioSessionNotification(t.Events); } catch (Exception) { }
        if (debug) Log("session removed");
    }

    static string SessionName(IAudioSessionControl2 control)
    {
        try
        {
            if (control.IsSystemSoundsSession() == 0) return "system sounds";
            int pid;
            control.GetProcessId(out pid);
            using (var p = Process.GetProcessById(pid)) return p.ProcessName + " (" + pid + ")";
        }
        catch (Exception) { return "?"; }
    }

    // MARK: - Main loop (everything except the COM callbacks runs on this thread)

    static DateTime rebuildAt = DateTime.MaxValue;
    static DateTime saveAt = DateTime.MaxValue;

    static void ScheduleSave()
    {
        DateTime at = DateTime.UtcNow.AddSeconds(1);
        if (at < saveAt) saveAt = at;
    }

    static void ScheduleRebuild(int seconds)
    {
        DateTime at = DateTime.UtcNow.AddSeconds(seconds);
        if (at < rebuildAt) rebuildAt = at;
    }

    static void Rebuild()
    {
        Detach();
        try
        {
            Attach();
        }
        catch (Exception e)
        {
            Log("error: " + e.Message.Trim() + " — retrying in 5 s");
            Detach();
            ResetEnumerator();
            ScheduleRebuild(5);
        }
    }

    // Device list changed: rebuild only if our device is gone or a different one matches now.
    static void CheckDevice()
    {
        try
        {
            EnsureEnumerator();
            string id, name;
            if (endpoint != null && FindDevice(out id, out name) != null && id == deviceId)
            {
                SyncSessions(ReadGain());
                return;
            }
        }
        catch (Exception) { }
        Rebuild();
    }

    static void Sync()
    {
        if (endpoint == null) return;
        try
        {
            SyncSessions(ReadGain());
        }
        catch (Exception e)
        {
            Log("error: " + e.Message.Trim() + " — rebuilding");
            ScheduleRebuild(1);
        }
    }

    static void Run(WaitHandle stop)
    {
        var handles = new WaitHandle[] { stop, wake };
        Rebuild();
        DateTime nextTick = DateTime.UtcNow.AddSeconds(TickSeconds);
        while (true)
        {
            DateTime due = nextTick;
            if (rebuildAt < due) due = rebuildAt;
            if (saveAt < due) due = saveAt;
            double ms = (due - DateTime.UtcNow).TotalMilliseconds;
            if (WaitHandle.WaitAny(handles, (int)Math.Max(0, Math.Min(ms, int.MaxValue))) == 0) break;

            DateTime now = DateTime.UtcNow;
            // Device events come in bursts; settle for a second before looking.
            if (Interlocked.Exchange(ref devicesDirty, 0) != 0) ScheduleRebuild(1);
            if (now >= rebuildAt)
            {
                rebuildAt = DateTime.MaxValue;
                CheckDevice();
            }
            if (Interlocked.Exchange(ref volumeDirty, 0) != 0) Sync();
            if (now >= nextTick)
            {
                // Safety net for missed notifications (sleep/wake, audio service restarts…).
                nextTick = now.AddSeconds(TickSeconds);
                if (endpoint == null) { if (rebuildAt == DateTime.MaxValue) Rebuild(); }
                else Sync();
                RotateLogIfLarge();
            }
            if (now >= saveAt)
            {
                saveAt = DateTime.MaxValue;
                SaveStore();
            }
        }

        Log("stopping");
        if (endpoint != null)
        {
            // Hand every app its own level back, as if g6vol had never run.
            try { SyncSessions(1f); }
            catch (Exception e) { Log("couldn't restore app volumes: " + e.Message.Trim()); }
        }
        Detach();
        ResetEnumerator();
        SaveStore();
    }

    // Asks a running instance to stop, and waits until it has.
    static bool StopRunning()
    {
        EventWaitHandle stop;
        if (!EventWaitHandle.TryOpenExisting(StopEventName, out stop)) return true;
        using (stop)
        {
            Mutex instance;
            bool running = Mutex.TryOpenExisting(InstanceMutexName, out instance);
            stop.Set();
            if (!running) return true;
            using (instance)
            {
                try
                {
                    if (!instance.WaitOne(10000)) return false;
                }
                catch (AbandonedMutexException) { }
                instance.ReleaseMutex();
                return true;
            }
        }
    }

    [MTAThread]
    static int Main(string[] args)
    {
        debug = Array.IndexOf(args, "--debug") >= 0;
        if (Array.IndexOf(args, "--stop") >= 0) return StopRunning() ? 0 : 1;

        RotateLogIfLarge();
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("fatal: " + e.ExceptionObject);

        bool createdNew;
        using (var instance = new Mutex(true, InstanceMutexName, out createdNew))
        using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName))
        {
            if (!createdNew)
            {
                Log("already running, exiting");
                return 0;
            }
            // A stop request still pending from before we started was meant for a previous instance.
            stop.Reset();
            Log("g6vol starting (device match: \"" + DeviceMatch + "\")");
            LoadStore();
            Run(stop);
            Log("stopped");
            instance.ReleaseMutex();
        }
        return 0;
    }
}

// MARK: - COM callbacks: only flag work for the main loop, never call back into the audio APIs here.

class DeviceEvents : IMMNotificationClient
{
    public void OnDeviceStateChanged(string id, int state) { G6Vol.DevicesChanged(); }
    public void OnDeviceAdded(string id) { G6Vol.DevicesChanged(); }
    public void OnDeviceRemoved(string id) { G6Vol.DevicesChanged(); }
    public void OnDefaultDeviceChanged(int flow, int role, string id) { }
    public void OnPropertyValueChanged(string id, PROPERTYKEY key) { }
}

class VolumeEvents : IAudioEndpointVolumeCallback
{
    public void OnNotify(IntPtr data) { G6Vol.VolumeChanged(); }
}

class SessionCreatedEvents : IAudioSessionNotification
{
    public void OnSessionCreated(IntPtr session) { G6Vol.VolumeChanged(); }
}

class SessionEvents : IAudioSessionEvents
{
    public void OnDisplayNameChanged(string name, IntPtr context) { }
    public void OnIconPathChanged(string path, IntPtr context) { }
    public void OnSimpleVolumeChanged(float volume, bool mute, IntPtr context)
    {
        if (context == IntPtr.Zero || (Guid)Marshal.PtrToStructure(context, typeof(Guid)) != G6Vol.OurContext)
            G6Vol.VolumeChanged();
    }
    public void OnChannelVolumeChanged(int channelCount, IntPtr volumes, int changedChannel, IntPtr context) { }
    public void OnGroupingParamChanged(IntPtr grouping, IntPtr context) { }
    public void OnStateChanged(int state) { G6Vol.VolumeChanged(); }
    public void OnSessionDisconnected(int reason) { G6Vol.DevicesChanged(); }
}

// MARK: - Core Audio COM interfaces (vtable order matters)

#pragma warning disable 169, 649

[StructLayout(LayoutKind.Sequential)]
struct PROPERTYKEY { public Guid fmtid; public int pid; }

[StructLayout(LayoutKind.Sequential)]
struct PROPVARIANT { public ushort vt; ushort r1, r2, r3; public IntPtr pointer; IntPtr extra; }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    void GetCount(out int count);
    void Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    void OpenPropertyStore(int access, out IPropertyStore properties);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out int state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    void GetCount(out int count);
    void GetAt(int index, out PROPERTYKEY key);
    void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    void Commit();
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY key);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    void GetChannelCount(out int count);
    void SetMasterVolumeLevel(float db, ref Guid context);
    void SetMasterVolumeLevelScalar(float level, ref Guid context);
    void GetMasterVolumeLevel(out float db);
    void GetMasterVolumeLevelScalar(out float level);
    void SetChannelVolumeLevel(int channel, float db, ref Guid context);
    void SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
    void GetChannelVolumeLevel(int channel, out float db);
    void GetChannelVolumeLevelScalar(int channel, out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    void GetVolumeStepInfo(out int step, out int stepCount);
    void VolumeStepUp(ref Guid context);
    void VolumeStepDown(ref Guid context);
    void QueryHardwareSupport(out int mask);
    void GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolumeCallback
{
    void OnNotify(IntPtr data);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    // IAudioSessionManager
    void GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr control);
    void GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr volume);
    // IAudioSessionManager2
    void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    void RegisterSessionNotification(IAudioSessionNotification notification);
    void UnregisterSessionNotification(IAudioSessionNotification notification);
    void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
    void UnregisterDuckNotification(IntPtr notification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    void GetCount(out int count);
    void GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionNotification
{
    void OnSessionCreated(IntPtr session);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    // IAudioSessionControl
    void GetState(out int state);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
    void GetGroupingParam(out Guid grouping);
    void SetGroupingParam(ref Guid grouping, ref Guid context);
    void RegisterAudioSessionNotification(IAudioSessionEvents events);
    void UnregisterAudioSessionNotification(IAudioSessionEvents events);
    // IAudioSessionControl2
    void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetProcessId(out int pid);
    [PreserveSig] int IsSystemSoundsSession();
    void SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEvents
{
    void OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
    void OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
    void OnSimpleVolumeChanged(float volume, [MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
    void OnChannelVolumeChanged(int channelCount, IntPtr volumes, int changedChannel, IntPtr context);
    void OnGroupingParamChanged(IntPtr grouping, IntPtr context);
    void OnStateChanged(int state);
    void OnSessionDisconnected(int reason);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid context);
    void GetMasterVolume(out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
