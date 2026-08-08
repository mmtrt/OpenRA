// Compatibility facade for launcher code. Does NOT maintain a parallel openra.log.
// Routes into OpenRA.Platforms.Android.AndroidPlatformLog → OpenRA.Support.Log.

using System;
using OpenRA.Platforms.Android;
using ALog = global::Android.Util.Log;

namespace OpenRA.Android
{
	/// <summary>
	/// Thin alias kept so existing call sites compile. All durable logs go through
	/// the engine <see cref="OpenRA.Support.Log"/> channels under SupportDir/Logs/.
	/// </summary>
	public static class AndroidFileLog
	{
		/// <summary>Legacy path name; engine logs live in SupportDir/Logs/.</summary>
		public static string ActiveDir => ContentBootstrap.SupportDir != null
			? System.IO.Path.Combine(ContentBootstrap.SupportDir, "Logs")
			: null;

		public static void Init()
		{
			// No-op: official Log channels are created in EngineBootstrap.InitOfficialLogging.
			// Keep method so early OnCreate call sites remain valid.
		}

		public static void Info(string tag, string message)
			=> AndroidPlatformLog.Info(tag, message);

		public static void Warn(string tag, string message)
			=> AndroidPlatformLog.Warn(tag, message);

		public static void Error(string tag, string message)
			=> AndroidPlatformLog.Error(tag, message);

		public static void Exception(string tag, Exception ex)
			=> AndroidPlatformLog.Exception(tag, ex);

		public static void WriteLine(string level, string tag, string message)
		{
			if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
				AndroidPlatformLog.Error(tag, message);
			else if (string.Equals(level, "WARN", StringComparison.OrdinalIgnoreCase))
				AndroidPlatformLog.Warn(tag, message);
			else
				AndroidPlatformLog.Info(tag, message);
		}

		/// <summary>
		/// Best-effort: engine Log flushes on its own timer. No parallel file handles.
		/// </summary>
		public static void Flush()
		{
			try
			{
				// Reflect optional Log.FlushToDisk if fork exposes it; otherwise no-op.
				var t = typeof(OpenRA.Support.Log);
				var m = t.GetMethod("FlushToDisk",
					System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
				m?.Invoke(null, m.GetParameters().Length == 0 ? null : new object[] { null });
			}
			catch { /* official Log has no public flush — ignore */ }
		}
	}
}
