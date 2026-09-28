// g6vol — software volume for a USB device whose hardware volume doesn't reach
// its S/PDIF (optical) output, e.g. Sound BlasterX G6.
//
// How it works:
//   1. A Core Audio process tap captures (and mutes) everything other apps send
//      to the device.
//   2. A private aggregate device (device + tap) runs an IOProc that copies the
//      tapped audio back to the device, scaled by the device's own volume/mute.
// So the macOS volume slider / keys keep driving the device's hardware volume,
// and we mirror that value digitally so it reaches the optical output too.

import AudioToolbox
import CoreAudio
import Foundation

let deviceMatch = ProcessInfo.processInfo.environment["G6VOL_DEVICE"] ?? "Sound BlasterX G6"
let debug = CommandLine.arguments.contains("--debug")

func log(_ msg: String) {
    let ts = ISO8601DateFormatter().string(from: Date())
    print("\(ts) \(msg)")
    fflush(stdout)
}

// MARK: - Core Audio property helpers

func addr(_ sel: AudioObjectPropertySelector,
          _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal,
          _ element: AudioObjectPropertyElement = kAudioObjectPropertyElementMain) -> AudioObjectPropertyAddress {
    AudioObjectPropertyAddress(mSelector: sel, mScope: scope, mElement: element)
}

func getValue<T: BitwiseCopyable>(_ obj: AudioObjectID, _ a: AudioObjectPropertyAddress, _ initial: T) -> T? {
    var a = a
    var value = initial
    var size = UInt32(MemoryLayout<T>.size)
    let err = AudioObjectGetPropertyData(obj, &a, 0, nil, &size, &value)
    return err == noErr ? value : nil
}

func getString(_ obj: AudioObjectID, _ sel: AudioObjectPropertySelector) -> String? {
    var a = addr(sel)
    var value: Unmanaged<CFString>?
    var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
    guard AudioObjectGetPropertyData(obj, &a, 0, nil, &size, &value) == noErr else { return nil }
    return value?.takeRetainedValue() as String?
}

func getArray<T: BitwiseCopyable>(_ obj: AudioObjectID, _ a: AudioObjectPropertyAddress, _ zero: T) -> [T] {
    var a = a
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(obj, &a, 0, nil, &size) == noErr, size > 0 else { return [] }
    var items = [T](repeating: zero, count: Int(size) / MemoryLayout<T>.size)
    guard AudioObjectGetPropertyData(obj, &a, 0, nil, &size, &items) == noErr else { return [] }
    return items
}

let systemObject = AudioObjectID(kAudioObjectSystemObject)

func findDevice() -> (id: AudioDeviceID, uid: String)? {
    for id in getArray(systemObject, addr(kAudioHardwarePropertyDevices), AudioDeviceID(0)) {
        guard let name = getString(id, kAudioObjectPropertyName), name.contains(deviceMatch) else { continue }
        let outStreams = getArray(id, addr(kAudioDevicePropertyStreams, kAudioObjectPropertyScopeOutput), AudioStreamID(0))
        guard !outStreams.isEmpty, let uid = getString(id, kAudioDevicePropertyDeviceUID) else { continue }
        return (id, uid)
    }
    return nil
}

// MARK: - Shared state between the control thread and the real-time IOProc
//
// Plain floats in a manually allocated block: the IOProc captures only this
// pointer, so no ARC traffic or locks happen on the audio thread.
//   [0], [1]  target gain L/R
//   [2], [3]  current (smoothed) gain L/R
//   [4...]    debug: peak per input buffer

let shared = UnsafeMutablePointer<Float>.allocate(capacity: 16)
shared.initialize(repeating: 0, count: 16)
// IOProc cycle counter, used by the watchdog to notice a stalled engine.
let heartbeat = UnsafeMutablePointer<UInt64>.allocate(capacity: 1)
heartbeat.initialize(to: 0)

func updateGain(device: AudioDeviceID) {
    let muted = getValue(device, addr(kAudioDevicePropertyMute, kAudioObjectPropertyScopeOutput), UInt32(0)) ?? 0
    for ch in 0..<2 {
        let element = AudioObjectPropertyElement(ch + 1)
        let db = getValue(device, addr(kAudioDevicePropertyVolumeDecibels, kAudioObjectPropertyScopeOutput, element), Float32(0))
            ?? getValue(device, addr(kAudioDevicePropertyVolumeDecibels, kAudioObjectPropertyScopeOutput), Float32(0))
            ?? 0
        // The device bottoms out at -64 dB; treat the floor as silence.
        let gain: Float = (muted != 0 || db <= -63.9) ? 0 : powf(10, db / 20)
        shared[ch] = gain
    }
    log(String(format: "volume: L %.3f  R %.3f%@", shared[0], shared[1], muted != 0 ? " (muted)" : ""))
}

// MARK: - TCC (system audio recording permission)
//
// Process taps need the "System Audio Recording" permission. If it's denied the
// tap would still mute other apps but deliver silence, so check first.

typealias TCCPreflightFn = @convention(c) (CFString, CFDictionary?) -> Int
typealias TCCRequestFn = @convention(c) (CFString, CFDictionary?, @escaping @convention(block) (Bool) -> Void) -> Void

func ensureAudioCapturePermission() -> Bool {
    guard let tcc = dlopen("/System/Library/PrivateFrameworks/TCC.framework/Versions/A/TCC", RTLD_NOW),
          let preflightSym = dlsym(tcc, "TCCAccessPreflight"),
          let requestSym = dlsym(tcc, "TCCAccessRequest") else {
        log("warning: can't load TCC, continuing without permission check")
        return true
    }
    let service = "kTCCServiceAudioCapture" as CFString
    let preflight = unsafeBitCast(preflightSym, to: TCCPreflightFn.self)
    switch preflight(service, nil) {
    case 0: return true
    case 1:
        log("System audio recording permission is denied. Enable g6vol in System Settings → Privacy & Security → Screen & System Audio Recording.")
        return false
    default:
        log("requesting system audio recording permission…")
        let request = unsafeBitCast(requestSym, to: TCCRequestFn.self)
        let done = DispatchSemaphore(value: 0)
        var granted = false
        request(service, nil) { ok in granted = ok; done.signal() }
        done.wait()
        log("permission \(granted ? "granted" : "denied")")
        return granted
    }
}

// MARK: - Engine

final class Engine {
    let deviceID: AudioDeviceID
    let deviceUID: String
    var tapID = AudioObjectID(kAudioObjectUnknown)
    var aggregateID = AudioObjectID(kAudioObjectUnknown)
    var procID: AudioDeviceIOProcID?

    init(deviceID: AudioDeviceID, deviceUID: String) {
        self.deviceID = deviceID
        self.deviceUID = deviceUID
    }

    func start() throws {
        // Exclude ourselves so our re-played audio isn't tapped (and muted) again.
        var pid = getpid()
        var selfObject = AudioObjectID(kAudioObjectUnknown)
        var a = addr(kAudioHardwarePropertyTranslatePIDToProcessObject)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        AudioObjectGetPropertyData(systemObject, &a, UInt32(MemoryLayout<pid_t>.size), &pid, &size, &selfObject)
        let excluded: [NSNumber] = selfObject == kAudioObjectUnknown ? [] : [NSNumber(value: selfObject)]

        let desc = CATapDescription(__excludingProcesses: excluded, andDeviceUID: deviceUID, withStream: 0)
        desc.name = "g6vol"
        desc.isPrivate = true
        desc.muteBehavior = .muted
        try check(AudioHardwareCreateProcessTap(desc, &tapID), "create tap")

        if let fmt = getValue(tapID, addr(kAudioTapPropertyFormat), AudioStreamBasicDescription()) {
            log("tap format: \(fmt.mSampleRate) Hz, \(fmt.mChannelsPerFrame) ch, flags \(fmt.mFormatFlags), \(fmt.mBitsPerChannel) bit")
            guard fmt.mFormatID == kAudioFormatLinearPCM, fmt.mFormatFlags & kAudioFormatFlagIsFloat != 0, fmt.mBitsPerChannel == 32 else {
                throw EngineError("unexpected tap format")
            }
        }

        let aggDesc: [String: Any] = [
            kAudioAggregateDeviceNameKey: "g6vol",
            kAudioAggregateDeviceUIDKey: "g6vol-" + UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: deviceUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: deviceUID]],
            kAudioAggregateDeviceTapListKey: [[
                kAudioSubTapUIDKey: desc.uuid.uuidString,
                kAudioSubTapDriftCompensationKey: true,
            ]],
        ]
        try check(AudioHardwareCreateAggregateDevice(aggDesc as CFDictionary, &aggregateID), "create aggregate")

        // Aggregate inputs = the device's own inputs (S/PDIF In on the G6) followed by the tap.
        let inStreams = getArray(aggregateID, addr(kAudioDevicePropertyStreams, kAudioObjectPropertyScopeInput), AudioStreamID(0))
        let devInStreams = getArray(deviceID, addr(kAudioDevicePropertyStreams, kAudioObjectPropertyScopeInput), AudioStreamID(0))
        let tapBuffer = devInStreams.count
        log("aggregate input streams: \(inStreams.count) (device \(devInStreams.count), tap at index \(tapBuffer))")
        guard tapBuffer < inStreams.count else { throw EngineError("tap stream not found in aggregate") }

        let state = shared
        let beat = heartbeat
        try check(AudioDeviceCreateIOProcIDWithBlock(&procID, aggregateID, nil) { _, inData, _, outData, _ in
            let ins = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: inData))
            let outs = UnsafeMutableAudioBufferListPointer(outData)
            beat.pointee &+= 1
            if debug {
                for (i, b) in ins.enumerated() where i < 12 {
                    guard let p = b.mData?.assumingMemoryBound(to: Float.self) else { continue }
                    var peak: Float = 0
                    for s in 0..<Int(b.mDataByteSize) / 4 { peak = max(peak, abs(p[s])) }
                    state[4 + i] = max(state[4 + i], peak)
                }
            }
            var channelBase = 0
            for k in 0..<outs.count {
                let ob = outs[k]
                guard let op = ob.mData?.assumingMemoryBound(to: Float.self) else { continue }
                let ch = max(Int(ob.mNumberChannels), 1)
                let outSamples = Int(ob.mDataByteSize) / 4
                let frames = outSamples / ch
                let inIndex = tapBuffer + k
                var ip: UnsafeMutablePointer<Float>? = nil
                var inSamples = 0
                var inCh = ch
                if inIndex < ins.count {
                    ip = ins[inIndex].mData?.assumingMemoryBound(to: Float.self)
                    inSamples = Int(ins[inIndex].mDataByteSize) / 4
                    inCh = max(Int(ins[inIndex].mNumberChannels), 1)
                }
                for c in 0..<ch {
                    let g = (channelBase + c) & 1
                    let from = state[2 + g]
                    let step = (state[g] - from) / Float(max(frames, 1))
                    var gain = from
                    for f in 0..<frames {
                        gain += step
                        let si = f * inCh + c
                        let sample: Float = (ip != nil && c < inCh && si < inSamples) ? ip![si] : 0
                        op[f * ch + c] = sample * gain
                    }
                }
                channelBase += ch
            }
            // Ramps above are per-buffer, so commit the targets once all buffers are written.
            state[2] = state[0]
            state[3] = state[1]

        }, "create IOProc")

        disableDeviceInputs(keepingStream: tapBuffer, of: inStreams.count)
        try check(AudioDeviceStart(aggregateID, procID), "start")
        log("running on \(deviceUID)")
    }

    // Mark the device's own input streams as unused so the G6's S/PDIF In isn't opened.
    func disableDeviceInputs(keepingStream tapIndex: Int, of count: Int) {
        guard let procID else { return }
        var a = addr(kAudioDevicePropertyIOProcStreamUsage, kAudioObjectPropertyScopeInput)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(aggregateID, &a, 0, nil, &size) == noErr else { return }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: 8)
        defer { raw.deallocate() }
        raw.storeBytes(of: unsafeBitCast(procID, to: UnsafeMutableRawPointer.self), as: UnsafeMutableRawPointer.self)
        guard AudioObjectGetPropertyData(aggregateID, &a, 0, nil, &size, raw) == noErr else { return }
        let n = Int(raw.load(fromByteOffset: MemoryLayout<UnsafeMutableRawPointer>.stride, as: UInt32.self))
        let flags = raw.advanced(by: MemoryLayout<AudioHardwareIOProcStreamUsage>.offset(of: \.mStreamIsOn)!)
            .assumingMemoryBound(to: UInt32.self)
        for i in 0..<n { flags[i] = i >= tapIndex ? 1 : 0 }
        let err = AudioObjectSetPropertyData(aggregateID, &a, 0, nil, size, raw)
        if err != noErr { log("note: couldn't disable device inputs (\(err))") }
    }

    func stop() {
        if let procID {
            AudioDeviceStop(aggregateID, procID)
            AudioDeviceDestroyIOProcID(aggregateID, procID)
        }
        procID = nil
        if aggregateID != kAudioObjectUnknown { AudioHardwareDestroyAggregateDevice(aggregateID) }
        aggregateID = AudioObjectID(kAudioObjectUnknown)
        if tapID != kAudioObjectUnknown { AudioHardwareDestroyProcessTap(tapID) }
        tapID = AudioObjectID(kAudioObjectUnknown)
    }
}

struct EngineError: Error, CustomStringConvertible {
    let description: String
    init(_ d: String) { description = d }
}

func check(_ err: OSStatus, _ what: String) throws {
    if err != noErr { throw EngineError("\(what) failed: \(err)") }
}

// MARK: - Lifecycle: follow the device appearing/disappearing and its volume

var engine: Engine?
var watchedDevice = AudioDeviceID(kAudioObjectUnknown)
var watchedRate: Float64 = 0
var rebuildPending = false

let volumeListener: AudioObjectPropertyListenerBlock = { _, _ in
    if watchedDevice != kAudioObjectUnknown { updateGain(device: watchedDevice) }
}
// Creating our own tap/aggregate also fires this, so only react when the target device itself changes.
let devicesListener: AudioObjectPropertyListenerBlock = { _, _ in
    if (findDevice()?.id ?? AudioDeviceID(kAudioObjectUnknown)) != watchedDevice { scheduleRebuild() }
}
let sampleRateListener: AudioObjectPropertyListenerBlock = { _, _ in
    if watchedDevice != kAudioObjectUnknown, nominalRate(watchedDevice) != watchedRate { scheduleRebuild() }
}

func nominalRate(_ device: AudioDeviceID) -> Float64 {
    getValue(device, addr(kAudioDevicePropertyNominalSampleRate), Float64(0)) ?? 0
}

func watchAddresses() -> [AudioObjectPropertyAddress] {
    [addr(kAudioDevicePropertyVolumeDecibels, kAudioObjectPropertyScopeOutput, 1),
     addr(kAudioDevicePropertyVolumeDecibels, kAudioObjectPropertyScopeOutput, 2),
     addr(kAudioDevicePropertyVolumeDecibels, kAudioObjectPropertyScopeOutput),
     addr(kAudioDevicePropertyMute, kAudioObjectPropertyScopeOutput)]
}

func setWatched(_ device: AudioDeviceID) {
    if watchedDevice != kAudioObjectUnknown {
        for var a in watchAddresses() { AudioObjectRemovePropertyListenerBlock(watchedDevice, &a, .main, volumeListener) }
        var sr = addr(kAudioDevicePropertyNominalSampleRate)
        AudioObjectRemovePropertyListenerBlock(watchedDevice, &sr, .main, sampleRateListener)
    }
    watchedDevice = device
    guard device != kAudioObjectUnknown else { return }
    for var a in watchAddresses() {
        if AudioObjectHasProperty(device, &a) { AudioObjectAddPropertyListenerBlock(device, &a, .main, volumeListener) }
    }
    var sr = addr(kAudioDevicePropertyNominalSampleRate)
    AudioObjectAddPropertyListenerBlock(device, &sr, .main, sampleRateListener)
}

func rebuild() {
    engine?.stop()
    engine = nil
    guard let dev = findDevice() else {
        setWatched(AudioDeviceID(kAudioObjectUnknown))
        log("\(deviceMatch) not connected, waiting")
        return
    }
    if dev.id != watchedDevice { setWatched(dev.id) }
    watchedRate = nominalRate(dev.id)
    updateGain(device: dev.id)
    shared[2] = shared[0]
    shared[3] = shared[1]
    let e = Engine(deviceID: dev.id, deviceUID: dev.uid)
    do {
        try e.start()
        engine = e
    } catch {
        log("error: \(error) — retrying in 5 s")
        e.stop()
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { scheduleRebuild() }
    }
}

func scheduleRebuild() {
    guard !rebuildPending else { return }
    rebuildPending = true
    DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
        rebuildPending = false
        rebuild()
    }
}

// MARK: - main

setvbuf(stdout, nil, _IOLBF, 0)
log("g6vol starting (device match: \"\(deviceMatch)\")")

guard ensureAudioCapturePermission() else { exit(0) }

var devicesAddr = addr(kAudioHardwarePropertyDevices)
AudioObjectAddPropertyListenerBlock(systemObject, &devicesAddr, .main, devicesListener)

// Watchdog: the aggregate runs continuously (even in silence), so a heartbeat that
// stops advancing means the engine died (sleep/wake, coreaudiod restart…).
var lastBeat: UInt64 = 0
Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { _ in
    let beat = heartbeat.pointee
    if engine != nil, beat == lastBeat {
        log("engine stalled, rebuilding")
        scheduleRebuild()
    }
    lastBeat = beat
}

for sig in [SIGTERM, SIGINT, SIGHUP] {
    signal(sig, SIG_IGN)
    let src = DispatchSource.makeSignalSource(signal: sig, queue: .main)
    src.setEventHandler {
        log("stopping")
        engine?.stop()
        exit(0)
    }
    src.resume()
    _ = Unmanaged.passRetained(src)
}

if debug {
    Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { _ in
        let peaks = (0..<6).map { String(format: "%.3f", shared[4 + $0]) }.joined(separator: " ")
        log("input peaks per buffer: \(peaks)  gain \(shared[2]) \(shared[3])")
        for i in 0..<12 { shared[4 + i] = 0 }
    }
}

rebuild()
RunLoop.main.run()
