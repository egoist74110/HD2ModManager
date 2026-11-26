using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Updates;

namespace HD2ModCore.Ias;

public static class IasUpdateHttp
{
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpClient, Func<UpdateNetworkSettings>> Settings = new();

	public static HttpClient Create(Func<UpdateNetworkSettings> settings)
	{
		var client = new HttpClient(new Handler(settings)) { Timeout = Timeout.InfiniteTimeSpan };
		Settings.AddOrUpdate(client, settings);
		return client;
	}

	/// <summary>这个客户端对应的下载来源设置是不是「优先使用镜像」（不是本类创建的客户端一律按否处理）。</summary>
	internal static bool PrefersMirror(HttpClient client)
	{
		if (!Settings.TryGetValue(client, out var settings)) return false;
		try { return settings().Mode == DownloadSourceMode.Mirror; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
	}

	/// <summary>
	/// 镜像只代理 github.com 地址。非直连模式下，把官方清单和 LFS 整包（media.githubusercontent.com）
	/// 换成 github.com 上的同一文件；GitHub 会 302 回原地址，内容与签名清单一致。其余地址原样返回。
	/// </summary>
	internal static string MirrorableUrl(Uri uri, DownloadSourceMode mode)
	{
		if (mode == DownloadSourceMode.Ask) return uri.AbsoluteUri;
		if (uri == IasFeedClient.FeedUri)
			return "https://github.com/Estecsky/HD2-Integrated-Armor-System/raw/refs/heads/main/feed.json";
		const string media = "/media/Estecsky/HD2-Integrated-Armor-System/";
		if (string.Equals(uri.Host, "media.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
			IasFeedClient.IsOfficialUrl(uri) && uri.AbsolutePath.StartsWith(media, StringComparison.OrdinalIgnoreCase))
			return "https://github.com/Estecsky/HD2-Integrated-Armor-System/raw/" + uri.AbsolutePath[media.Length..] + uri.Query;
		return uri.AbsoluteUri;
	}

	public static UpdateNetworkSettings ReadSettings(string managerRoot)
	{
		var path = Path.Combine(managerRoot, "config", "app-update.json");
		try
		{
			var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
			return (File.Exists(path) ? JsonSerializer.Deserialize<AppUpdatePrefs>(File.ReadAllText(path), options)?.Sanitized().Network : null)
				?? UpdateNetworkSettings.Default;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return UpdateNetworkSettings.Default;
		}
	}

	private sealed class Handler : HttpMessageHandler
	{
		private readonly HttpClient _transport = new(new SocketsHttpHandler
		{
			AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15)
		}) { Timeout = Timeout.InfiniteTimeSpan };
		private readonly UpdateNetwork _network;

		public Handler(Func<UpdateNetworkSettings> settings) => _network = new(settings, SendRawAsync);

		private async Task<HttpResponseMessage> SendRawAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken token)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.UserAgent.ParseAdd("HD2ModManager-IAS");
			if (headers is not null)
				foreach (var (name, value) in headers)
					request.Headers.TryAddWithoutValidation(name, value);
			return await _transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		}

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var uri = request.RequestUri ?? throw new HttpRequestException("IAS 请求缺少地址。");
			// NuGet 包使用固定地址和包哈希，镜像切换由运行库下载器处理。
			if (IasWorkerRuntime.IsPackageRequest(uri))
				return await SendRawAsync(uri.AbsoluteUri, null, cancellationToken).ConfigureAwait(false);
			var source = _network.Source();
			// 更新器的镜像支持 GitHub 仓库地址。该路径和 raw URL 对应同一个签名清单。
			var url = MirrorableUrl(uri, _network.Settings.Mode);
			var headers = request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(", ", pair.Value));
			var response = await (uri == IasFeedClient.FeedUri && _network.Settings.Mode == DownloadSourceMode.Ask
				? source.Direct(url, headers, cancellationToken) : source.Fetch(url, headers, cancellationToken)).ConfigureAwait(false);
			// UpdateNetwork 验证了实际重定向链，W5 收到的是原始逻辑请求。
			response.RequestMessage = request;
			return response;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing) _transport.Dispose();
			base.Dispose(disposing);
		}
	}
}

