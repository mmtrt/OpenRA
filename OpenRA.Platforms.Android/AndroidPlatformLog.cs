#region Copyright & License Information
/*
 * Platform-layer logging. Always logcat.
 * After MarkEngineLogReady(), also OpenRA.Log → SupportDir/Logs/*.log
 * All OpenRA.Log access is behind try/catch so a broken Game assembly cannot
 * take down the process during logging.
 *
 * logcat truncates ~4KB per message — long exceptions are chunked and also
 * written to SupportDir/Logs/crash.log when available.
 */
#endregion

using System;
using System.IO;
using System.Text;
using ALog = global::Android.Util.Log;

namespace OpenRA.Platforms.Android
{
	public static class AndroidPlatformLog
	{
		const int LogcatChunk = 3500;

		static volatile bool engineLogReady;
		static volatile bool engineLogBroken;

		public static void MarkEngineLogReady() => engineLogReady = true;

		public static bool IsEngineLogReady => engineLogReady && !engineLogBroken;

		public static void Info(string tag, string message) => Write("INFO", tag, message);
		public static void Warn(string tag, string message) => Write("WARN", tag, message);
		public static void Error(string tag, string message) => Write("ERROR", tag, message);

		public static void Exception(string tag, Exception ex)
		{
			var text = ex?.ToString() ?? "null exception";
			Error(tag, text);
			TryWriteCrashFile(tag, text);
		}

		static void TryWriteCrashFile(string tag, string text)
		{
			try
			{
				string dir = null;
				try
				{
					// Prefer Platform.SupportDir when engine has set it
					dir = global::OpenRA.Platform.SupportDir;
				}
				catch { /* not ready */ }

				if (string.IsNullOrEmpty(dir))
				{
					try
					{
						var ctx = global::Android.App.Application.Context;
						var files = ctx.GetExternalFilesDir(null)?.AbsolutePath;
						if (!string.IsNullOrEmpty(files))
						{
							var parent = Directory.GetParent(files)?.FullName;
							if (!string.IsNullOrEmpty(parent))
								dir = parent;
						}
					}
					catch { /* ignore */ }
				}

				if (string.IsNullOrEmpty(dir))
					return;

				var logs = Path.Combine(dir, "Logs");
				Directory.CreateDirectory(logs);
				var path = Path.Combine(logs, "crash.log");
				var block = DateTime.UtcNow.ToString("o") + " [" + tag + "]\n" + text + "\n\n";
				File.AppendAllText(path, block, Encoding.UTF8);
			}
			catch { /* never throw from logging */ }
		}

		static void Write(string level, string tag, string message)
		{
			message ??= "";
			tag ??= "OpenRA";

			// Chunk for logcat (~4KB limit)
			try
			{
				if (message.Length <= LogcatChunk)
				{
					LogcatOne(level, tag, message);
				}
				else
				{
					var total = (message.Length + LogcatChunk - 1) / LogcatChunk;
					for (var i = 0; i < total; i++)
					{
						var start = i * LogcatChunk;
						var len = Math.Min(LogcatChunk, message.Length - start);
						LogcatOne(level, tag, "[" + (i + 1) + "/" + total + "] " + message.Substring(start, len));
					}
				}
			}
			catch { /* ignore */ }

			if (!engineLogReady || engineLogBroken)
				return;

			try
			{
				var channel = ChannelForTag(tag);
				// OpenRA.Log also has practical line limits — chunk long payloads
				if (message.Length <= LogcatChunk)
				{
					var line = level == "INFO"
						? "[" + tag + "] " + message
						: "[" + level + "] [" + tag + "] " + message;
					global::OpenRA.Log.Write(channel, line);
				}
				else
				{
					var total = (message.Length + LogcatChunk - 1) / LogcatChunk;
					for (var i = 0; i < total; i++)
					{
						var start = i * LogcatChunk;
						var len = Math.Min(LogcatChunk, message.Length - start);
						var piece = message.Substring(start, len);
						var line = level == "INFO"
							? "[" + tag + "] [" + (i + 1) + "/" + total + "] " + piece
							: "[" + level + "] [" + tag + "] [" + (i + 1) + "/" + total + "] " + piece;
						global::OpenRA.Log.Write(channel, line);
					}
				}
			}
			catch (Exception e)
			{
				engineLogBroken = true;
				try { ALog.Warn("OpenRA.Log", "Disabling engine Log mirror: " + e.GetType().Name + ": " + e.Message); }
				catch { /* ignore */ }
			}
		}

		static void LogcatOne(string level, string tag, string message)
		{
			if (level == "ERROR")
				ALog.Error(tag, message);
			else if (level == "WARN")
				ALog.Warn(tag, message);
			else
				ALog.Info(tag, message);
		}

		static string ChannelForTag(string tag)
		{
			if (string.IsNullOrEmpty(tag))
				return "android";

			if (tag.Contains("EGL", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("GL", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Shader", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Surface", StringComparison.OrdinalIgnoreCase))
				return "graphics";

			if (tag.Contains("Sound", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Audio", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("OpenAL", StringComparison.OrdinalIgnoreCase))
				return "sound";

			if (tag.Contains("Native", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Bootstrap", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Crash", StringComparison.OrdinalIgnoreCase))
				return "debug";

			return "android";
		}
	}
}
