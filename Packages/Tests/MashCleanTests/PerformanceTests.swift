import FileSystemKit
import Foundation
import XCTest

/// Đo hiệu năng duyệt và đo dung lượng (mục 16.1, 18). Fixture nhỏ để chạy được trong CI thường;
/// bản 1 triệu file chạy hằng đêm với `MASHCLEAN_PERF_FILES`.
final class WalkPerformanceTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        let count = Int(ProcessInfo.processInfo.environment["MASHCLEAN_PERF_FILES"] ?? "") ?? 3_000
        root = FileManager.default.temporaryDirectory.appendingPathComponent("mashclean-perf-\(UUID().uuidString)")
        let payload = Data(repeating: 0x41, count: 512)
        for i in 0..<count {
            let dir = root.appendingPathComponent("d\(i % 50)/s\(i % 7)")
            if i < 350 { try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true) }
            try payload.write(to: dir.appendingPathComponent("f\(i)"))
        }
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    func testMeasureDirectory() throws {
        let fs = FileSystemService(home: root)
        var items = 0
        measure(metrics: [XCTClockMetric(), XCTMemoryMetric()]) {
            items = (try? fs.measure(root).itemCount) ?? 0
        }
        XCTAssertGreaterThan(items, 0)
    }

    func testBulkWalkerVsFTS() throws {
        var bulk = 0, fts = 0
        try BulkWalker().walkSync(root, options: .default) { _ in bulk += 1; return .continue }
        try FTSWalker().walkSync(root, options: .default) { _ in fts += 1; return .continue }
        XCTAssertEqual(bulk, fts)
        measure(metrics: [XCTClockMetric()]) {
            var n = 0
            try? BulkWalker().walkSync(root, options: .default) { _ in n += 1; return .continue }
        }
    }
}
