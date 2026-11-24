using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Updates;

namespace HD2ModCore.Ias;

public static class IasUpdateHttp
{
	public static HttpClient Create(Func<UpdateNetworkSettings> settings) =>
		new(new Handler(settings)) { Timeout = Timeout.InfiniteTimeSpan };

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
			var source = _network.Source();
			// 更新器的镜像支持 GitHub 仓库地址。该路径和 raw URL 对应同一个签名清单。
			var url = uri == IasFeedClient.FeedUri && _network.Settings.Mode != DownloadSourceMode.Ask
				? "https://github.com/Estecsky/HD2-Integrated-Armor-System/raw/refs/heads/main/feed.json" : uri.AbsoluteUri;
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

