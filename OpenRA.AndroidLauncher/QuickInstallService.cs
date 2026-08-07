// Quick Install: download OpenRA RA freeware package and extract to Content/ra/v2.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenRA.Android
{
	public static class QuickInstallService
	{
		public const string MirrorListUrl = "https://www.openra.net/packages/ra-quickinstall-mirrors.txt";
		public const string ExpectedSha1 = "44241f68e69db9511db82cf83c174737ccda300b";

		public sealed class Progress
		{
			public string Status { get; set; }
			public long BytesReceived { get; set; }
			public long? TotalBytes { get; set; }
			public double Fraction =>
				TotalBytes is > 0 ? Math.Min(1.0, (double)BytesReceived / TotalBytes.Value) : 0;
		}

		public static async Task InstallAsync(
			string supportDir,
			IProgress<Progress> progress,
			CancellationToken ct)
		{
			Directory.CreateDirectory(supportDir);
			var contentRoot = ContentProbe.ContentRaV2(supportDir);
			Directory.CreateDirectory(contentRoot);

			progress?.Report(new Progress { Status = "Fetching mirror list…" });
			var mirrors = await FetchMirrorsAsync(ct).ConfigureAwait(false);
			if (mirrors.Length == 0)
				throw new InvalidOperationException("No content mirrors available.");

			var cacheDir = Path.Combine(supportDir, "Cache");
			Directory.CreateDirectory(cacheDir);
			var zipPath = Path.Combine(cacheDir, "ra-quickinstall.zip");

			Exception last = null;
			foreach (var url in mirrors)
			{
				ct.ThrowIfCancellationRequested();
				try
				{
					progress?.Report(new Progress { Status = "Downloading…\n" + url });
					await DownloadAsync(url, zipPath, progress, ct).ConfigureAwait(false);
					last = null;
					break;
				}
				catch (Exception e) when (e is not OperationCanceledException)
				{
					last = e;
					AndroidFileLog.Warn("OpenRA.Install", "Mirror failed " + url + ": " + e.Message);
				}
			}

			if (last != null && !File.Exists(zipPath))
				throw new InvalidOperationException("All mirrors failed.", last);

			progress?.Report(new Progress { Status = "Extracting content…" });
			ExtractZip(zipPath, contentRoot, progress, ct);

			if (!ContentProbe.IsBaseContentInstalled(supportDir))
			{
				throw new InvalidOperationException(
					"Extract finished but required MIX files are still missing: "
					+ ContentProbe.MissingSummary(supportDir));
			}

			progress?.Report(new Progress { Status = "Content installed.", BytesReceived = 1, TotalBytes = 1 });
			AndroidFileLog.Info("OpenRA.Install", "Quick Install complete → " + contentRoot);
		}

		static async Task<string[]> FetchMirrorsAsync(CancellationToken ct)
		{
			using var http = CreateHttp();
			var text = await http.GetStringAsync(MirrorListUrl, ct).ConfigureAwait(false);
			return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.Where(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
				.ToArray();
		}

		static async Task DownloadAsync(string url, string destPath, IProgress<Progress> progress, CancellationToken ct)
		{
			using var http = CreateHttp();
			using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
				.ConfigureAwait(false);
			resp.EnsureSuccessStatusCode();
			var total = resp.Content.Headers.ContentLength;

			await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
			await using var output = File.Create(destPath);
			var buffer = new byte[64 * 1024];
			long readTotal = 0;
			int n;
			while ((n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
			{
				await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
				readTotal += n;
				progress?.Report(new Progress
				{
					Status = total is > 0
						? $"Downloading… {readTotal / (1024 * 1024)}/{total.Value / (1024 * 1024)} MB"
						: $"Downloading… {readTotal / (1024 * 1024)} MB",
					BytesReceived = readTotal,
					TotalBytes = total
				});
			}
		}

		static void ExtractZip(string zipPath, string contentRaV2, IProgress<Progress> progress, CancellationToken ct)
		{
			using var zip = ZipFile.OpenRead(zipPath);
			var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToArray();
			var i = 0;
			foreach (var entry in entries)
			{
				ct.ThrowIfCancellationRequested();
				i++;
				// Zip layout matches downloads.yaml source names (e.g. allies.mix, expand/...)
				var rel = entry.FullName.Replace('\\', '/');
				if (rel.Contains("..", StringComparison.Ordinal))
					continue;

				var dest = Path.Combine(contentRaV2, rel.Replace('/', Path.DirectorySeparatorChar));
				var dir = Path.GetDirectoryName(dest);
				if (!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);

				entry.ExtractToFile(dest, overwrite: true);
				progress?.Report(new Progress
				{
					Status = $"Extracting… {i}/{entries.Length}",
					BytesReceived = i,
					TotalBytes = entries.Length
				});
			}
		}

		static HttpClient CreateHttp()
		{
			var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
			http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenRA-Android/0.1");
			return http;
		}
	}
}
