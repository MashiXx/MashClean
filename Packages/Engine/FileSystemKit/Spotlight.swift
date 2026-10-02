import CoreServices
import Foundation

/// Một kết quả Spotlight, đã chuyển sang kiểu Sendable.
public struct SpotlightItem: Sendable, Hashable {
    public let url: URL
    public let size: Int64
    public let lastUsed: Date?
    public let modified: Date?
    public let contentType: String?
    public let displayName: String?
    public let bundleID: String?
}

/// Bọc `NSMetadataQuery` thành async (mục 16.2, 22.3). Query chạy trên main run loop.
@MainActor
public final class SpotlightQuery {
    private let query = NSMetadataQuery()
    private var observer: (any NSObjectProtocol)?

    public init() {}

    /// Chạy một predicate trên các scope, trả kết quả khi Spotlight gom xong (hoặc khi hết timeout).
    public static func run(predicate: NSPredicate, scopes: [Any], timeout: TimeInterval = 20) async -> [SpotlightItem] {
        let q = SpotlightQuery()
        return await q.start(predicate: predicate, scopes: scopes, timeout: timeout)
    }

    public func start(predicate: NSPredicate, scopes: [Any], timeout: TimeInterval) async -> [SpotlightItem] {
        await withCheckedContinuation { (cont: CheckedContinuation<[SpotlightItem], Never>) in
            var finished = false
            query.predicate = predicate
            query.searchScopes = scopes
            query.valueListAttributes = []
            let finish: @MainActor () -> Void = { [weak self] in
                guard let self, !finished else { return }
                finished = true
                self.query.disableUpdates()
                self.query.stop()
                let items = self.collect()
                if let observer = self.observer { NotificationCenter.default.removeObserver(observer) }
                cont.resume(returning: items)
            }
            observer = NotificationCenter.default.addObserver(forName: .NSMetadataQueryDidFinishGathering, object: query, queue: .main) { _ in
                MainActor.assumeIsolated { finish() }
            }
            if !query.start() {
                finish()
                return
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + timeout) {
                MainActor.assumeIsolated { finish() }
            }
        }
    }

    private func collect() -> [SpotlightItem] {
        var items: [SpotlightItem] = []
        items.reserveCapacity(query.resultCount)
        for i in 0..<query.resultCount {
            guard let item = query.result(at: i) as? NSMetadataItem else { continue }
            let path = item.value(forAttribute: NSMetadataItemPathKey) as? String
            guard let path else { continue }
            items.append(SpotlightItem(
                url: URL(fileURLWithPath: path),
                size: (item.value(forAttribute: NSMetadataItemFSSizeKey) as? NSNumber)?.int64Value ?? 0,
                lastUsed: item.value(forAttribute: "kMDItemLastUsedDate") as? Date,
                modified: item.value(forAttribute: NSMetadataItemFSContentChangeDateKey) as? Date,
                contentType: item.value(forAttribute: NSMetadataItemContentTypeKey) as? String,
                displayName: item.value(forAttribute: NSMetadataItemDisplayNameKey) as? String,
                bundleID: item.value(forAttribute: "kMDItemCFBundleIdentifier") as? String
            ))
        }
        return items
    }

    /// Lần mở cuối của một file/app (`kMDItemLastUsedDate`), đọc trực tiếp qua MDItem, không cần query.
    nonisolated public static func lastUsedDate(of url: URL) -> Date? {
        guard let item = MDItemCreateWithURL(kCFAllocatorDefault, url as CFURL) else { return nil }
        return MDItemCopyAttribute(item, kMDItemLastUsedDate) as? Date
    }
}

/// Theo dõi thay đổi thư mục bằng FSEvents (mục 11.4, 22.3). Độ trễ 1 giây gom nhiều thay đổi thành một lần.
public final class DirectoryWatcher: @unchecked Sendable {
    private var stream: FSEventStreamRef?
    private let queue = DispatchQueue(label: "com.mashclean.fsevents", qos: .utility)
    private var box: Unmanaged<CallbackBox>?

    public init() {}

    deinit { stop() }

    public func start(paths: [String], latency: TimeInterval = 1.0, onChange: @escaping @Sendable ([String]) -> Void) {
        stop()
        let retained = Unmanaged.passRetained(CallbackBox(onChange))
        box = retained
        var ctx = FSEventStreamContext(version: 0, info: retained.toOpaque(), retain: nil, release: nil, copyDescription: nil)
        let callback: FSEventStreamCallback = { _, info, count, paths, _, _ in
            guard let info else { return }
            let box = Unmanaged<CallbackBox>.fromOpaque(info).takeUnretainedValue()
            let list = unsafeBitCast(paths, to: NSArray.self) as? [String] ?? []
            box.handler(Array(list.prefix(count)))
        }
        guard let s = FSEventStreamCreate(nil, callback, &ctx, paths as CFArray,
                                          FSEventStreamEventId(kFSEventStreamEventIdSinceNow), latency,
                                          FSEventStreamCreateFlags(kFSEventStreamCreateFlagUseCFTypes | kFSEventStreamCreateFlagNoDefer | kFSEventStreamCreateFlagWatchRoot))
        else { return }
        stream = s
        FSEventStreamSetDispatchQueue(s, queue)
        FSEventStreamStart(s)
    }

    public func stop() {
        if let s = stream {
            FSEventStreamStop(s)
            FSEventStreamInvalidate(s)
            FSEventStreamRelease(s)
            stream = nil
        }
        box?.release()
        box = nil
    }
}

final class CallbackBox: @unchecked Sendable {
    let handler: @Sendable ([String]) -> Void
    init(_ h: @escaping @Sendable ([String]) -> Void) { handler = h }
}
