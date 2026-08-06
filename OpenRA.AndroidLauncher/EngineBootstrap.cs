// Bootstrap — path injection, PlatformFactory, Game.InitializeAndRun.

using System;
using System.IO;
using System.Threading;
using Android.App;
using OpenRA;
using OpenRA.Platforms.Android;
using ALog = global::Android.Util.Log;

namespace OpenRA.Android
{
	public static class EngineBootstrap
	{
		public static bool IsRunning { get; private set; }
		public static string SupportDir { get; private set; }
		public static string CacheDir { get; private set; }

		const string ContentReadyMarker = ".content_ready";
		static Thread gameThread;

		public static void Start(GameSurfaceView surface, string mod = "ra")
		{
			if (IsRunning)
				return;

			var context = Application.Context;
			SupportDir = context.FilesDir?.AbsolutePath
				?? Path.Combine(context.ApplicationInfo.DataDir, "files");
			CacheDir = context.CacheDir?.AbsolutePath
				?? Path.Combine(context.ApplicationInfo.DataDir, "cache");

			Directory.CreateDirectory(SupportDir);
			Directory.CreateDirectory(CacheDir);
			Directory.CreateDirectory(Path.Combine(SupportDir, "maps"));
			Directory.CreateDirectory(Path.Combine(SupportDir, "Replays"));
			Directory.CreateDirectory(Path.Combine(SupportDir, "Content"));

			ALog.Info("OpenRA.Bootstrap", $"SupportDir={SupportDir}");
			ALog.Info("OpenRA.Bootstrap", $"CacheDir={CacheDir}");

			AndroidNativeBootstrap.Init();

			Platform.AndroidFilesDir = SupportDir;
			Platform.AndroidCacheDir = CacheDir;
			Game.PlatformFactory = () => new AndroidPlatform();

			if (!IsContentReady())
				ALog.Info("OpenRA.Bootstrap", "Content not ready — first-launch path");

			IsRunning = true;

			gameThread = new Thread(() =>
			{
				try
				{
					ALog.Info("OpenRA.Bootstrap", $"InitializeAndRun mod={mod}");
					if (!AndroidEgl.MakeCurrent())
						ALog.Warn("OpenRA.Bootstrap", "EGL MakeCurrent on game thread failed: " + AndroidEgl.LastError);

					Game.InitializeAndRun(new[]
					{
						"Engine.Platform=Android",
						"Game.Mod=" + mod,
						"Engine.SupportDir=" + SupportDir
					});
				}
				catch (Exception e)
				{
					ALog.Error("OpenRA.Bootstrap", $"Engine failed: {e}");
				}
				finally
				{
					IsRunning = false;
					ALog.Info("OpenRA.Bootstrap", "Engine exited");
				}
			})
			{
				IsBackground = true,
				Name = "OpenRA.Game"
			};
			gameThread.Start();
		}

		public static bool IsContentReady()
		{
			return File.Exists(Path.Combine(SupportDir ?? "", "Content", ContentReadyMarker));
		}

		public static void MarkContentReady()
		{
			var path = Path.Combine(SupportDir, "Content", ContentReadyMarker);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
		}

		public static void Stop()
		{
			try { Game.Exit(); } catch { /* engine may not be up */ }
			IsRunning = false;
			MainActivity.PlatformWindow = null;
		}
	}
}
