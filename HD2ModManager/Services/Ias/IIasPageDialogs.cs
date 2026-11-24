namespace HD2ModManager.Services.Ias;

public sealed record IasConfirmation(string Title, string Message, string ConfirmLabel, string? CheckBoxLabel = null,
    string CancelLabel = "取消");

public interface IIasPageDialogs
{
    // null 表示取消；false 表示确认且未勾选。
    bool? Confirm(IasConfirmation confirmation);
    // 对话框先打开、在里面异步加载候选（显示加载中）。null 表示取消；确认时返回勾选的全部候选（至少一个）。
    IReadOnlyList<IasLibraryCandidate>? ChooseLibraryMods(
        Func<CancellationToken, Task<IReadOnlyList<IasLibraryCandidate>>> loadCandidates, CancellationToken ct);
}
