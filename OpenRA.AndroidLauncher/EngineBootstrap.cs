// Bootstrap — SupportDir, assemblies on disk, PlatformFactory, engine start.

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

				ContentBootstrap.EnsureLayout(null);
				SupportDir = ContentBootstrap.SupportDir;
				CacheDir = Path.Combine(SupportDir, "Cache");
				Directory.CreateDirectory(CacheDir);

				// Ensure DLLs exist where ObjectCreator File.OpenRead looks
				ContentBootstrap.PlaceAssembliesForLoader();

				AndroidNativeBootstrap.Init();

				Platform.AndroidFilesDir = SupportDir;
				Platform.AndroidCacheDir = CacheDir;
				Game.PlatformFactory = () => new AndroidPlatform();

				if (!ContentBootstrap.HasAnyMod())
				{
					AndroidFileLog.Warn("OpenRA.Bootstrap", "No mod.yaml under mods/ — not starting engine.");
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

				// Critical: assembly names resolve relative to cwd / FilesDir
				try
				{
					Directory.SetCurrentDirectory(SupportDir);
					AndroidFileLog.Info("OpenRA.Bootstrap", "cwd=" + Directory.GetCurrentDirectory());
				}
				catch (Exception e)
				{
					AndroidFileLog.Warn("OpenRA.Bootstrap", "SetCurrentDirectory: " + e.Message);
				}

				ContentBootstrap.PlaceAssembliesForLoader();

				if (!AndroidEgl.MakeCurrent())
					AndroidFileLog.Warn("OpenRA.Bootstrap", "EGL MakeCurrent failed: " + AndroidEgl.LastError);

				var versionPath = Path.Combine(SupportDir, "VERSION");
				if (!File.Exists(versionPath))
					File.WriteAllText(versionPath, "android-port-dev");

				Directory.CreateDirectory(Path.Combine(SupportDir, "Logs"));

				var args = new[]
				{
					"Engine.Platform=Android",
					"Game.Mod=" + mod,
					"Engine.SupportDir=" + SupportDir,
					"Engine.EngineDir=" + SupportDir,
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
