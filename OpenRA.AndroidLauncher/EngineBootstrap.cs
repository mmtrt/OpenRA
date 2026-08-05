// Bootstrap — path injection, first-launch marker, future Game.InitializeAndRun.
// arm64-v8a prototype.

using System;
using System.IO;
using Android.App;
using Android.Util;

namespace OpenRA.Android
{
	public static class EngineBootstrap
	{
		public static bool IsRunning { get; private set; }
		public static string SupportDir { get; private set; }
		public static string CacheDir { get; private set; }

		const string ContentReadyMarker = ".content_ready";

		/// <summary>
		/// Call once the GameSurfaceView reports IsSurfaceReady.
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

			// When Platform.cs patch is applied:
			// OpenRA.Platform.AndroidFilesDir = SupportDir;
			// OpenRA.Platform.AndroidCacheDir  = CacheDir;

			if (!IsContentReady())
			{
				Log.Info("OpenRA.Bootstrap", "Content not ready — first-launch install path");
				// TODO: extract minimal assets from APK or start download
				// MarkContentReady() after successful install
			}

			// TODO after multi-target:
			// var platform = new OpenRA.Platforms.Android.AndroidPlatform();
			// var window = platform.CreateWindow(...);
			// MainActivity.PlatformWindow = (AndroidPlatformWindow)window;
			// OpenRA.Game.InitializeAndRun(new[] {
			//   "Engine.EngineDir=" + SupportDir,
			//   "Engine.Platform=Android",
			//   "Game.Mod=" + mod
			// });

			IsRunning = true;
			Log.Info("OpenRA.Bootstrap", $"Bootstrap stub complete (mod={mod}) — engine call pending multi-target");
		}

		public static bool IsContentReady()
		{
			return File.Exists(Path.Combine(SupportDir ?? "", "Content", ContentReadyMarker));
		}

		public static void MarkContentReady()
		{
			var path = Path.Combine(SupportDir, "Content", ContentReadyMarker);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
		}

		public static void Stop()
		{
			IsRunning = false;
			MainActivity.PlatformWindow = null;
		}
	}
}
