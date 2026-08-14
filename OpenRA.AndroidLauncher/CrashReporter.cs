// Independent crash file writer. Does not depend on OpenRA.Game / OpenRA.Log.
// Always writes under package external data: …/Android/data/<pkg>/Logs/crash.log
// Also mirrors short lines to logcat (chunked).

using System;
using System.IO;
using System.Text;
using Android.App;
using ALog = global::Android.Util.Log;

namespace OpenRA.Android
{
	public static class CrashReporter
	{
		const int LogcatChunk = 3500;
		static readonly object Gate = new();

		public static string CrashLogPath { get; private set; }

		/// <summary>Resolve …/Android/data/&lt;pkg&gt;/Logs (same root as SupportDir).</summary>
		public static string ResolveLogsDir()
		{
			try
			{
				var support = ContentBootstrap.SupportDir;
				if (!string.IsNullOrEmpty(support))
					return Path.Combine(support, "Logs");
			}
			catch { /* order */ }

			try
			{
				var ctx = Application.Context;
				var files = ctx.GetExternalFilesDir(null)?.AbsolutePath;
				if (!string.IsNullOrEmpty(files))
				{
					var parent = Directory.GetParent(files)?.FullName;
					if (!string.IsNullOrEmpty(parent))
						return Path.Combine(parent, "Logs");
				}
			}
			catch { /* ignore */ }

			try
			{
				var internalFiles = Application.Context.FilesDir?.AbsolutePath;
				if (!string.IsNullOrEmpty(internalFiles))
				{
					var parent = Directory.GetParent(internalFiles)?.FullName;
					if (!string.IsNullOrEmpty(parent))
						return Path.Combine(parent, "Logs");
				}
			}
			catch { /* ignore */ }

			try
			{
				return Path.Combine(Application.Context.FilesDir.AbsolutePath, "Logs");
			}
			catch
			{
				return null;
			}
		}

		public static void Report(string source, Exception ex, bool isTerminating = false)
		{
			var text = ex != null ? ex.ToString() : "null exception";
			Report(source, text, isTerminating);
		}

		public static void Report(string source, string text, bool isTerminating = false)
		{
			text ??= "unknown";
			source ??= "Crash";

			var header = DateTime.UtcNow.ToString("o")
				+ " pid=" + global::Android.OS.Process.MyPid()
				+ " terminating=" + isTerminating
				+ " source=" + source;

			// logcat (chunked)
			try
			{
				ALog.Error("OpenRA.Crash", header);
				for (var i = 0; i < text.Length; i += LogcatChunk)
				{
					var len = Math.Min(LogcatChunk, text.Length - i);
					var part = (i / LogcatChunk) + 1;
					var total = (text.Length + LogcatChunk - 1) / LogcatChunk;
					ALog.Error("OpenRA.Crash", "[" + part + "/" + total + "] " + text.Substring(i, len));
				}
			}
			catch { /* ignore */ }

			// BootLog (may truncate very long lines — still useful early)
			try
			{
				BootLog.Error(header);
				if (text.Length <= LogcatChunk)
					BootLog.Error(text);
				else
					BootLog.Error(text.Substring(0, LogcatChunk) + "…(see crash.log)");
			}
			catch { /* ignore */ }

			// Durable file
			lock (Gate)
			{
				try
				{
					var logs = ResolveLogsDir();
					if (string.IsNullOrEmpty(logs))
						return;
					Directory.CreateDirectory(logs);
					CrashLogPath = Path.Combine(logs, "crash.log");
					var sb = new StringBuilder(text.Length + 256);
					sb.AppendLine(header);
					sb.AppendLine(text);
					sb.AppendLine("---");
					sb.AppendLine();
					File.AppendAllText(CrashLogPath, sb.ToString(), Encoding.UTF8);
				}
				catch (Exception e)
				{
					try { ALog.Error("OpenRA.Crash", "Write crash.log failed: " + e.Message); }
					catch { /* ignore */ }
				}
			}

			// Official engine channel when ready
			try
			{
				AndroidPlatformLog.Error("OpenRA.Crash", header + "\n" + text);
			}
			catch { /* ignore */ }
		}
	}
}
