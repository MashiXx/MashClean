import Foundation
import UniformTypeIdentifiers

/// Loại file dùng cho bộ lọc Large & Old (mục 11.7), suy ra từ UTType của đuôi file.
public enum FileKind: String, Sendable, CaseIterable, Hashable, Identifiable {
    case documents, images, videos, audio, archives, apps, other

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .documents: String(localized: "Tài liệu")
        case .images: String(localized: "Ảnh")
        case .videos: "Video"
        case .audio: String(localized: "Âm thanh")
        case .archives: String(localized: "Lưu trữ & ổ đĩa ảo")
        case .apps: String(localized: "Ứng dụng & bộ cài")
        case .other: String(localized: "Khác")
        }
    }

    public var symbol: String {
        switch self {
        case .documents: "doc.text"
        case .images: "photo"
        case .videos: "film"
        case .audio: "music.note"
        case .archives: "archivebox"
        case .apps: "shippingbox"
        case .other: "doc"
        }
    }

    private static let archiveExtensions: Set<String> = [
        "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "lz4", "dmg", "iso", "img", "sparseimage", "sparsebundle",
        "vmdk", "vdi", "qcow2", "vhd", "vhdx", "hdd", "ova", "ipsw", "xip", "cpgz",
    ]
    private static let appExtensions: Set<String> = ["app", "pkg", "mpkg", "exe", "msi", "apk", "ipa", "deb", "rpm", "appimage"]

    public static func classify(pathExtension raw: String) -> FileKind {
        let ext = raw.lowercased()
        guard !ext.isEmpty else { return .other }
        if archiveExtensions.contains(ext) { return .archives }
        if appExtensions.contains(ext) { return .apps }
        guard let type = UTType(filenameExtension: ext) else { return .other }
        if type.conforms(to: .movie) || type.conforms(to: .video) { return .videos }
        if type.conforms(to: .audio) { return .audio }
        if type.conforms(to: .image) { return .images }
        if type.conforms(to: .archive) || type.conforms(to: .diskImage) { return .archives }
        if type.conforms(to: .application) || type.conforms(to: .executable) { return .apps }
        if type.conforms(to: .pdf) || type.conforms(to: .text) || type.conforms(to: .presentation)
            || type.conforms(to: .spreadsheet) || type.conforms(to: .compositeContent) {
            return .documents
        }
        return .other
    }

    public static func classify(_ url: URL) -> FileKind { classify(pathExtension: url.pathExtension) }
}
