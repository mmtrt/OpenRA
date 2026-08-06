// Logs to openra.log + error.log under OpenRa/ (public + app-external + internal).

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
						// touch both log files
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
			var w = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
			Writers.Add(w);
		}

		static IEnumerable<string> CandidateDirs()
		{
			// 1) App-specific external — no special permission on modern Android
			try
			{
				var ext = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
				if (!string.IsNullOrEmpty(ext))
					yield return Path.Combine(ext, PublicDirName);
			}
			catch { /* ignore */ }

			// 2) Public /storage/emulated/0/OpenRa (needs storage permission / all-files access)
			yield return PreferredPublicDir;

			// 3) Internal app files
			try
			{
				var internalRoot = Application.Context.FilesDir?.AbsolutePath;
				if (!string.IsNullOrEmpty(internalRoot))
					yield return Path.Combine(internalRoot, PublicDirName);
			}
			catch { /* ignore */ }
		}

		public static void Info(string tag, string message) => WriteLine("INFO", tag, message);
		public static void Warn(string tag, string message) => WriteLine("WARN", tag, message);
		public static void Error(string tag, string message) => WriteLine("ERROR", tag, message);

		public static void Exception(string tag, Exception ex)
		{
			Error(tag, ex.ToString());
		}

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
					catch { /* ignore single-writer failure */ }
				}
			}
		}
	}
}
