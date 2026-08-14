// Ultra-early boot log. Never references OpenRA.Game / OpenRA.Log.
// Survives crashes that happen before SupportDir + official Log channels exist.
// Path matches StorageAccess SupportDir: …/Android/data/<pkg>/Logs/boot.log
// (not the legacy …/files/OpenRA/Logs/ path).

using System;
using System.IO;
using System.Text;
using Android.App;
using ALog = global::Android.Util.Log;

namespace OpenRA.Android
{
	public static class BootLog
	{
		static readonly object Gate = new();
		static StreamWriter writer;
		static bool init;

		public static string Path { get; private set; }

		public static void Init()
		{
			lock (Gate)
			{
				if (init)
					return;
				init = true;

				try
				{
					var dir = ResolveLogsDir();
					Directory.CreateDirectory(dir);
					Path = System.IO.Path.Combine(dir, "boot.log");
					writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
					{
						AutoFlush = true
					};
					Write("INFO", "BootLog ready path=" + Path + " pid=" + global::Android.OS.Process.MyPid());
				}
				catch (Exception e)
				{
					try { ALog.Error("OpenRA.Boot", "BootLog.Init failed: " + e); }
					catch { /* ignore */ }
				}
			}
		}

		/// <summary>
		/// Same root as StorageAccess.ResolveSupportDir: package external data dir
		/// (parent of GetExternalFilesDir), then Logs/. Fallback: internal files parent/Logs.
		/// </summary>
		static string ResolveLogsDir()
		{
			var ctx = Application.Context;

			// Prefer already-locked SupportDir when bootstrap has run.
			try
			{
				var support = ContentBootstrap.SupportDir;
				if (!string.IsNullOrEmpty(support))
					return System.IO.Path.Combine(support, "Logs");
			}
			catch { /* type init order */ }

			try
			{
				var filesDir = ctx.GetExternalFilesDir(null)?.AbsolutePath;
				if (!string.IsNullOrEmpty(filesDir))
				{
					var parent = Directory.GetParent(filesDir)?.FullName;
					if (!string.IsNullOrEmpty(parent))
						return System.IO.Path.Combine(parent, "Logs");
				}
			}
			catch { /* ignore */ }

			try
			{
				var internalFiles = ctx.FilesDir?.AbsolutePath;
				if (!string.IsNullOrEmpty(internalFiles))
				{
					var parent = Directory.GetParent(internalFiles)?.FullName;
					if (!string.IsNullOrEmpty(parent))
						return System.IO.Path.Combine(parent, "Logs");
				}
			}
			catch { /* ignore */ }

			// Last resort — still not files/OpenRA
			return System.IO.Path.Combine(ctx.FilesDir.AbsolutePath, "Logs");
		}

		public static void Info(string msg) => Write("INFO", msg);
		public static void Warn(string msg) => Write("WARN", msg);
		public static void Error(string msg) => Write("ERROR", msg);
		public static void Exception(string msg, Exception ex) => Write("ERROR", msg + " " + ex);

		static void Write(string level, string message)
		{
			var line = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
			try
			{
				if (level == "ERROR") ALog.Error("OpenRA.Boot", message ?? "");
				else if (level == "WARN") ALog.Warn("OpenRA.Boot", message ?? "");
				else ALog.Info("OpenRA.Boot", message ?? "");
			}
			catch { /* ignore */ }

			lock (Gate)
			{
				if (!init)
					Init();
				try { writer?.WriteLine(line); }
				catch { /* ignore */ }
			}
		}
	}
}
