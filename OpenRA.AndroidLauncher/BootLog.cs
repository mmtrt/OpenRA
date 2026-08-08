// Ultra-early boot log. Never references OpenRA.Game / OpenRA.Log.
// Survives crashes that happen before SupportDir + official Log channels exist.
// File: Context.FilesDir/boot.log  and  external files OpenRA/Logs/boot.log

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
					var ctx = Application.Context;
					// Prefer external app files (user can pull without root)
					string dir = null;
					try
					{
						dir = ctx.GetExternalFilesDir(null)?.AbsolutePath;
						if (!string.IsNullOrEmpty(dir))
							dir = System.IO.Path.Combine(dir, "OpenRA", "Logs");
					}
					catch { /* ignore */ }

					if (string.IsNullOrEmpty(dir))
					{
						dir = System.IO.Path.Combine(ctx.FilesDir.AbsolutePath, "OpenRA", "Logs");
					}

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
