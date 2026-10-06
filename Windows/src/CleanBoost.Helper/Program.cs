using System.IO.Pipes;
using CleanBoost.Core;
using CleanBoost.Core.Ipc;
using CleanBoost.Core.Native;
using CleanBoost.Helper;

// Helper quản trị của Clean Boost (SYSTEM_DESIGN mục 9, bản Windows).
// Tham số: --pipe <tên> --parent <pid>. Không có tham số hợp lệ thì thoát ngay.
Log.Configure("CleanBoost.Helper");

string? pipeName = null;
uint parentPid = 0;
for (var i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--pipe") pipeName = args[++i];
    else if (args[i] == "--parent" && uint.TryParse(args[i + 1], out var pid)) { parentPid = pid; i++; }
}
if (pipeName is null || !pipeName.StartsWith("CleanBoost.Helper.", StringComparison.Ordinal) || parentPid == 0)
{
    Log.Error(LogCategory.Helper, "Tham số không hợp lệ, thoát");
    return 2;
}

using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
try
{
    await pipe.ConnectAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
}
catch (Exception ex)
{
    Log.Error(LogCategory.Helper, "Không nối được pipe", ex);
    return 3;
}

// Kiểm tra ngược: tiến trình tạo pipe phải là tiến trình cha đã khai báo, và là CleanBoost.exe cùng thư mục với helper.
if (!Win32.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != parentPid)
{
    Log.Error(LogCategory.Helper, $"Pipe không do tiến trình cha tạo (server {serverPid}, cha {parentPid}), thoát");
    return 4;
}
var expected = Path.Combine(AppContext.BaseDirectory, Identifiers.AppExeName);
var image = Win32.ProcessImagePath(serverPid);
if (image is null || !string.Equals(Path.GetFullPath(image), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
{
    Log.Error(LogCategory.Helper, "Tiến trình gọi không phải Clean Boost cùng thư mục, thoát");
    return 5;
}

Log.Info(LogCategory.Helper, $"Helper sẵn sàng, phục vụ pid {serverPid}");
var service = new HelperService(serverPid);
while (pipe.IsConnected)
{
    HelperRequest? request;
    try
    {
        request = await PipeMessage.ReadAsync<HelperRequest>(pipe);
    }
    catch (Exception ex)
    {
        Log.Error(LogCategory.Helper, "Đọc yêu cầu lỗi", ex);
        break;
    }
    if (request is null || request.Command == HelperCommands.Quit) break;
    var response = await service.HandleAsync(request);
    try
    {
        await PipeMessage.WriteAsync(pipe, response);
    }
    catch (Exception ex)
    {
        Log.Error(LogCategory.Helper, "Ghi phản hồi lỗi", ex);
        break;
    }
}
Log.Info(LogCategory.Helper, "Helper thoát");
return 0;
