// Logs to openra.log + error.log under OpenRa/ (app-external first, then public, then internal).

using System;
using System.Collections.Generic;
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
		public const string MainLogName = "openra.log";
		public const string ErrorLogName = "error.log";

		static readonly object Gate = new();
		static readonly List<StreamWriter> Writers = new();
		static string primaryDir;
		static bool initialized;

		public static string ActiveDir => primaryDir;
		public static string PreferredPublicDir =>
			Path.Combine(AEnv.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0", PublicDirName);

		public static void Init()
		{
			lock (Gate)
			{
				if (initialized)
					return;
				initialized = true;

				foreach (var dir in CandidateDirs())
				{
					try
					{
						Directory.CreateDirectory(dir);
						OpenWriter(Path.Combine(dir, MainLogName));
						OpenWriter(Path.Combine(dir, ErrorLogName));
						if (primaryDir == null)
							primaryDir = dir;
						ALog.Info("OpenRA.Log", "Logging directory: " + dir);
					}
					catch (Exception e)
					{
						ALog.Warn("OpenRA.Log", "Cannot use log dir " + dir + ": " + e.Message);
					}
				}

				WriteLine("INFO", "OpenRA.Log",
					$"start utc={DateTime.UtcNow:o} pid={AProcess.MyPid()} primary={primaryDir}");
			}
		}

		static void OpenWriter(string path)
		{
			var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
			Writers.Add(new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true });
		}

		static List<string> CandidateDirs()
		{
			var list = new List<string>();

			try
			{
				var ext = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
				if (!string.IsNullOrEmpty(ext))
					list.Add(Path.Combine(ext, PublicDirName));
			}
			catch { /* ignore */ }

			list.Add(PreferredPublicDir);

			try
			{
				var internalRoot = Application.Context.FilesDir?.AbsolutePath;
				if (!string.IsNullOrEmpty(internalRoot))
					list.Add(Path.Combine(internalRoot, PublicDirName));
			}
			catch { /* ignore */ }

			return list;
		}

		public static void Info(string tag, string message) => WriteLine("INFO", tag, message);
		public static void Warn(string tag, string message) => WriteLine("WARN", tag, message);
		public static void Error(string tag, string message) => WriteLine("ERROR", tag, message);
		public static void Exception(string tag, Exception ex) => Error(tag, ex.ToString());

		public static void WriteLine(string level, string tag, string message)
		{
			if (level == "ERROR")
				ALog.Error(tag, message);
			else if (level == "WARN")
				ALog.Warn(tag, message);
			else
				ALog.Info(tag, message);

			lock (Gate)
			{
				if (!initialized)
					Init();

				var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {tag}: {message}";
				foreach (var w in Writers)
				{
					try { w.WriteLine(line); }
					catch { /* ignore */ }
				}
			}
		}
	}
}
