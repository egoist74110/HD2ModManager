using System.Net.Http;
using System.Net.Sockets;

namespace HD2ModManager.Services.Ias;

/// <summary>
/// 判断一次 IAS 操作失败是不是「下载没连上」（IAS 本体、官方清单、微软运行库等），
/// 这类失败换个下载来源通常就能好，界面据此提示用户去设置里换来源。
/// </summary>
public static class IasDownloadFailure
{
    public const string Hint = "可能是当前下载来源连不上。请到「设置 → 程序更新 → 下载来源」换一个来源（例如镜像），再重试。";

    public static bool Is(Exception? error)
    {
        var pending = new Stack<Exception>();
        if (error is not null) pending.Push(error);
        var visited = 0;
        while (pending.Count > 0 && visited++ < 64)
        {
            var current = pending.Pop();
            if (current is HttpRequestException or TimeoutException or SocketException) return true;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return false;
    }
}
