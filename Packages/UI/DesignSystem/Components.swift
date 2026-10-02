import AppKit
import SwiftUI
import SweepCore

/// Nút tròn lớn ở giữa màn hình (Quét / Dọn dẹp / Chạy).
public struct BigActionButton: View {
    let title: String
    let subtitle: String?
    let accent: Theme.Accent
    let action: () -> Void
    @State private var hovering = false

    public init(_ title: String, subtitle: String? = nil, accent: Theme.Accent, action: @escaping () -> Void) {
        self.title = title
        self.subtitle = subtitle
        self.accent = accent
        self.action = action
    }

    public var body: some View {
        Button(action: action) {
            ZStack {
                Circle()
                    .fill(LinearGradient(colors: [Color.white.opacity(0.35), Color.white.opacity(0.12)], startPoint: .top, endPoint: .bottom))
                    .overlay(Circle().stroke(Color.white.opacity(0.5), lineWidth: 1.5))
                    .shadow(color: accent.tint.opacity(0.6), radius: hovering ? 24 : 14)
                VStack(spacing: 2) {
                    Text(title).font(.system(size: 20, weight: .bold, design: .rounded))
                    if let subtitle { Text(subtitle).font(.system(size: 11)).opacity(0.8) }
                }
                .foregroundStyle(.white)
            }
            .frame(width: 112, height: 112)
            .scaleEffect(hovering ? 1.04 : 1)
            .animation(.spring(response: 0.3), value: hovering)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .keyboardShortcut(.defaultAction)
        .accessibilityLabel(title)
    }
}

/// Thẻ nền mờ.
public struct Card<Content: View>: View {
    let content: Content
    let padding: CGFloat

    public init(padding: CGFloat = 16, @ViewBuilder content: () -> Content) {
        self.content = content()
        self.padding = padding
    }

    public var body: some View {
        content
            .padding(padding)
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Theme.cardBackground))
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).stroke(Theme.cardStroke, lineWidth: 1))
    }
}

/// Vòng tiến độ có số phần trăm ở giữa.
public struct ProgressRing: View {
    let progress: Double
    let lineWidth: CGFloat
    let label: String?

    public init(progress: Double, lineWidth: CGFloat = 10, label: String? = nil) {
        self.progress = progress
        self.lineWidth = lineWidth
        self.label = label
    }

    public var body: some View {
        ZStack {
            Circle().stroke(Color.white.opacity(0.15), lineWidth: lineWidth)
            Circle()
                .trim(from: 0, to: max(0.001, min(progress, 1)))
                .stroke(AngularGradient(colors: [.white.opacity(0.6), .white], center: .center), style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(.easeOut(duration: 0.25), value: progress)
            VStack(spacing: 2) {
                Text("\(Int(progress * 100))%").font(.system(size: 28, weight: .bold, design: .rounded)).monospacedDigit()
                if let label { Text(label).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText) }
            }
            .foregroundStyle(.white)
        }
    }
}

/// Nhãn mức an toàn.
public struct SafetyBadge: View {
    let safety: SafetyLevel
    public init(_ safety: SafetyLevel) { self.safety = safety }
    public var body: some View {
        Text(safety.localizedTitle)
            .font(.system(size: 10, weight: .semibold))
            .padding(.horizontal, 6).padding(.vertical, 2)
            .background(Capsule().fill(Theme.color(for: safety).opacity(0.25)))
            .foregroundStyle(Theme.color(for: safety))
    }
}

/// Nhãn nhỏ dạng viên thuốc.
public struct Pill: View {
    let text: String
    let color: Color
    public init(_ text: String, color: Color = .white) {
        self.text = text
        self.color = color
    }
    public var body: some View {
        Text(text)
            .font(.system(size: 10, weight: .semibold))
            .padding(.horizontal, 6).padding(.vertical, 2)
            .background(Capsule().fill(color.opacity(0.2)))
            .foregroundStyle(color)
    }
}

/// Tiêu đề màn hình feature: icon, tên, mô tả.
public struct FeatureHeader: View {
    let symbol: String
    let title: String
    let subtitle: String

    public init(symbol: String, title: String, subtitle: String) {
        self.symbol = symbol
        self.title = title
        self.subtitle = subtitle
    }

    public var body: some View {
        VStack(spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 64, weight: .light))
                .foregroundStyle(.white)
                .shadow(color: .black.opacity(0.25), radius: 10)
            Text(title).font(Theme.Font.title).foregroundStyle(Theme.primaryText)
            Text(subtitle).font(Theme.Font.body).foregroundStyle(Theme.secondaryText).multilineTextAlignment(.center).frame(maxWidth: 460)
        }
    }
}

/// Nút phụ trong suốt.
public struct GlassButtonStyle: ButtonStyle {
    public init() {}
    public func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold))
            .padding(.horizontal, 14).padding(.vertical, 7)
            .background(Capsule().fill(Color.white.opacity(configuration.isPressed ? 0.28 : 0.16)))
            .overlay(Capsule().stroke(Color.white.opacity(0.25)))
            .foregroundStyle(.white)
    }
}

/// Nút chính đặc.
public struct PrimaryButtonStyle: ButtonStyle {
    let accent: Theme.Accent
    public init(accent: Theme.Accent) { self.accent = accent }
    public func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 14, weight: .bold))
            .padding(.horizontal, 22).padding(.vertical, 9)
            .background(Capsule().fill(Color.white.opacity(configuration.isPressed ? 0.75 : 0.95)))
            .foregroundStyle(accent.gradient[1])
            .shadow(color: .black.opacity(0.2), radius: 6, y: 2)
    }
}

/// Icon file/app từ Finder.
public struct FileIcon: View {
    let url: URL?
    let fallback: String
    let size: CGFloat

    public init(url: URL?, fallback: String = "doc", size: CGFloat = 18) {
        self.url = url
        self.fallback = fallback
        self.size = size
    }

    public var body: some View {
        Group {
            if let url, FileManager.default.fileExists(atPath: url.path) {
                Image(nsImage: NSWorkspace.shared.icon(forFile: url.path)).resizable()
            } else {
                Image(systemName: fallback).resizable().scaledToFit().padding(2).foregroundStyle(.white.opacity(0.85))
            }
        }
        .frame(width: size, height: size)
    }
}

/// Ô thống kê: số lớn + nhãn.
public struct StatTile: View {
    let value: String
    let label: String
    let symbol: String?

    public init(value: String, label: String, symbol: String? = nil) {
        self.value = value
        self.label = label
        self.symbol = symbol
    }

    public var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                if let symbol { Image(systemName: symbol).foregroundStyle(Theme.secondaryText) }
                Text(label).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
            }
            Text(value).font(.system(size: 22, weight: .bold, design: .rounded)).monospacedDigit().foregroundStyle(.white)
        }
    }
}

/// Banner cảnh báo (ví dụ "Cần Full Disk Access").
public struct NoticeBanner: View {
    let symbol: String
    let text: String
    let actionTitle: String?
    let action: (() -> Void)?

    public init(symbol: String = "exclamationmark.triangle.fill", text: String, actionTitle: String? = nil, action: (() -> Void)? = nil) {
        self.symbol = symbol
        self.text = text
        self.actionTitle = actionTitle
        self.action = action
    }

    public var body: some View {
        HStack(spacing: 10) {
            Image(systemName: symbol).foregroundStyle(Theme.review)
            Text(text).font(Theme.Font.body).foregroundStyle(.white)
            Spacer()
            if let actionTitle, let action {
                Button(actionTitle, action: action).buttonStyle(GlassButtonStyle())
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.black.opacity(0.25)))
    }
}

extension ByteCount {
    /// Chuỗi hiển thị ngắn.
    public var short: String { formatted }
}
