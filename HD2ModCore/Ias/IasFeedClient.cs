using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace HD2ModCore.Ias;

public sealed record IasReleaseFile(string Path, long Bytes, string Sha256);

public sealed class IasRelease
{
	internal IasRelease(string version, long sequence, long minimumSequence, string notes,
		Uri zipUrl, string zipSha256, long zipBytes, IReadOnlyList<IasReleaseFile> files, Uri releaseUrl)
	{
		Version = version;
		Sequence = sequence;
		MinimumSequence = minimumSequence;
		Notes = notes;
		ZipUrl = zipUrl;
		ZipSha256 = zipSha256;
		ZipBytes = zipBytes;
		Files = Array.AsReadOnly(files.ToArray());
		ReleaseUrl = releaseUrl;
	}

	public string Version { get; }
	public long Sequence { get; }
	public long MinimumSequence { get; }
	public string Notes { get; }
	public Uri ZipUrl { get; }
	public string ZipSha256 { get; }
	public long ZipBytes { get; }
	public IReadOnlyList<IasReleaseFile> Files { get; }
	public Uri ReleaseUrl { get; }
}

public enum IasFeedFailure
{
	None,
	Timeout,
	HttpStatus,
	Network,
	InvalidEnvelope,
	InvalidSignature,
	InvalidSchema,
	InvalidPayload
}

public sealed record IasFeedResult(IasRelease? Latest, IasFeedFailure Failure, string Message);

public sealed class IasFeedClient(HttpClient httpClient)
{
	private const string Repository = "/Estecsky/HD2-Integrated-Armor-System/";
	private const int MaxFeedBytes = 4 * 1024 * 1024;
	private const string RsaModulus = "u7h7n4eci0Cv2/asTqJdcvtg1C88O4KU/XAG3b1Hc9uY9aXmJvASGGngNJo5VFac0ZIzW11Sk2N9AtLPY3RwiPFOnFc5VaZQ43v7VlUUBEIBEqX9JlAkp4a0lHpX3LE/pRcecWMT8VWdJtOPTV9FA6LvDz94I47ncDw9xJt1uVaM9vvo75J5NlQ6mh8xcoe38qegVdyvXojWpuSd/ogtvrh4ydh56XlxuBMy+mpREFalm2RuGaW7FoSxLBDMqQgcZa/GCK2VQ3HGNk0osVa73oi7vEd12kyuQUvP9jxFF1o3kC6Y0RD+Vrgz7I4X9MVFk/XjI4q09haJVnnxKLUhFT8Xr0p/qRGqkfX4zzr7/Cavo9ry/8De48VYlzWNjgSdNL/eWbO4wFvjoOph8JJ6nWf3Ph102RIBlfMcKh4cOtEZs2G4es8zmVlnqbs4doUa/OiipCk56sUivzvr7o1QVGHENLyh+L48I3g6U+7/KhEBtowiHGQPCNzQCX0iSTSF";

	public static Uri FeedUri { get; } = new($"https://raw.githubusercontent.com{Repository}main/feed.json");

	public async Task<IasFeedResult> GetLatestAsync(IProgress<IasProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		progress?.Report(new(IasStage.FetchFeed, 0, 0, "正在读取 IAS 官方清单。"));
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));
		try
		{
			using var response = await httpClient.GetAsync(FeedUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
				.ConfigureAwait(false);
			if (response.StatusCode != HttpStatusCode.OK)
				return new(null, IasFeedFailure.HttpStatus, $"IAS 官方清单返回 HTTP {(int)response.StatusCode}。");
			if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != FeedUri)
				return new(null, IasFeedFailure.HttpStatus, "IAS 官方清单响应来自意外的网址。");
			if (response.Content.Headers.ContentLength > MaxFeedBytes)
				return new(null, IasFeedFailure.InvalidEnvelope, "IAS 清单超出大小限制。");

			using var content = new MemoryStream();
			using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
			var buffer = new byte[8192];
			int read;
			while ((read = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
			{
				if (content.Length + read > MaxFeedBytes)
					return new(null, IasFeedFailure.InvalidEnvelope, "IAS 清单超出大小限制。");
				content.Write(buffer, 0, read);
			}
			return Verify(content.ToArray());
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new(null, IasFeedFailure.Timeout, "获取 IAS 官方清单超时。");
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException)
		{
			return new(null, IasFeedFailure.Network, $"无法读取 IAS 官方清单：{exception.Message}");
		}
	}

	public static IasFeedResult Verify(ReadOnlyMemory<byte> signedFeed)
	{
		if (signedFeed.Length > MaxFeedBytes)
			return new(null, IasFeedFailure.InvalidEnvelope, "IAS 清单超出大小限制。");

		byte[] payload;
		byte[] signature;
		try
		{
			using var document = JsonDocument.Parse(signedFeed);
			payload = Convert.FromBase64String(document.RootElement.GetProperty("payload").GetString() ?? "");
			signature = Convert.FromBase64String(document.RootElement.GetProperty("signature").GetString() ?? "");
			if (payload.Length == 0 || signature.Length == 0)
				return new(null, IasFeedFailure.InvalidEnvelope, "IAS 清单缺少 payload 或签名。");
		}
		catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
		{
			return new(null, IasFeedFailure.InvalidEnvelope, "IAS 清单外层 JSON 或 Base64 数据无效。");
		}

		try
		{
			using var rsa = RSA.Create();
			rsa.ImportParameters(new RSAParameters
			{
				Modulus = Convert.FromBase64String(RsaModulus),
				Exponent = Convert.FromBase64String("AQAB")
			});
			// 官方签名覆盖原始 JSON 字节，不能重新序列化后再验。
			if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
				return new(null, IasFeedFailure.InvalidSignature, "IAS 官方清单验签失败。");
		}
		catch (CryptographicException)
		{
			return new(null, IasFeedFailure.InvalidSignature, "无法验证 IAS 官方清单的 RSA 签名。");
		}

		return ParsePayload(payload);
	}

	internal static IasFeedResult ParsePayload(ReadOnlyMemory<byte> payload)
	{
		try
		{
			using var document = JsonDocument.Parse(payload);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema)
				|| schema.ValueKind != JsonValueKind.String || schema.GetString() != "HD2ManagerFeed1")
				return new(null, IasFeedFailure.InvalidSchema, "IAS 官方清单 schema 必须为 HD2ManagerFeed1。");
			var latestVersion = RequiredString(root, "latest_version");
			var latestSequence = root.GetProperty("latest_sequence").GetInt64();
			var releaseUrl = new Uri(RequiredString(root, "release_url"), UriKind.Absolute);
			if (!IsOfficialUrl(releaseUrl) || releaseUrl.Host != "github.com")
				throw new FormatException("release_url 超出官方 Releases 路径。");

			var latest = root.GetProperty("releases").EnumerateArray().Single(entry =>
				entry.GetProperty("version").GetString() == latestVersion &&
				entry.GetProperty("sequence").GetInt64() == latestSequence);
			var minimumSequence = latest.GetProperty("minimum_sequence").GetInt64();
			var zipBytes = latest.GetProperty("zip_bytes").GetInt64();
			var zipUrl = new Uri(RequiredString(latest, "zip_url"), UriKind.Absolute);
			if (!IasPaths.IsVersion(latestVersion) || latestSequence <= 0 || minimumSequence < 0 ||
				minimumSequence > latestSequence || zipBytes <= 0 || !IsOfficialUrl(zipUrl))
				throw new FormatException("版本、序号、整包大小或官方 URL 无效。");

			var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var files = new List<IasReleaseFile>();
			foreach (var file in latest.GetProperty("files").EnumerateArray())
			{
				var path = RequiredString(file, "path");
				var bytes = file.GetProperty("bytes").GetInt64();
				if (!IasPaths.IsRelativePath(path) || !paths.Add(path) || bytes < 0)
					throw new FormatException($"文件路径重复、危险或大小无效：{path}");
				files.Add(new(path, bytes, RequiredSha256(file, "sha256")));
			}
			var release = new IasRelease(latestVersion, latestSequence, minimumSequence,
				latest.GetProperty("notes").GetString() ?? "", zipUrl, RequiredSha256(latest, "zip_sha256"),
				zipBytes, files, releaseUrl);
			return new(release, IasFeedFailure.None, "IAS 官方清单验签通过。");
		}
		catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
		{
			return new(null, IasFeedFailure.InvalidPayload, $"IAS 官方清单字段无效：{exception.Message}");
		}
	}

	internal static bool IsOfficialUrl(Uri uri)
	{
		if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
			return false;
		return uri.Host switch
		{
			"github.com" => uri.AbsolutePath.StartsWith(Repository + "releases/", StringComparison.OrdinalIgnoreCase),
			"raw.githubusercontent.com" => uri.AbsolutePath.StartsWith(Repository, StringComparison.OrdinalIgnoreCase),
			"media.githubusercontent.com" => uri.AbsolutePath.StartsWith("/media" + Repository, StringComparison.OrdinalIgnoreCase),
			_ => false
		};
	}

	private static string RequiredString(JsonElement node, string key) =>
		node.GetProperty(key).GetString() is { Length: > 0 } value ? value : throw new FormatException($"缺少 {key}。");

	private static string RequiredSha256(JsonElement node, string key)
	{
		var value = RequiredString(node, key);
		return IasPaths.IsSha256(value) ? value : throw new FormatException($"{key} 不是 SHA-256。");
	}
}
