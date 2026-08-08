#region Copyright & License Information
/*
 * Platform-layer logging. Always mirrors to Android logcat.
 * Once EngineBootstrap calls MarkEngineLogReady(), lines also go through
 * OpenRA.Support.Log (official SupportDir/Logs/*.log channels).
 */
#endregion

using System;
using OpenRA.Support;
using ALog = global::Android.Util.Log;

namespace OpenRA.Platforms.Android
{
	public static class AndroidPlatformLog
	{
		static volatile bool engineLogReady;

		/// <summary>
		/// Call after Platform.OverrideSupportDir + Log.AddChannel for android/debug/graphics.
		/// </summary>
		public static void MarkEngineLogReady() => engineLogReady = true;

		public static bool IsEngineLogReady => engineLogReady;

		public static void Info(string tag, string message) => Write("INFO", tag, message);
		public static void Warn(string tag, string message) => Write("WARN", tag, message);
		public static void Error(string tag, string message) => Write("ERROR", tag, message);

		public static void Exception(string tag, Exception ex)
			=> Error(tag, ex?.ToString() ?? "null exception");

		static void Write(string level, string tag, string message)
		{
			message ??= "";
			tag ??= "OpenRA";

			// Always logcat (visible without pulling SupportDir files)
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

			if (!engineLogReady)
				return;

			try
			{
				var channel = ChannelForTag(tag);
				var line = level == "INFO"
					? $"[{tag}] {message}"
					: $"[{level}] [{tag}] {message}";
				Log.Write(channel, line);
			}
			catch (Exception e)
			{
				try { ALog.Warn("OpenRA.Log", "Log.Write failed: " + e.Message); }
				catch { /* ignore */ }
			}
		}

		/// <summary>Map Android tags onto official engine log channels.</summary>
		static string ChannelForTag(string tag)
		{
			if (string.IsNullOrEmpty(tag))
				return "android";

			// graphics.log — GL / EGL / shaders / Present
			if (tag.Contains("EGL", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("GL", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Shader", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Surface", StringComparison.OrdinalIgnoreCase))
				return "graphics";

			// sound.log
			if (tag.Contains("Sound", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Audio", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("OpenAL", StringComparison.OrdinalIgnoreCase))
				return "sound";

			// debug.log — native libs, bootstrap diagnostics
			if (tag.Contains("Native", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Bootstrap", StringComparison.OrdinalIgnoreCase)
			    || tag.Contains("Crash", StringComparison.OrdinalIgnoreCase))
				return "debug";

			// android.log — launcher UI, content install, general
			return "android";
		}
	}
}
