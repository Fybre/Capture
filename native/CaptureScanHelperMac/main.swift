// CaptureScanHelperMac — signed ImageCaptureCore bridge for Capture.App.

import AppKit
import CoreGraphics
import Foundation
import ImageCaptureCore
import ImageIO

let arguments = CommandLine.arguments

func printJSON(_ value: Any) {
    guard let data = try? JSONSerialization.data(withJSONObject: value) else { return }
    FileHandle.standardOutput.write(data)
    FileHandle.standardOutput.write("\n".data(using: .utf8)!)
}

func fail(_ message: String) -> Never {
    printJSON(["error": message])
    exit(1)
}

final class DeviceLister: NSObject, NSApplicationDelegate, ICDeviceBrowserDelegate, ICScannerDeviceDelegate {
    let browser = ICDeviceBrowser()
    var devices: [[String: Any]] = []
    var pending: [ObjectIdentifier: ICScannerDevice] = [:]
    var basics: [ObjectIdentifier: (id: String, name: String)] = [:]
    var provisional: [ObjectIdentifier: [String: Any]] = [:]
    var enumerationComplete = false
    var finished = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        browser.delegate = self
        let mask = ICDeviceTypeMask.scanner.rawValue | ICDeviceLocationTypeMask.local.rawValue
        browser.browsedDeviceTypeMask = ICDeviceTypeMask(rawValue: mask)!
        browser.start()
        DispatchQueue.main.asyncAfter(deadline: .now() + 6.0) { self.finishWithFallbacks() }
    }

    func deviceBrowser(_ browser: ICDeviceBrowser, didAdd device: ICDevice, moreComing: Bool) {
        guard let scanner = device as? ICScannerDevice else { return }
        let key = ObjectIdentifier(scanner)
        pending[key] = scanner
        basics[key] = (device.uuidString ?? device.name ?? "unknown", device.name ?? "Unknown scanner")
        scanner.delegate = self
        scanner.requestOpenSession()
        if !moreComing {
            enumerationComplete = true
            maybeFinish()
        }
    }

    func device(_ device: ICDevice, didOpenSessionWithError error: Error?) {
        if error != nil, let scanner = device as? ICScannerDevice {
            finalize(scanner, capabilities: fallbackCapabilities(scanner))
        }
    }

    func deviceDidBecomeReady(_ device: ICDevice) {
        guard let scanner = device as? ICScannerDevice else { return }
        let types = Set(scanner.availableFunctionalUnitTypes.map { $0.intValue })
        let unit = scanner.selectedFunctionalUnit
        let resolutionSet = unit.preferredResolutions.count > 0
            ? unit.preferredResolutions
            : unit.supportedResolutions
        let key = ObjectIdentifier(scanner)
        let basic = basics[key] ?? (device.uuidString ?? "unknown", device.name ?? "Unknown scanner")
        let capabilities: [String: Any] = [
            "id": basic.id,
            "name": basic.name,
            "supportedDpis": resolutionSet.map { Int($0) },
            "supportsFlatbed": types.contains(Int(ICScannerFunctionalUnitType.flatbed.rawValue)) || unit.type == .flatbed,
            "supportsFeeder": types.contains(Int(ICScannerFunctionalUnitType.documentFeeder.rawValue)),
            "supportsDuplex": (unit as? ICScannerFunctionalUnitDocumentFeeder)?.supportsDuplexScanning ?? false,
            "supportsColor": true,
            "supportsGrayscale": true
        ]
        if (capabilities["supportsFeeder"] as? Bool) == true && unit.type != .documentFeeder {
            provisional[key] = capabilities
            scanner.requestSelect(.documentFeeder)
        } else {
            finalize(scanner, capabilities: capabilities)
        }
    }

    func scannerDevice(_ scanner: ICScannerDevice, didSelect unit: ICScannerFunctionalUnit, error: Error?) {
        let key = ObjectIdentifier(scanner)
        var capabilities = provisional.removeValue(forKey: key) ?? fallbackCapabilities(scanner)
        if error == nil, let feeder = unit as? ICScannerFunctionalUnitDocumentFeeder {
            capabilities["supportsDuplex"] = feeder.supportsDuplexScanning
            let set = feeder.preferredResolutions.count > 0 ? feeder.preferredResolutions : feeder.supportedResolutions
            let existing = capabilities["supportedDpis"] as? [Int] ?? []
            capabilities["supportedDpis"] = Array(Set(existing + set.map { Int($0) })).sorted()
        }
        finalize(scanner, capabilities: capabilities)
    }

    func fallbackCapabilities(_ scanner: ICScannerDevice) -> [String: Any] {
        let key = ObjectIdentifier(scanner)
        let basic = basics[key] ?? (scanner.uuidString ?? "unknown", scanner.name ?? "Unknown scanner")
        return [
            "id": basic.id, "name": basic.name, "supportedDpis": [],
            "supportsFlatbed": true, "supportsFeeder": false, "supportsDuplex": false,
            "supportsColor": true, "supportsGrayscale": true
        ]
    }

    func finalize(_ scanner: ICScannerDevice, capabilities: [String: Any]) {
        let key = ObjectIdentifier(scanner)
        guard pending.removeValue(forKey: key) != nil else { return }
        provisional.removeValue(forKey: key)
        devices.append(capabilities)
        if scanner.hasOpenSession { scanner.requestCloseSession() }
        maybeFinish()
    }

    func maybeFinish() {
        guard enumerationComplete && pending.isEmpty else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { self.finish() }
    }

    func deviceBrowser(_ browser: ICDeviceBrowser, didRemove device: ICDevice, moreGoing: Bool) {}

    func device(_ device: ICDevice, didCloseSessionWithError error: Error?) {}
    func didRemove(_ device: ICDevice) {}

    func finishWithFallbacks() {
        for scanner in pending.values {
            devices.append(fallbackCapabilities(scanner))
            if scanner.hasOpenSession { scanner.requestCloseSession() }
        }
        pending.removeAll()
        finish()
    }

    func finish() {
        guard !finished else { return }
        finished = true
        printJSON(devices)
        exit(0)
    }
}

final class Scanner: NSObject, NSApplicationDelegate, ICDeviceBrowserDelegate, ICScannerDeviceDelegate {
    let browser = ICDeviceBrowser()
    let targetDeviceId: String
    let requestedDpi: Int
    let outputPath: String
    let isGray: Bool
    let source: ICScannerFunctionalUnitType
    let wantsDuplex: Bool

    var scanner: ICScannerDevice?
    var pages: [[String: Any]] = []
    var timeoutWorkItem: DispatchWorkItem?
    var pendingPayload: [String: Any]?
    var pendingExitCode: Int32 = 0
    var finishing = false
    var scanScheduled = false

    init(targetDeviceId: String, requestedDpi: Int, outputPath: String, isGray: Bool,
         source: ICScannerFunctionalUnitType, wantsDuplex: Bool) {
        self.targetDeviceId = targetDeviceId
        self.requestedDpi = requestedDpi
        self.outputPath = outputPath
        self.isGray = isGray
        self.source = source
        self.wantsDuplex = wantsDuplex
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        browser.delegate = self
        let mask = ICDeviceTypeMask.scanner.rawValue | ICDeviceLocationTypeMask.local.rawValue
        browser.browsedDeviceTypeMask = ICDeviceTypeMask(rawValue: mask)!
        browser.start()
        scheduleTimeout(seconds: 20, message: "Timed out waiting for the selected scanner to appear.")
    }

    func deviceBrowser(_ browser: ICDeviceBrowser, didAdd device: ICDevice, moreComing: Bool) {
        guard scanner == nil else { return }
        let id = device.uuidString ?? device.name ?? ""
        guard id == targetDeviceId, let found = device as? ICScannerDevice else { return }
        timeoutWorkItem?.cancel()
        scanner = found
        found.delegate = self
        found.requestOpenSession()
        scheduleTimeout(seconds: 20, message: "Timed out opening the scanner session.")
    }

    func deviceBrowser(_ browser: ICDeviceBrowser, didRemove device: ICDevice, moreGoing: Bool) {
        guard device === scanner, !finishing else { return }
        finish(error: "The scanner was disconnected.")
    }

    func device(_ device: ICDevice, didOpenSessionWithError error: Error?) {
        if let error { finish(error: "Could not open a session with the scanner: \(error.localizedDescription)") }
    }

    func deviceDidBecomeReady(_ device: ICDevice) {
        guard let scanner else { return }
        if scanner.selectedFunctionalUnit.type != source {
            guard scanner.availableFunctionalUnitTypes.contains(NSNumber(value: source.rawValue)) else {
                finish(error: source == .documentFeeder
                    ? "The selected scanner has no document feeder."
                    : "The selected scanner has no flatbed unit.")
                return
            }
            scanner.requestSelect(source)
        } else {
            beginAfterDriverSettles()
        }
    }

    func scannerDevice(_ scanner: ICScannerDevice, didSelect functionalUnit: ICScannerFunctionalUnit,
                       error: Error?) {
        if let error {
            finish(error: "Could not select the scan source: \(error.localizedDescription)")
            return
        }
        beginAfterDriverSettles()
    }

    func beginAfterDriverSettles() {
        guard !scanScheduled else { return }
        scanScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.0) { self.startScan() }
    }

    func startScan() {
        guard let scanner, !finishing else { return }
        let unit = scanner.selectedFunctionalUnit
        let supported = unit.supportedResolutions.map { Int($0) }
        let effectiveDpi = supported.min(by: {
            abs($0 - requestedDpi) < abs($1 - requestedDpi)
        }) ?? requestedDpi

        unit.scanArea = CGRect(origin: .zero, size: unit.physicalSize)
        unit.resolution = effectiveDpi
        unit.pixelDataType = isGray ? .gray : .RGB
        unit.bitDepth = .depth8Bits

        if let feeder = unit as? ICScannerFunctionalUnitDocumentFeeder {
            if wantsDuplex && !feeder.supportsDuplexScanning {
                finish(error: "The selected document feeder does not support duplex scanning.")
                return
            }
            feeder.duplexScanningEnabled = wantsDuplex
        }

        let requestedURL = URL(fileURLWithPath: outputPath)
        scanner.downloadsDirectory = requestedURL.deletingLastPathComponent()
        scanner.documentName = requestedURL.deletingPathExtension().lastPathComponent
        scanner.documentUTI = "public.png"
        scanner.transferMode = .fileBased
        scheduleTimeout(
            seconds: max(120, min(900, Double(effectiveDpi) * 0.5)),
            message: "Timed out waiting for the scan to complete.")
        scanner.requestScan()
    }

    func scannerDevice(_ scanner: ICScannerDevice, didScanTo url: URL) {
        guard !finishing else { return }
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int else {
            try? FileManager.default.removeItem(at: url)
            finish(error: "Could not decode the image returned by the scanner.")
            return
        }
        pages.append([
            "path": url.path,
            "width": width,
            "height": height,
            "dpi": scanner.selectedFunctionalUnit.resolution
        ])
    }

    func scannerDevice(_ scanner: ICScannerDevice, didCompleteScanWithError error: Error?) {
        if let error {
            finish(error: "Scan failed: \(error.localizedDescription)")
        } else if pages.isEmpty {
            finish(error: "Scan completed but produced no image file.")
        } else {
            finish(payload: ["pages": pages])
        }
    }

    func scheduleTimeout(seconds: Double, message: String) {
        timeoutWorkItem?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.finish(error: message) }
        timeoutWorkItem = work
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: work)
    }

    func finish(error message: String) {
        for page in pages {
            if let path = page["path"] as? String { try? FileManager.default.removeItem(atPath: path) }
        }
        pages.removeAll()
        finish(payload: ["error": message], exitCode: 1)
    }

    func finish(payload: [String: Any], exitCode: Int32 = 0) {
        guard !finishing else { return }
        finishing = true
        timeoutWorkItem?.cancel()
        pendingPayload = payload
        pendingExitCode = exitCode
        if let scanner, scanner.hasOpenSession {
            if exitCode != 0 { scanner.cancelScan() }
            scanner.requestCloseSession()
            DispatchQueue.main.asyncAfter(deadline: .now() + 3.0) { self.emitAndExit() }
        } else {
            emitAndExit()
        }
    }

    func emitAndExit() {
        guard let payload = pendingPayload else { return }
        pendingPayload = nil
        printJSON(payload)
        exit(pendingExitCode)
    }

    func device(_ device: ICDevice, didCloseSessionWithError error: Error?) { emitAndExit() }
    func didRemove(_ device: ICDevice) {}
}

guard arguments.count >= 2 else {
    fail("Usage: CaptureScanHelperMac list-devices | scan <deviceId> <dpi> <outputPath> <color|gray> <flatbed|feeder> <simplex|duplex>")
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)

switch arguments[1] {
case "list-devices":
    let delegate = DeviceLister()
    app.delegate = delegate
    app.run()
case "scan":
    guard arguments.count == 8,
          let dpi = Int(arguments[3]),
          ["color", "gray"].contains(arguments[5]),
          ["flatbed", "feeder"].contains(arguments[6]),
          ["simplex", "duplex"].contains(arguments[7]) else {
        fail("Usage: CaptureScanHelperMac scan <deviceId> <dpi> <outputPath> <color|gray> <flatbed|feeder> <simplex|duplex>")
    }
    let delegate = Scanner(
        targetDeviceId: arguments[2],
        requestedDpi: dpi,
        outputPath: arguments[4],
        isGray: arguments[5] == "gray",
        source: arguments[6] == "feeder" ? .documentFeeder : .flatbed,
        wantsDuplex: arguments[7] == "duplex")
    app.delegate = delegate
    app.run()
default:
    fail("Unknown command '\(arguments[1])'. Expected list-devices or scan.")
}
