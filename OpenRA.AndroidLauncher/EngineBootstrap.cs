// Bootstrap — public SupportDir, content extract, PlatformFactory, safe engine start.

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
				AndroidFileLog.Init();
				AndroidFileLog.Info("OpenRA.Bootstrap", $"Start attempt={++startAttempts} mod={mod}");

				// Prefer /storage/emulated/0/OpenRA (survives uninstall)
				ContentBootstrap.EnsureLayout(null);
				SupportDir = ContentBootstrap.SupportDir;
				CacheDir = Path.Combine(SupportDir, "Cache");
				Directory.CreateDirectory(CacheDir);

				AndroidNativeBootstrap.Init();

				Platform.AndroidFilesDir = SupportDir;
				Platform.AndroidCacheDir = CacheDir;
				Game.PlatformFactory = () => new AndroidPlatform();

				if (!ContentBootstrap.HasAnyMod())
				{
					AndroidFileLog.Warn("OpenRA.Bootstrap",
						"No mod.yaml under mods/. Build with package-android-assets.sh or adb push mods.");
					AndroidFileLog.Warn("OpenRA.Bootstrap", "Skipping InitializeAndRun until mods are present.");
					IsRunning = false;
					return;
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

				var engineDir = SupportDir;
				var versionPath = Path.Combine(engineDir, "VERSION");
				if (!File.Exists(versionPath))
					File.WriteAllText(versionPath, "android-port-dev");

				// Log channel files under SupportDir/Logs (engine-native path)
				Directory.CreateDirectory(Path.Combine(SupportDir, "Logs"));

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
