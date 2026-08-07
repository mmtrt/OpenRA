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
			try
			{
				EnsureLayoutCore(preferredSupportDir);
			}
			catch (Exception e)
			{
				AndroidFileLog.Exception("OpenRA.Content.EnsureLayout", e);
			}
		}

		static void EnsureLayoutCore(string preferredSupportDir)
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

			PlaceAssembliesForLoader();
			LogTree();
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
			// Stage from Assets/assemblies → SupportDir root (where ObjectCreator loads),
			// then remove the assemblies/ staging folder so we do not keep a dual tree.
			var stage = AssembliesDir;
			if (!Directory.Exists(stage))
			{
				// Also accept DLLs already extracted under SupportDir
				stage = SupportDir;
			}

			string[] sources = Directory.Exists(AssembliesDir)
				? Directory.GetFiles(AssembliesDir, "*.dll")
				: Array.Empty<string>();

			// Targets the engine actually probes (cwd / SupportDir / FilesDir)
			var targets = new System.Collections.Generic.List<string> { SupportDir };
			try
			{
				var files = Application.Context.FilesDir?.AbsolutePath;
				if (!string.IsNullOrEmpty(files) && files != SupportDir)
					targets.Add(files);
			}
			catch { /* ignore */ }

			foreach (var dll in sources)
			{
				var name = Path.GetFileName(dll);
				foreach (var dir in targets)
				{
					try
					{
						Directory.CreateDirectory(dir);
						var dest = Path.Combine(dir, name);
						if (!SameFile(dll, dest))
						{
							File.Copy(dll, dest, overwrite: true);
							AndroidFileLog.Info("OpenRA.Content", "Assembly → " + dest);
						}
					}
					catch { /* ignore */ }
				}
			}

			// Drop staging directory — game does not load from assemblies/
			try
			{
				if (Directory.Exists(AssembliesDir))
				{
					Directory.Delete(AssembliesDir, recursive: true);
					AndroidFileLog.Info("OpenRA.Content", "Cleared staging assemblies/");
				}
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Content", "Could not clear assemblies/: " + e.Message);
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
