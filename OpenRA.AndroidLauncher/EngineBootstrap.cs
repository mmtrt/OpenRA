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
		static GameSurfaceView boundSurface;

		public static void Start(GameSurfaceView surface, string mod = "ra")
		{
			if (IsRunning)
				return;

			boundSurface = surface;

			try
			{
				AndroidFileLog.Init();
				AndroidFileLog.Info("OpenRA.Bootstrap", $"Start attempt={++startAttempts} mod={mod}");

				// Use already-locked SupportDir — do not re-resolve mid-session
				ContentBootstrap.EnsureLayout(ContentBootstrap.SupportDir ?? StorageAccess.ResolveSupportDir());
				SupportDir = ContentBootstrap.SupportDir;
				CacheDir = Path.Combine(SupportDir, "Cache");
				Directory.CreateDirectory(CacheDir);

				ContentBootstrap.PlaceAssembliesForLoader();
				AndroidNativeBootstrap.Init();

				Platform.AndroidFilesDir = SupportDir;
				Platform.AndroidCacheDir = CacheDir;
				Game.PlatformFactory = () => new AndroidPlatform();

				if (!ContentBootstrap.HasAnyMod())
				{
					AndroidFileLog.Warn("OpenRA.Bootstrap", "No mod.yaml — not starting.");
					return;
				}

				if (surface == null || !surface.IsSurfaceReady)
				{
					AndroidFileLog.Warn("OpenRA.Bootstrap", "Surface/EGL not ready — defer start");
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

				try { Directory.SetCurrentDirectory(SupportDir); }
				catch (Exception e) { AndroidFileLog.Warn("OpenRA.Bootstrap", "cwd: " + e.Message); }

				AndroidFileLog.Info("OpenRA.Bootstrap", "cwd=" + Directory.GetCurrentDirectory());

				// Ensure GL is current on THIS thread
				if (!EnsureEglCurrent())
				{
					AndroidFileLog.Error("OpenRA.Bootstrap", "Cannot bind EGL — abort engine start");
					return;
				}

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

		static bool EnsureEglCurrent()
		{
			if (AndroidEgl.MakeCurrent())
			{
				AndroidFileLog.Info("OpenRA.Bootstrap", "EGL MakeCurrent ok");
				return true;
			}

			AndroidFileLog.Warn("OpenRA.Bootstrap", "EGL MakeCurrent failed: " + AndroidEgl.LastError);

			// Re-create from surface if display was destroyed (e.g. OnPause/SurfaceDestroyed)
			var surface = boundSurface;
			if (surface != null)
			{
				AndroidFileLog.Info("OpenRA.Bootstrap", "Re-initializing EGL from surface…");
				try
				{
					// SurfaceView.Holder must be used on UI thread typically — try anyway
					var holder = surface.Holder;
					var w = Math.Max(1, surface.SurfaceWidth);
					var h = Math.Max(1, surface.SurfaceHeight);
					if (AndroidEgl.Initialize(holder, w, h) && AndroidEgl.MakeCurrent())
					{
						AndroidFileLog.Info("OpenRA.Bootstrap", "EGL re-init + MakeCurrent ok");
						return true;
					}
					AndroidFileLog.Error("OpenRA.Bootstrap", "EGL re-init failed: " + AndroidEgl.LastError);
				}
				catch (Exception e)
				{
					AndroidFileLog.Exception("OpenRA.Bootstrap.EGL", e);
				}
			}

			return false;
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
