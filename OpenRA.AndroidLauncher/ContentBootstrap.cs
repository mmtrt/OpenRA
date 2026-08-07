// Extract APK assets once into the locked SupportDir.

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
		public static string GlslDir => Path.Combine(SupportDir, "glsl");
		public static string AssembliesDir => Path.Combine(SupportDir, "assemblies");

		static bool extractedThisProcess;

		public static void EnsureLayout(string preferredSupportDir = null)
		{
			SupportDir = preferredSupportDir ?? StorageAccess.ResolveSupportDir();
			Directory.CreateDirectory(SupportDir);
			Directory.CreateDirectory(ModsDir);
			Directory.CreateDirectory(ContentDir);
			Directory.CreateDirectory(GlslDir);
			Directory.CreateDirectory(AssembliesDir);
			Directory.CreateDirectory(Path.Combine(SupportDir, "maps"));
			Directory.CreateDirectory(Path.Combine(SupportDir, "Replays"));
			Directory.CreateDirectory(Path.Combine(SupportDir, "Screenshots"));
			Directory.CreateDirectory(Path.Combine(SupportDir, "Logs"));
			Directory.CreateDirectory(Path.Combine(SupportDir, "Cache"));

			AndroidFileLog.Info("OpenRA.Content", "SupportDir=" + SupportDir);

			var marker = Path.Combine(SupportDir, ".assets_extracted");
			var needExtract = !File.Exists(marker) || !HasAnyMod();

			if (needExtract && !extractedThisProcess)
			{
				extractedThisProcess = true;
				AndroidFileLog.Info("OpenRA.Content", "Extracting APK assets (first time for this SupportDir)…");
				ExtractAssetsFolder("assemblies", AssembliesDir);
				ExtractAssetsFolder("mods", ModsDir);
				ExtractAssetsFolder("glsl", GlslDir);
				ExtractAssetsFolder("Content", ContentDir);
				// MIX filename hash database (silences debug.log unknown-hash warnings)
				TryExtractRootFile("global mix database.dat", Path.Combine(SupportDir, "global mix database.dat"));
				try { File.WriteAllText(marker, DateTime.UtcNow.ToString("o")); }
				catch { /* ignore */ }
			}
			else
			{
				AndroidFileLog.Info("OpenRA.Content", "Assets already present — skip full extract");
			}

			MigrateContentFromAppExternalIfNeeded();
			PlaceAssembliesForLoader();
			LogTree();
		}


		/// <summary>
		/// If SupportDir is public OpenRA but MIX files only exist under app-external, copy once.
		/// </summary>
		static void MigrateContentFromAppExternalIfNeeded()
		{
			try
			{
				if (ContentProbe.IsBaseContentInstalled(SupportDir))
					return;

				var ext = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
				if (string.IsNullOrEmpty(ext))
					return;
				var oldRoot = Path.Combine(ext, "OpenRA");
				if (oldRoot == SupportDir)
					return;
				if (!ContentProbe.IsBaseContentInstalled(oldRoot))
					return;

				AndroidFileLog.Info("OpenRA.Content", "Migrating Content from " + oldRoot + " → " + SupportDir);
				var src = Path.Combine(oldRoot, "Content");
				var dst = Path.Combine(SupportDir, "Content");
				if (Directory.Exists(src))
					CopyDir(src, dst);
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Content", "Migrate: " + e.Message);
			}
		}

		static void CopyDir(string src, string dst)
		{
			Directory.CreateDirectory(dst);
			foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
			{
				var rel = Path.GetRelativePath(src, dir);
				Directory.CreateDirectory(Path.Combine(dst, rel));
			}
			foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
			{
				var rel = Path.GetRelativePath(src, file);
				var dest = Path.Combine(dst, rel);
				Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
				if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
					File.Copy(file, dest, overwrite: true);
			}
		}


		static void TryExtractRootFile(string assetName, string destPath)
		{
			try
			{
				if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
					return;
				var assets = Application.Context.Assets;
				if (assets == null) return;
				using var input = assets.Open(assetName);
				using var output = File.Create(destPath);
				input.CopyTo(output);
				AndroidFileLog.Info("OpenRA.Content", "Extracted " + assetName);
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Content", assetName + ": " + e.Message);
			}
		}

		public static void PlaceAssembliesForLoader()
		{
			if (!Directory.Exists(AssembliesDir))
				return;

			string[] targets =
			{
				SupportDir,
				Application.Context.FilesDir?.AbsolutePath,
				Path.Combine(Application.Context.FilesDir?.AbsolutePath ?? "", "OpenRA"),
			};

			foreach (var dll in Directory.GetFiles(AssembliesDir, "*.dll"))
			{
				var name = Path.GetFileName(dll);
				foreach (var dir in targets)
				{
					if (string.IsNullOrEmpty(dir)) continue;
					try
					{
						Directory.CreateDirectory(dir);
						var dest = Path.Combine(dir, name);
						if (!SameFile(dll, dest))
							File.Copy(dll, dest, overwrite: true);
					}
					catch { /* ignore */ }
				}

				foreach (var modId in new[] { "common", "ra", "cnc", "d2k" })
				{
					var modDir = Path.Combine(ModsDir, modId);
					if (!Directory.Exists(modDir)) continue;
					try
					{
						var dest = Path.Combine(modDir, name);
						if (!SameFile(dll, dest))
							File.Copy(dll, dest, overwrite: true);
					}
					catch { /* ignore */ }
				}
			}
		}

		static bool SameFile(string a, string b)
		{
			try
			{
				if (!File.Exists(b)) return false;
				return new FileInfo(a).Length == new FileInfo(b).Length;
			}
			catch { return false; }
		}

		static void LogTree()
		{
			if (!Directory.Exists(ModsDir)) return;
			foreach (var d in Directory.GetDirectories(ModsDir))
			{
				if (File.Exists(Path.Combine(d, "mod.yaml")))
					AndroidFileLog.Info("OpenRA.Content", "mod ok: " + Path.GetFileName(d));
			}
		}

		public static void ExtractAssetsFolder(string assetFolder, string destDir)
		{
			var assets = Application.Context.Assets;
			if (assets == null) return;
			ExtractRecursive(assets, assetFolder, destDir);
		}

		static void ExtractRecursive(AssetManager assets, string assetPath, string destDir)
		{
			string[] list;
			try { list = assets.List(assetPath); }
			catch { return; }

			if (list == null || list.Length == 0)
			{
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
				var dir = Path.GetDirectoryName(destPath);
				if (!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);
				if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
					return;
				using var input = assets.Open(assetPath);
				using var output = File.Create(destPath);
				input.CopyTo(output);
			}
			catch { /* missing leaf */ }
		}

		public static bool HasAnyMod()
		{
			if (!Directory.Exists(ModsDir)) return false;
			foreach (var d in Directory.GetDirectories(ModsDir))
				if (File.Exists(Path.Combine(d, "mod.yaml")))
					return true;
			return false;
		}
	}
}
