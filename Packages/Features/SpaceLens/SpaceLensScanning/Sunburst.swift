import Foundation

/// Gộp các mục nhỏ hơn ngưỡng (mặc định 0,5% vùng cha) thành "Các mục nhỏ khác" (mục 11.4).
public enum SmallItemGrouping {
    public static let defaultThreshold = 0.005

    public struct Result<ID: Hashable & Sendable>: Sendable {
        /// Mục đủ lớn để vẽ riêng, sắp giảm dần theo dung lượng.
        public var visible: [(id: ID, size: UInt64)]
        public var otherSize: UInt64
        public var otherCount: Int
    }

    public static func group<ID: Hashable & Sendable>(_ items: [(id: ID, size: UInt64)], total: UInt64,
                                                      threshold: Double = defaultThreshold) -> Result<ID> {
        let sorted = items.sorted { $0.size > $1.size }
        guard total > 0 else { return Result(visible: [], otherSize: 0, otherCount: items.count) }
        let minSize = Double(total) * threshold
        var visible: [(id: ID, size: UInt64)] = []
        var otherSize: UInt64 = 0
        var otherCount = 0
        for item in sorted {
            if Double(item.size) >= minSize && item.size > 0 {
                visible.append(item)
            } else {
                otherSize &+= item.size
                otherCount += 1
            }
        }
        return Result(visible: visible, otherSize: otherSize, otherCount: otherCount)
    }
}

/// Một cung trên biểu đồ sunburst. Góc tính bằng radian, 0 ở hướng 12 giờ, tăng theo chiều kim đồng hồ.
public struct SunburstSegment: Sendable, Hashable, Identifiable {
    public enum Target: Sendable, Hashable {
        case node(Int)
        /// "Các mục nhỏ khác" bên dưới node cha.
        case others(parent: Int, count: Int)
    }

    public let id: Int
    public let target: Target
    /// Vòng thứ mấy tính từ tâm (1 = con trực tiếp của thư mục đang xem).
    public let depth: Int
    public let startAngle: Double
    public let endAngle: Double
    public let size: UInt64
    /// Thứ tự của nhánh cấp 1 chứa cung này, dùng để chọn màu.
    public let branch: Int

    public var midAngle: Double { (startAngle + endAngle) / 2 }
    public var span: Double { endAngle - startAngle }

    public var nodeIndex: Int? {
        if case let .node(i) = target { return i }
        return nil
    }
}

/// Tính bố cục sunburst và hit-test (hàm thuần, test được).
public enum SunburstLayout {
    public static let fullCircle = 2 * Double.pi

    /// - Parameters:
    ///   - center: node đang xem (ở tâm).
    ///   - maxDepth: số vòng tối đa.
    ///   - threshold: tỉ lệ tối thiểu so với vùng cha để vẽ riêng.
    ///   - minAngle: cung hẹp hơn (radian) không vẽ riêng mà gộp vào "Các mục nhỏ khác".
    public static func layout(tree: DiskTree, center: Int, maxDepth: Int = 4,
                              threshold: Double = SmallItemGrouping.defaultThreshold, minAngle: Double = 0.003) -> [SunburstSegment] {
        guard tree.size(of: center) > 0 else { return [] }
        var segments: [SunburstSegment] = []
        var nextID = 0

        func visit(_ node: Int, depth: Int, start: Double, span: Double, branch: Int) {
            let parentSize = tree.size(of: node)
            guard parentSize > 0, depth <= maxDepth else { return }
            let kids = tree.children(of: node).map { (id: $0, size: tree.size(of: $0)) }
            let grouped = SmallItemGrouping.group(kids, total: parentSize, threshold: threshold)
            var angle = start
            var otherSize = grouped.otherSize
            var otherCount = grouped.otherCount
            for (i, item) in grouped.visible.enumerated() {
                let width = span * Double(item.size) / Double(parentSize)
                if width < minAngle {
                    otherSize &+= item.size
                    otherCount += 1
                    continue
                }
                let b = depth == 1 ? i : branch
                segments.append(SunburstSegment(id: nextID, target: .node(item.id), depth: depth, startAngle: angle,
                                                endAngle: angle + width, size: item.size, branch: b))
                nextID += 1
                if tree.isDirectory(item.id) { visit(item.id, depth: depth + 1, start: angle, span: width, branch: b) }
                angle += width
            }
            if otherSize > 0 {
                let width = span * Double(otherSize) / Double(parentSize)
                if width >= minAngle / 4 {
                    segments.append(SunburstSegment(id: nextID, target: .others(parent: node, count: otherCount), depth: depth,
                                                    startAngle: angle, endAngle: angle + width, size: otherSize, branch: -1))
                    nextID += 1
                }
            }
        }

        visit(center, depth: 1, start: 0, span: fullCircle, branch: 0)
        return segments
    }

    /// Toạ độ cực của điểm (dx, dy) so với tâm, trục y hướng xuống (toạ độ màn hình).
    public static func polar(dx: Double, dy: Double) -> (angle: Double, radius: Double) {
        var angle = atan2(dx, -dy)
        if angle < 0 { angle += fullCircle }
        return (angle, (dx * dx + dy * dy).squareRoot())
    }

    /// Tìm cung chứa điểm. `nil` nếu ở vòng tâm hoặc ngoài biểu đồ.
    public static func hitTest(_ segments: [SunburstSegment], angle: Double, radius: Double,
                               innerRadius: Double, ringWidth: Double) -> SunburstSegment? {
        guard radius >= innerRadius, ringWidth > 0 else { return nil }
        let ring = Int((radius - innerRadius) / ringWidth) + 1
        return segments.first { $0.depth == ring && angle >= $0.startAngle && angle < $0.endAngle }
    }
}
