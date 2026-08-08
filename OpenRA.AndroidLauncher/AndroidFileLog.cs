// Compatibility facade for launcher code. Does NOT maintain a parallel openra.log.
// Routes into OpenRA.Platforms.Android.AndroidPlatformLog → OpenRA.Log (official).

using System;
using OpenRA.Platforms.Android;

namespace OpenRA.Android
{
	/// <summary>
	/// Thin alias kept so existing call sites compile. All durable logs go through
	/// the engine <see cref="OpenRA.Log"/> channels under SupportDir/Logs/.
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
		/// Best-effort flush of engine Log writers (private FlushToDisk via reflection).
		/// </summary>
		public static void Flush()
		{
			try
			{
				// Log lives in namespace OpenRA (OpenRA.Game), not OpenRA.Support.
				var t = typeof(global::OpenRA.Log);
				var m = t.GetMethod("FlushToDisk",
					System.Reflection.BindingFlags.Public
					| System.Reflection.BindingFlags.NonPublic
					| System.Reflection.BindingFlags.Static);
				if (m == null)
					return;
				var ps = m.GetParameters();
				m.Invoke(null, ps.Length == 0 ? null : new object[] { null });
			}
			catch { /* official Log has no public flush — ignore */ }
		}
	}
}
