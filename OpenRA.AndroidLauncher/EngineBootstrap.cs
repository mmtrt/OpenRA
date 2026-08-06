// Bootstrap — paths, content extract, PlatformFactory, safe engine start.

using System;
using System.IO;
using System.Threading;
using Android.App;
using OpenRA;
using OpenRA.Platforms.Android;

namespace OpenRA.Android
{
	public static class EngineBootstrap
	{
		public static bool IsRunning { get; private set; }
		public static string SupportDir { get; private set; }
		public static string CacheDir { get; private set; }

		static Thread gameThread;
		static int startAttempts;

		public static void Start(GameSurfaceView surface, string mod = "ra")
		{
			if (IsRunning)
				return;

			try
			{
				var context = Application.Context;
				SupportDir = context.GetExternalFilesDir(null)?.AbsolutePath
					?? context.FilesDir?.AbsolutePath
					?? Path.Combine(context.ApplicationInfo.DataDir, "files");
				SupportDir = Path.Combine(SupportDir, "OpenRA");
				CacheDir = context.CacheDir?.AbsolutePath
					?? Path.Combine(context.ApplicationInfo.DataDir, "cache");

				AndroidFileLog.Init();
				AndroidFileLog.Info("OpenRA.Bootstrap", $"Start attempt={++startAttempts} mod={mod}");

				Directory.CreateDirectory(SupportDir);
				Directory.CreateDirectory(CacheDir);

				AndroidNativeBootstrap.Init();
				ContentBootstrap.EnsureLayout(SupportDir);

				Platform.AndroidFilesDir = SupportDir;
				Platform.AndroidCacheDir = CacheDir;
				Game.PlatformFactory = () => new AndroidPlatform();

				if (!ContentBootstrap.HasAnyMod())
				{
					AndroidFileLog.Warn("OpenRA.Bootstrap",
						"No mod.yaml under mods/ — engine will likely fail. " +
						"Package mods into Assets/mods or copy onto device under SupportDir/mods.");
				}

				IsRunning = true;
				gameThread = new Thread(() => RunEngine(mod))
				{
					IsBackground = true,
					Name = "OpenRA.Game"
				};
				gameThread.Start();
			}
			catch (Exception e)
			{
				IsRunning = false;
				AndroidFileLog.Exception("OpenRA.Bootstrap", e);
			}
		}

		static void RunEngine(string mod)
		{
			try
			{
				AndroidFileLog.Info("OpenRA.Bootstrap", "Game thread enter");
				if (!AndroidEgl.MakeCurrent())
					AndroidFileLog.Warn("OpenRA.Bootstrap", "EGL MakeCurrent failed: " + AndroidEgl.LastError);

				// EngineDir should contain VERSION + mods relative paths
				var engineDir = SupportDir;
				var versionPath = Path.Combine(engineDir, "VERSION");
				if (!File.Exists(versionPath))
				{
					File.WriteAllText(versionPath, "android-port-dev");
					AndroidFileLog.Info("OpenRA.Bootstrap", "Wrote placeholder VERSION");
				}

				var args = new[]
				{
					"Engine.Platform=Android",
					"Game.Mod=" + mod,
					"Engine.SupportDir=" + SupportDir,
					"Engine.EngineDir=" + engineDir,
					"Engine.ModSearchPaths=" + ContentBootstrap.ModsDir
				};

				AndroidFileLog.Info("OpenRA.Bootstrap", "InitializeAndRun " + string.Join(" ", args));
				Game.InitializeAndRun(args);
				AndroidFileLog.Info("OpenRA.Bootstrap", "InitializeAndRun returned");
			}
			catch (Exception e)
			{
				AndroidFileLog.Exception("OpenRA.Bootstrap", e);
			}
			finally
			{
				IsRunning = false;
				AndroidFileLog.Info("OpenRA.Bootstrap", "Game thread exit");
			}
		}

		public static void Stop()
		{
			try { Game.Exit(); }
			catch (Exception e) { AndroidFileLog.Exception("OpenRA.Bootstrap.Stop", e); }
			IsRunning = false;
			MainActivity.PlatformWindow = null;
		}
	}
}
