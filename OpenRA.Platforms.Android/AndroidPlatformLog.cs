#region Copyright & License Information
/*
 * Platform-layer logging. Always logcat.
 * After MarkEngineLogReady(), also OpenRA.Log → SupportDir/Logs/*.log
 * All OpenRA.Log access is behind reflection-free but try/catch TypeLoadException
 * so a broken Game assembly cannot take down the process during logging.
 */
#endregion

using System;
using ALog = global::Android.Util.Log;

namespace OpenRA.Platforms.Android
{
	public static class AndroidPlatformLog
	{
		static volatile bool engineLogReady;
		static volatile bool engineLogBroken;

		public static void MarkEngineLogReady() => engineLogReady = true;

		public static bool IsEngineLogReady => engineLogReady && !engineLogBroken;

		public static void Info(string tag, string message) => Write("INFO", tag, message);
		public static void Warn(string tag, string message) => Write("WARN", tag, message);
		public static void Error(string tag, string message) => Write("ERROR", tag, message);
		public static void Exception(string tag, Exception ex)
			=> Error(tag, ex?.ToString() ?? "null exception");

		static void Write(string level, string tag, string message)
		{
			message ??= "";
			tag ??= "OpenRA";

			try
			{
				if (level == "ERROR")
					ALog.Error(tag, message);
				else if (level == "WARN")
					ALog.Warn(tag, message);
				else
					ALog.Info(tag, message);
			}
			catch { /* ignore */ }

			if (!engineLogReady || engineLogBroken)
				return;

			try
			{
				// OpenRA.Log is in namespace OpenRA (OpenRA.Game assembly).
				var channel = ChannelForTag(tag);
				var line = level == "INFO"
					? "[" + tag + "] " + message
					: "[" + level + "] [" + tag + "] " + message;
				global::OpenRA.Log.Write(channel, line);
			}
			catch (Exception e)
			{
				engineLogBroken = true;
				try { ALog.Warn("OpenRA.Log", "Disabling engine Log mirror: " + e.GetType().Name + ": " + e.Message); }
				catch { /* ignore */ }
			}
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
