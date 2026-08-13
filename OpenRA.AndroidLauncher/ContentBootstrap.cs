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

		/// <summary>
		/// True only after SupportDir is set, APK engine assets are extracted (or skipped as present),
		/// and mod assemblies are staged for ObjectCreator. Engine must not start before this.
		/// Manual game content (MIX files) can exist earlier — that alone is not enough.
		/// </summary>
		public static bool IsReady { get; private set; }

		static bool extractedThisProcess;

		public static void EnsureLayout(string preferredSupportDir = null)
		{
			IsReady = false;
			try
			{
				EnsureLayoutCore(preferredSupportDir);
				IsReady = SupportDir != null && HasAnyMod() && HasStagedModAssembly();
				AndroidFileLog.Info("OpenRA.Content",
					"EnsureLayout done ready=" + IsReady
					+ " mods=" + HasAnyMod()
					+ " assemblies=" + HasStagedModAssembly());
			}
			catch (Exception e)
			{
				IsReady = false;
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
			var needExtract = !File.Exists(marker) || !HasAnyMod() || !HasStagedModAssembly();

			// Engine assets (mods/chrome/fluent, glsl, assemblies) must refresh from APK when
			// incomplete OR when chrome/fluent lack Touch keys (old extract left stale files —
			// TryCopyFile used to skip non-empty destinations, so CNC showed raw fluent keys).
			var staleChrome = EngineAssetsLookStale();
			if ((needExtract || staleChrome) && !extractedThisProcess)
			{
				extractedThisProcess = true;
				AndroidFileLog.Info("OpenRA.Content",
					"Extracting APK engine assets (needExtract=" + needExtract + " stale=" + staleChrome + ")…");
				// Overwrite engine files; never treat user Content/ the same way.
				ExtractAssetsFolder("assemblies", AssembliesDir, overwrite: true);
				ExtractAssetsFolder("mods", ModsDir, overwrite: true);
				ExtractAssetsFolder("glsl", GlslDir, overwrite: true);
				ExtractAssetsFolder("Content", ContentDir, overwrite: false);
				TryExtractRootFile("global mix database.dat", Path.Combine(SupportDir, "global mix database.dat"));
				PlaceAssembliesForLoader();

				if (HasAnyMod() && HasStagedModAssembly())
				{
					try { File.WriteAllText(marker, DateTime.UtcNow.ToString("o")); }
					catch { /* ignore */ }
					AndroidFileLog.Info("OpenRA.Content", "Asset extract complete — marker written");
				}
				else
				{
					AndroidFileLog.Warn("OpenRA.Content",
						"Asset extract incomplete (mods=" + HasAnyMod()
						+ " assemblies=" + HasStagedModAssembly()
						+ ") — marker NOT written; will retry next launch");
				}
			}
			else
			{
				AndroidFileLog.Info("OpenRA.Content", "Assets already present — skip full extract");
				PlaceAssembliesForLoader();
			}

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
			// Stage from Assets/assemblies → SupportDir + BaseDirectory (ObjectCreator / BinDir).
			var stage = AssembliesDir;
			if (!Directory.Exists(stage))
				stage = SupportDir;

			string[] sources = Directory.Exists(AssembliesDir)
				? Directory.GetFiles(AssembliesDir, "*.dll")
				: Array.Empty<string>();

			// If staging was already cleared on a prior run, still ensure SupportDir DLLs exist in BinDir.
			if (sources.Length == 0 && Directory.Exists(SupportDir))
			{
				sources = Directory.GetFiles(SupportDir, "OpenRA.Mods.*.dll");
			}

			var targets = new System.Collections.Generic.List<string> { SupportDir };
			try
			{
				var bin = AppDomain.CurrentDomain.BaseDirectory;
				if (!string.IsNullOrEmpty(bin))
				{
					var trimmed = bin.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					if (!string.Equals(trimmed, SupportDir, StringComparison.Ordinal))
						targets.Add(trimmed);
				}
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

		public static void ExtractAssetsFolder(string assetFolder, string destDir, bool overwrite = false)
		{
			var assets = Application.Context.Assets;
			if (assets == null) return;
			ExtractRecursive(assets, assetFolder, destDir, overwrite);
		}

		static void ExtractRecursive(AssetManager assets, string assetPath, string destDir, bool overwrite)
		{
			string[] list;
			try { list = assets.List(assetPath); }
			catch { return; }

			if (list == null || list.Length == 0)
			{
				TryCopyFile(assets, assetPath, destDir, overwrite);
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
					ExtractRecursive(assets, childAsset, childDest, overwrite);
				else
					TryCopyFile(assets, childAsset, childDest, overwrite);
			}
		}

		static void TryCopyFile(AssetManager assets, string assetPath, string destPath, bool overwrite = false)
		{
			try
			{
				var dir = Path.GetDirectoryName(destPath);
				if (!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);
				// User Content/: skip existing. Engine mods/glsl/assemblies: overwrite from APK.
				if (!overwrite && File.Exists(destPath) && new FileInfo(destPath).Length > 0)
					return;
				using var input = assets.Open(assetPath);
				using var output = File.Create(destPath);
				input.CopyTo(output);
			}
			catch { /* missing leaf */ }
		}

		/// <summary>
		/// True when a previous extract left chrome/fluent without Touch labels (raw keys on screen).
		/// </summary>
		static bool EngineAssetsLookStale()
		{
			try
			{
				if (string.IsNullOrEmpty(SupportDir) || !Directory.Exists(ModsDir))
					return true;
				// Any packaged settings-input that still lacks Touch after we ship it → refresh.
				foreach (var f in Directory.EnumerateFiles(ModsDir, "settings-input.yaml", SearchOption.AllDirectories))
				{
					var text = File.ReadAllText(f);
					if (text.Contains("MOUSE_CONTROL_DESC_MODERN", StringComparison.Ordinal)
					    && !text.Contains("MOUSE_CONTROL_DESC_TOUCH", StringComparison.Ordinal))
						return true;
				}
				// CNC loads cnc|fluent/chrome.ftl — keys must resolve there (or common chrome.ftl).
				foreach (var f in Directory.EnumerateFiles(ModsDir, "chrome.ftl", SearchOption.AllDirectories))
				{
					var text = File.ReadAllText(f);
					// Only flag mod chrome.ftl that has other scheme labels but not touch
					if (text.Contains("label-mouse-control-desc-modern-selection", StringComparison.Ordinal)
					    && !text.Contains("label-mouse-control-desc-touch-selection", StringComparison.Ordinal))
						return true;
				}
			}
			catch { /* ignore */ }
			return false;
		}

		public static bool HasAnyMod()
		{
			if (string.IsNullOrEmpty(SupportDir) || !Directory.Exists(ModsDir))
				return false;
			foreach (var d in Directory.GetDirectories(ModsDir))
				if (File.Exists(Path.Combine(d, "mod.yaml")))
					return true;
			return false;
		}

		/// <summary>Mods.Common (or any Mods.*) available for load from SupportDir or BaseDirectory.</summary>
		public static bool HasStagedModAssembly()
		{
			try
			{
				foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
				{
					try
					{
						var n = a.GetName().Name;
						if (n == "OpenRA.Mods.Common" || (n != null && n.StartsWith("OpenRA.Mods.", StringComparison.Ordinal)))
							return true;
					}
					catch { /* ignore */ }
				}
				foreach (var dir in new[] { SupportDir, AppDomain.CurrentDomain.BaseDirectory, AssembliesDir })
				{
					if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
						continue;
					if (File.Exists(Path.Combine(dir, "OpenRA.Mods.Common.dll")))
						return true;
					foreach (var f in Directory.EnumerateFiles(dir, "OpenRA.Mods.*.dll"))
						if (new FileInfo(f).Length > 0)
							return true;
				}
			}
			catch { /* ignore */ }
			return false;
		}
	}
}
