// First-launch: ensure SupportDir layout + extract packaged mods/assets if present.

using System;
using System.IO;
using Android.App;
using Android.Content.Res;

namespace OpenRA.Android
{
	public static class ContentBootstrap
	{
		public static string SupportDir { get; private set; }
		public static string ModsDir => Path.Combine(SupportDir, "mods");
		public static string ContentDir => Path.Combine(SupportDir, "Content");

		public static void EnsureLayout(string supportDir)
		{
			SupportDir = supportDir;
			Directory.CreateDirectory(supportDir);
			Directory.CreateDirectory(ModsDir);
			Directory.CreateDirectory(ContentDir);
			Directory.CreateDirectory(Path.Combine(supportDir, "maps"));
			Directory.CreateDirectory(Path.Combine(supportDir, "Replays"));
			Directory.CreateDirectory(Path.Combine(supportDir, "Screenshots"));

			AndroidFileLog.Info("OpenRA.Content", "SupportDir=" + supportDir);

			try
			{
				ExtractAssetsFolder("mods", ModsDir);
				ExtractAssetsFolder("Content", ContentDir);
			}
			catch (Exception e)
			{
				AndroidFileLog.Exception("OpenRA.Content", e);
			}

			// Point engine at extracted tree
			LogMods();
		}

		static void LogMods()
		{
			if (!Directory.Exists(ModsDir))
			{
				AndroidFileLog.Warn("OpenRA.Content", "No mods directory");
				return;
			}

			foreach (var d in Directory.GetDirectories(ModsDir))
				AndroidFileLog.Info("OpenRA.Content", "mod dir: " + d);
			foreach (var f in Directory.GetFiles(ModsDir, "*", SearchOption.AllDirectories))
				AndroidFileLog.Info("OpenRA.Content", "mod file: " + f);
		}

		/// <summary>
		/// Copies Assets/&lt;assetFolder&gt;/** into destDir if those assets exist in the APK.
		/// </summary>
		public static void ExtractAssetsFolder(string assetFolder, string destDir)
		{
			var assets = Application.Context.Assets;
			if (assets == null)
				return;

			ExtractRecursive(assets, assetFolder, destDir);
		}

		static void ExtractRecursive(AssetManager assets, string assetPath, string destDir)
		{
			string[] list;
			try { list = assets.List(assetPath); }
			catch { return; }

			if (list == null || list.Length == 0)
			{
				// Might be a file
				TryCopyFile(assets, assetPath, destDir);
				return;
			}

			Directory.CreateDirectory(destDir);
			foreach (var name in list)
			{
				var childAsset = string.IsNullOrEmpty(assetPath) ? name : assetPath + "/" + name;
				var childDest = Path.Combine(destDir, name);
				string[] sub;
				try { sub = assets.List(childAsset); }
				catch { sub = null; }

				if (sub != null && sub.Length > 0)
					ExtractRecursive(assets, childAsset, childDest);
				else
					TryCopyFile(assets, childAsset, childDest);
			}
		}

		static void TryCopyFile(AssetManager assets, string assetPath, string destPath)
		{
			try
			{
				// If destPath is intended as directory entry file:
				var dir = Path.GetDirectoryName(destPath);
				if (!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);

				// Skip if already extracted and non-empty
				if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
					return;

				using var input = assets.Open(assetPath);
				using var output = File.Create(destPath);
				input.CopyTo(output);
				AndroidFileLog.Info("OpenRA.Content", "Extracted " + assetPath + " -> " + destPath);
			}
			catch (Exception e)
			{
				// Not a file or missing — expected for directory placeholders
				AndroidFileLog.Warn("OpenRA.Content", "Skip asset " + assetPath + ": " + e.Message);
			}
		}

		public static bool HasAnyMod()
		{
			if (!Directory.Exists(ModsDir))
				return false;
			foreach (var d in Directory.GetDirectories(ModsDir))
			{
				if (File.Exists(Path.Combine(d, "mod.yaml")))
					return true;
			}
			return false;
		}
	}
}
