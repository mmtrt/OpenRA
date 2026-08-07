// Extract APK assets → SupportDir; place mod DLLs where ObjectCreator can open them.

using System;
using System.IO;
using Android.App;
using Android.Content.Res;

namespace OpenRA.Android
{
	public static class ContentBootstrap
	{
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

			ExtractAssetsFolder("assemblies", AssembliesDir);
			ExtractAssetsFolder("mods", ModsDir);
			ExtractAssetsFolder("glsl", GlslDir);
			ExtractAssetsFolder("Content", ContentDir);

			PlaceAssembliesForLoader();
			LogTree();
		}

		static string ResolveSupportDir()
		{
			return StorageAccess.ResolveSupportDir();
		}

		static bool TryCreate(string path)
		{
			try
			{
				Directory.CreateDirectory(path);
				return true;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// ObjectCreator uses File.OpenRead on bare assembly names; resolution ends up under
		/// internal FilesDir (see device log: /data/user/0/.../files/OpenRA.Mods.Common.dll).
		/// Also place copies under SupportDir root and each mod folder.
		/// </summary>
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
					if (string.IsNullOrEmpty(dir))
						continue;
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
					catch (Exception e)
					{
						AndroidFileLog.Warn("OpenRA.Content", "Assembly copy " + name + " → " + dir + ": " + e.Message);
					}
				}

				foreach (var modId in new[] { "common", "ra", "cnc", "d2k" })
				{
					var modDir = Path.Combine(ModsDir, modId);
					if (!Directory.Exists(modDir))
						continue;
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
				if (!File.Exists(b))
					return false;
				return new FileInfo(a).Length == new FileInfo(b).Length;
			}
			catch
			{
				return false;
			}
		}

		static void LogTree()
		{
			foreach (var d in Directory.Exists(ModsDir) ? Directory.GetDirectories(ModsDir) : Array.Empty<string>())
			{
				if (File.Exists(Path.Combine(d, "mod.yaml")))
					AndroidFileLog.Info("OpenRA.Content", "mod ok: " + Path.GetFileName(d));
			}

			if (Directory.Exists(AssembliesDir))
				foreach (var f in Directory.GetFiles(AssembliesDir, "*.dll"))
					AndroidFileLog.Info("OpenRA.Content", "assembly packaged: " + Path.GetFileName(f));
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
			}
			catch
			{
				// missing asset leaf — expected
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
