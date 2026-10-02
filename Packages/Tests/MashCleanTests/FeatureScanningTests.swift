import DuplicatesScanning
import Foundation
import Testing

/// Hàm thuần của Duplicates (mục 11.8): nhóm theo dung lượng rồi theo hash.
@Suite struct DuplicateGroupingTests {
    func c(_ path: String, size: UInt64, id: UInt64, device: Int32 = 1) -> DuplicateCandidate {
        DuplicateCandidate(path: path, size: size, allocatedSize: size, device: device, fileID: id)
    }

    @Test func groupsBySizeSkippingSmallSinglesAndHardLinks() {
        let groups = DuplicateGrouping.bySize([
            c("/a", size: 2_000_000, id: 1), c("/b", size: 2_000_000, id: 2),
            c("/a-hardlink", size: 2_000_000, id: 1),            // cùng inode với /a
            c("/single", size: 3_000_000, id: 3),
            c("/tiny1", size: 10, id: 4), c("/tiny2", size: 10, id: 5),
            c("/x", size: 5_000_000, id: 6), c("/y", size: 5_000_000, id: 7), c("/z", size: 5_000_000, id: 8),
        ])
        #expect(groups.map { $0.map(\.path).sorted() } == [["/x", "/y", "/z"], ["/a", "/b"]])
    }

    @Test func regroupByKeyDropsUnreadable() {
        let group = [c("/1", size: 5, id: 1), c("/2", size: 5, id: 2), c("/3", size: 5, id: 3), c("/4", size: 5, id: 4)]
        let keys = ["/1": "h", "/2": "h", "/3": "other"]
        let result = DuplicateGrouping.regroup([group]) { keys[$0.path] }
        #expect(result.map { $0.map(\.path) } == [["/1", "/2"]])
    }
}
