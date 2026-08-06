// Bootstrap — path injection, PlatformFactory, Game.InitializeAndRun.
// arm64-v8a prototype.

using System;
using System.IO;
using System.Threading;
using Android.App;
using Android.Util;
using OpenRA;
using OpenRA.Platforms.Android;

namespace OpenRA.Android
{
	public static class EngineBootstrap
	{
		public static bool IsRunning { get; private set; }
		public static string SupportDir { get; private set; }
		public static string CacheDir { get; private set; }

		const string ContentReadyMarker = ".content_ready";
		static Thread gameThread;

		/// <summary>
		/// Call once GameSurfaceView reports IsSurfaceReady.
		/// Sets Android support paths, registers PlatformFactory, starts the engine.
		/// </summary>
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

			Log.Info("OpenRA.Bootstrap", $"SupportDir={SupportDir}");
			Log.Info("OpenRA.Bootstrap", $"CacheDir={CacheDir}");

			// Inject Android paths before any Platform.SupportDir access
			// Load arm64 .so packages before any P/Invoke
			OpenRA.Platforms.Android.AndroidNativeBootstrap.Init();

			Platform.AndroidFilesDir = SupportDir;
			Platform.AndroidCacheDir = CacheDir;

			// Register platform without OpenRA.Game → Platforms.Android project reference
			Game.PlatformFactory = () => new AndroidPlatform();

			if (!IsContentReady())
			{
				Log.Info("OpenRA.Bootstrap", "Content not ready — first-launch path");
				// Placeholder: mark ready so later boots skip until real content pipeline exists
				// MarkContentReady();
			}

			IsRunning = true;

			// Run the engine off the UI thread so MotionEvents keep flowing.
			// NOTE: Real GLES requires the GL context on the correct thread;
			// this will need alignment once EGL is implemented on GameSurfaceView.
			gameThread = new Thread(() =>
			{
				try
				{
					Log.Info("OpenRA.Bootstrap", $"InitializeAndRun mod={mod}");
					// Bind EGL to the game thread before the renderer starts
					if (!OpenRA.Platforms.Android.AndroidEgl.MakeCurrent())
						Log.Warn("OpenRA.Bootstrap", "EGL MakeCurrent on game thread failed: " + OpenRA.Platforms.Android.AndroidEgl.LastError);
					Game.InitializeAndRun(new[]
					{
						"Engine.Platform=Android",
						"Game.Mod=" + mod,
						"Engine.SupportDir=" + SupportDir
					});
				}
				catch (Exception e)
				{
					Log.Error("OpenRA.Bootstrap", $"Engine failed: {e}");
				}
				finally
				{
					IsRunning = false;
					Log.Info("OpenRA.Bootstrap", "Engine exited");
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
