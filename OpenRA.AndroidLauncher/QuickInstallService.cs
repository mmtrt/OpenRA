// Quick Install: download OpenRA freeware package for the built mod and extract to Content/.

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
			var mod = ModInfo.Current;
			Directory.CreateDirectory(supportDir);
			var contentRoot = ContentProbe.ContentRoot(supportDir);
			Directory.CreateDirectory(contentRoot);

			progress?.Report(new Progress { Status = "Fetching mirror list…" });
			var mirrors = await FetchMirrorsAsync(mod.MirrorListUrl, ct).ConfigureAwait(false);
			if (mirrors.Length == 0)
				throw new InvalidOperationException("No content mirrors available for " + mod.DisplayName + ".");

			var cacheDir = Path.Combine(supportDir, "Cache");
			Directory.CreateDirectory(cacheDir);
			var zipPath = Path.Combine(cacheDir, mod.Id + "-quickinstall.zip");

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
					"Extract finished but required files are still missing: "
					+ ContentProbe.MissingSummary(supportDir));
			}

			progress?.Report(new Progress { Status = "Content installed.", BytesReceived = 1, TotalBytes = 1 });
			AndroidFileLog.Info("OpenRA.Install", "Quick Install complete → " + contentRoot);
		}

		static async Task<string[]> FetchMirrorsAsync(string mirrorListUrl, CancellationToken ct)
		{
			using var http = CreateHttp();
			var text = await http.GetStringAsync(mirrorListUrl, ct).ConfigureAwait(false);
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

		static void ExtractZip(string zipPath, string contentRoot, IProgress<Progress> progress, CancellationToken ct)
		{
			using var zip = ZipFile.OpenRead(zipPath);
			var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToArray();
			var i = 0;
			foreach (var entry in entries)
			{
				ct.ThrowIfCancellationRequested();
				var dest = Path.Combine(contentRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
				var dir = Path.GetDirectoryName(dest);
				if (!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);
				if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
					continue;
				entry.ExtractToFile(dest, overwrite: true);
				i++;
				if (i % 25 == 0)
					progress?.Report(new Progress { Status = $"Extracting… {i}/{entries.Length}" });
			}
		}

		static HttpClient CreateHttp()
		{
			var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
			http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenRA-Android/" + BuildConfig.ModId);
			return http;
		}
	}
}
