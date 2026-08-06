// File logger → /storage/emulated/0/OpenRa/error.log (with fallbacks).

using System;
using System.IO;
using System.Text;
using Android.App;
using ALog = global::Android.Util.Log;
using AEnv = global::Android.OS.Environment;
using AProcess = global::Android.OS.Process;

namespace OpenRA.Android
{
	public static class AndroidFileLog
	{
		public const string PublicDirName = "OpenRa";
		public const string ErrorFileName = "error.log";

		static readonly object Gate = new();
		static string logPath;
		static StreamWriter writer;

		/// <summary>Preferred path: /storage/emulated/0/OpenRa/error.log</summary>
		public static string PreferredPath =>
			Path.Combine(AEnv.ExternalStorageDirectory?.AbsolutePath
				?? "/storage/emulated/0", PublicDirName, ErrorFileName);

		public static string ActivePath => logPath;

		public static void Init()
		{
			lock (Gate)
			{
				if (writer != null)
					return;

				foreach (var candidate in CandidatePaths())
				{
					try
					{
						var dir = Path.GetDirectoryName(candidate);
						if (!string.IsNullOrEmpty(dir))
							Directory.CreateDirectory(dir);

						var fs = new FileStream(candidate, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
						writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
						logPath = candidate;
						writer.WriteLine();
						writer.WriteLine($"===== OpenRA Android log start {DateTime.UtcNow:o} =====");
						writer.WriteLine($"pid={AProcess.MyPid()} path={candidate}");
						ALog.Info("OpenRA.Log", "File log: " + candidate);
						return;
					}
					catch (Exception e)
					{
						ALog.Warn("OpenRA.Log", $"Cannot write {candidate}: {e.Message}");
					}
				}

				ALog.Error("OpenRA.Log", "No writable path for error.log");
			}
		}

		static string[] CandidatePaths()
		{
			var primary = PreferredPath;
			string appExternal = null;
			try
			{
				var ctx = Application.Context;
				var ext = ctx.GetExternalFilesDir(null)?.AbsolutePath;
				if (!string.IsNullOrEmpty(ext))
					appExternal = Path.Combine(ext, PublicDirName, ErrorFileName);
			}
			catch { /* ignore */ }

			var internalPath = Path.Combine(
				Application.Context.FilesDir?.AbsolutePath ?? "/data/local/tmp",
				PublicDirName, ErrorFileName);

			return appExternal != null
				? new[] { primary, appExternal, internalPath }
				: new[] { primary, internalPath };
		}

		public static void Info(string tag, string message) => Write("INFO", tag, message);
		public static void Warn(string tag, string message) => Write("WARN", tag, message);
		public static void Error(string tag, string message) => Write("ERROR", tag, message);

		public static void Write(string level, string tag, string message)
		{
			if (level == "ERROR")
				ALog.Error(tag, message);
			else if (level == "WARN")
				ALog.Warn(tag, message);
			else
				ALog.Info(tag, message);

			lock (Gate)
			{
				if (writer == null)
					Init();
				try
				{
					writer?.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {tag}: {message}");
				}
				catch (Exception e)
				{
					ALog.Warn("OpenRA.Log", "Write failed: " + e.Message);
				}
			}
		}

		public static void Exception(string tag, Exception ex)
		{
			Error(tag, ex.ToString());
		}
	}
}
