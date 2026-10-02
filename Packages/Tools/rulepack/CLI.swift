import Foundation

/// Lỗi dòng lệnh: in ra stderr và thoát với mã khác 0.
struct CLIError: Error, CustomStringConvertible {
    let message: String
    let code: Int32

    init(_ message: String, code: Int32 = 1) {
        self.message = message
        self.code = code
    }

    static func usage(_ message: String) -> CLIError { CLIError(message, code: 2) }

    var description: String { message }
}

/// Tham số dòng lệnh: vị trí + tuỳ chọn `--key value` / cờ `--flag`.
struct Arguments {
    var positional: [String] = []
    var options: [String: String] = [:]
    var flags: Set<String> = []

    init(_ raw: [String], flags known: Set<String> = []) throws {
        var i = 0
        while i < raw.count {
            let a = raw[i]
            if a.hasPrefix("--") {
                let name = String(a.dropFirst(2))
                if let eq = name.firstIndex(of: "=") {
                    options[String(name[..<eq])] = String(name[name.index(after: eq)...])
                } else if known.contains(name) {
                    flags.insert(name)
                } else {
                    guard i + 1 < raw.count else { throw CLIError.usage("Thiếu giá trị cho --\(name)") }
                    options[name] = raw[i + 1]
                    i += 1
                }
            } else {
                positional.append(a)
            }
            i += 1
        }
    }

    func positional(_ index: Int, _ what: String) throws -> String {
        guard index < positional.count else { throw CLIError.usage("Thiếu tham số: \(what)") }
        return positional[index]
    }

    func required(_ name: String) throws -> String {
        guard let v = options[name], !v.isEmpty else { throw CLIError.usage("Thiếu tuỳ chọn --\(name)") }
        return v
    }
}

enum Console {
    static func out(_ s: String) {
        FileHandle.standardOutput.write(Data((s + "\n").utf8))
    }

    static func err(_ s: String) {
        FileHandle.standardError.write(Data((s + "\n").utf8))
    }
}

func absoluteURL(_ path: String, isDirectory: Bool = false) -> URL {
    let expanded = (path as NSString).expandingTildeInPath
    if expanded.hasPrefix("/") { return URL(fileURLWithPath: expanded, isDirectory: isDirectory).standardizedFileURL }
    return URL(fileURLWithPath: FileManager.default.currentDirectoryPath, isDirectory: true)
        .appendingPathComponent(expanded, isDirectory: isDirectory).standardizedFileURL
}

func readFile(_ path: String) throws -> Data {
    do {
        return try Data(contentsOf: absoluteURL(path))
    } catch {
        throw CLIError("Không đọc được \(path): \(error.localizedDescription)")
    }
}

func writeFile(_ data: Data, to path: String, permissions: Int? = nil) throws {
    let url = absoluteURL(path)
    do {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: .atomic)
        if let permissions {
            try FileManager.default.setAttributes([.posixPermissions: permissions], ofItemAtPath: url.path)
        }
    } catch {
        throw CLIError("Không ghi được \(path): \(error.localizedDescription)")
    }
}
