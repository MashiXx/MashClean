import AppKit
import SwiftUI
import SweepCore

/// Bảng màu và kiểu chữ của MashClean.
public enum Theme {
    // Nền gradient của cửa sổ chính, theo từng feature để người dùng biết đang ở đâu.
    public static func background(for accent: Accent) -> LinearGradient {
        LinearGradient(colors: accent.gradient, startPoint: .topLeading, endPoint: .bottomTrailing)
    }

    public enum Accent: String, CaseIterable, Sendable {
        case smartScan, cleanup, applications, spaceLens, maintenance, protection, files, neutral

        public var gradient: [Color] {
            switch self {
            case .smartScan: [Color(hex: 0x3A1C71), Color(hex: 0x6A3093), Color(hex: 0xA044FF)]
            case .cleanup: [Color(hex: 0x0F3D3E), Color(hex: 0x11706A), Color(hex: 0x2BB39B)]
            case .applications: [Color(hex: 0x1A2A6C), Color(hex: 0x2B5DC5), Color(hex: 0x46A3FF)]
            case .spaceLens: [Color(hex: 0x23074D), Color(hex: 0x6B1D9C), Color(hex: 0xCC5333)]
            case .maintenance: [Color(hex: 0x3E1E0E), Color(hex: 0x9A4512), Color(hex: 0xF09819)]
            case .protection: [Color(hex: 0x0B3D20), Color(hex: 0x16753E), Color(hex: 0x56AB2F)]
            case .files: [Color(hex: 0x13213A), Color(hex: 0x24477A), Color(hex: 0x4E7BB8)]
            case .neutral: [Color(hex: 0x1E1E2A), Color(hex: 0x2A2A3C), Color(hex: 0x3A3A52)]
            }
        }

        public var tint: Color { gradient.last ?? .accentColor }
    }

    public static let cardBackground = Color.white.opacity(0.08)
    public static let cardStroke = Color.white.opacity(0.12)
    public static let primaryText = Color.white
    public static let secondaryText = Color.white.opacity(0.72)
    public static let tertiaryText = Color.white.opacity(0.5)
    public static let safe = Color(hex: 0x4CD964)
    public static let review = Color(hex: 0xFFCC00)
    public static let risky = Color(hex: 0xFF5E57)

    public static func color(for safety: SafetyLevel) -> Color {
        switch safety {
        case .safe: safe
        case .review: review
        case .risky: risky
        }
    }

    public enum Font {
        public static let hero = SwiftUI.Font.system(size: 40, weight: .bold, design: .rounded)
        public static let title = SwiftUI.Font.system(size: 26, weight: .bold, design: .rounded)
        public static let headline = SwiftUI.Font.system(size: 15, weight: .semibold)
        public static let body = SwiftUI.Font.system(size: 13)
        public static let caption = SwiftUI.Font.system(size: 11)
        public static let number = SwiftUI.Font.system(size: 34, weight: .heavy, design: .rounded).monospacedDigit()
    }
}

extension Color {
    public init(hex: UInt32, alpha: Double = 1) {
        self.init(.sRGB,
                  red: Double((hex >> 16) & 0xFF) / 255,
                  green: Double((hex >> 8) & 0xFF) / 255,
                  blue: Double(hex & 0xFF) / 255,
                  opacity: alpha)
    }
}
