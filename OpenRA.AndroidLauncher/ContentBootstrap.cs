// Extract APK assets → /storage/emulated/0/OpenRA/ (mods, glsl, Content, assemblies).

using System;
using System.IO;
using Android.App;
using Android.Content.Res;

namespace OpenRA.Android
{
	public static class ContentBootstrap
	{
		/// <summary>Public tree that survives app uninstall (user requested).</summary>
		public static string PublicRoot => "/storage/emulated/0/OpenRA";

		public static string SupportDir { get; private set; }
		public static string ModsDir => Path.Combine(SupportDir, "mods");
		public static string ContentDir => Path.Combine(SupportDir, "Content");
		public static string GlslDir => Path.Combine(SupportDir, "glsl");
		public static string AssembliesDir => Path.Combine(SupportDir, "assemblies");

		public static void EnsureLayout(string preferredSupportDir = null)
		{
			SupportDir = preferredSupportDir ?? ResolveSupportDir();
			Directory.CreateDirectory(SupportDir);
			Directory.CreateDirectory(ModsDir);
			Directory.CreateDirectory(ContentDir);
			Directory.CreateDirectory(GlslDir);
			Directory.CreateDirectory(AssembliesDir);
			Directory.CreateDirectory(Path.Combine(SupportDir, "maps"));
			Directory.CreateDirectory(Path.Combine(SupportDir, "Replays"));
			Directory.CreateDirectory(Path.Combine(SupportDir, "Screenshots"));
			Directory.CreateDirectory(Path.Combine(SupportDir, "Logs"));

			AndroidFileLog.Info("OpenRA.Content", "SupportDir=" + SupportDir);

			// Order: assemblies first (mod loader), then mods YAML/bits, glsl, content
			ExtractAssetsFolder("assemblies", AssembliesDir);
			ExtractAssetsFolder("mods", ModsDir);
			ExtractAssetsFolder("glsl", GlslDir);
			ExtractAssetsFolder("Content", ContentDir);

			// Place mod DLLs where FileSystem/mod loader expects them (mod folder and/or assemblies)
			MirrorAssembliesIntoMods();

			LogTree();
		}

		static string ResolveSupportDir()
		{
			// Prefer public /storage/emulated/0/OpenRA as requested
			try
			{
				Directory.CreateDirectory(PublicRoot);
				var probe = Path.Combine(PublicRoot, ".write_test");
				File.WriteAllText(probe, "ok");
				File.Delete(probe);
				return PublicRoot;
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Content", "Public OpenRA path not writable: " + e.Message);
			}

			// Fallback: app external (always writable, cleared on uninstall)
			try
			{
				var ext = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
				if (!string.IsNullOrEmpty(ext))
					return Path.Combine(ext, "OpenRA");
			}
			catch { /* ignore */ }

			return Path.Combine(Application.Context.FilesDir.AbsolutePath, "OpenRA");
		}

		static void MirrorAssembliesIntoMods()
		{
			if (!Directory.Exists(AssembliesDir))
				return;

			foreach (var dll in Directory.GetFiles(AssembliesDir, "*.dll"))
			{
				var name = Path.GetFileName(dll);
				// OpenRA often resolves assemblies next to mod packages
				foreach (var modId in new[] { "common", "ra", "cnc", "d2k" })
				{
					var modDir = Path.Combine(ModsDir, modId);
					if (!Directory.Exists(modDir))
						continue;
					var dest = Path.Combine(modDir, name);
					try
					{
						if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(dll).Length)
						{
							File.Copy(dll, dest, overwrite: true);
							AndroidFileLog.Info("OpenRA.Content", "Mirrored " + name + " -> mods/" + modId);
						}
					}
					catch (Exception e)
					{
						AndroidFileLog.Warn("OpenRA.Content", "Mirror " + name + ": " + e.Message);
					}
				}
			}
		}

		static void LogTree()
		{
			foreach (var d in Directory.Exists(ModsDir) ? Directory.GetDirectories(ModsDir) : Array.Empty<string>())
			{
				AndroidFileLog.Info("OpenRA.Content", "mod dir: " + d);
				var yaml = Path.Combine(d, "mod.yaml");
				if (File.Exists(yaml))
					AndroidFileLog.Info("OpenRA.Content", "  has mod.yaml");
			}

			if (Directory.Exists(AssembliesDir))
				foreach (var f in Directory.GetFiles(AssembliesDir, "*.dll"))
					AndroidFileLog.Info("OpenRA.Content", "assembly: " + Path.GetFileName(f));

			if (Directory.Exists(GlslDir))
			{
				var n = Directory.GetFiles(GlslDir, "*", SearchOption.AllDirectories).Length;
				AndroidFileLog.Info("OpenRA.Content", "glsl files: " + n);
			}
		}

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
				AndroidFileLog.Info("OpenRA.Content", "Extracted " + assetPath);
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Content", "Skip " + assetPath + ": " + e.Message);
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
