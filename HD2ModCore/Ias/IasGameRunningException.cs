namespace HD2ModCore.Ias;

public sealed class IasGameRunningException() : InvalidOperationException(Notice)
{
	public const string Notice = "请先完全退出游戏再操作 IAS（检测到 Helldivers 2 正在运行）。";
}
