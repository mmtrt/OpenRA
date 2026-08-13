// SupportDir = Android/data/<package>/  (external) so Logs/Content live next to files/, not under files/OpenRA/.
// Example: /storage/emulated/0/Android/data/net.openra.android.ts/Logs

using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.OS;

namespace OpenRA.Android
{
	public static class StorageAccess
	{
		const string PrefsName = "openra";
		const string PrefSupportDir = "openra_support_dir";

		static string lockedSupportDir;

		/// <summary>Always false for now — public path disabled.</summary>
		public static bool HasAllFilesAccess() => false;

		public static void RequestAllFilesAccess(Activity activity)
		{
			AndroidFileLog.Info("OpenRA.Storage", "All-files / public path disabled until game is bootable");
		}

		public static bool ShouldPromptAllFiles() => false;

		public static void MarkAskedAllFiles() { }

		public static void PreferPublicOnNextLaunch() { }

		public static string TryPromoteToPublic() => ResolveSupportDir();

		/// <summary>
		/// Package external data root: …/Android/data/&lt;package&gt;/
		/// (parent of GetExternalFilesDir), so Logs/Content/mods sit at package scope
		/// for every mod ApplicationId. Fallback: parent of internal FilesDir.
		/// </summary>
		public static string ResolveSupportDir(bool allowRelock = false)
		{
			if (!string.IsNullOrEmpty(lockedSupportDir) && !allowRelock)
			{
				// Migrate away from legacy …/files/OpenRA if we previously locked that path.
				if (IsLegacyFilesOpenRA(lockedSupportDir))
					allowRelock = true;
				else
					return lockedSupportDir;
			}

			string chosen = null;
			string legacy = null;

			try
			{
				var filesDir = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
				if (!string.IsNullOrEmpty(filesDir))
				{
					legacy = Path.Combine(filesDir, "OpenRA");
					var parent = Directory.GetParent(filesDir)?.FullName;
					if (!string.IsNullOrEmpty(parent))
						chosen = parent;
				}
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Storage", "GetExternalFilesDir: " + e.Message);
			}

			if (string.IsNullOrEmpty(chosen))
			{
				try
				{
					var internalFiles = Application.Context.FilesDir?.AbsolutePath;
					if (!string.IsNullOrEmpty(internalFiles))
					{
						legacy ??= Path.Combine(internalFiles, "OpenRA");
						var parent = Directory.GetParent(internalFiles)?.FullName;
						if (!string.IsNullOrEmpty(parent))
							chosen = parent;
					}
				}
				catch { /* ignore */ }
			}

			chosen ??= Path.Combine(Application.Context.FilesDir.AbsolutePath, "OpenRA");

			try { Directory.CreateDirectory(chosen); }
			catch { /* ignore */ }

			// One-shot move from legacy files/OpenRA → package root when target looks empty of content.
			if (!string.IsNullOrEmpty(legacy) && !string.Equals(legacy, chosen, StringComparison.Ordinal))
				TryMigrateLegacy(legacy, chosen);

			lockedSupportDir = chosen;

			try
			{
				Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private)
					.Edit().PutString(PrefSupportDir, chosen).Apply();
			}
			catch { /* ignore */ }

			AndroidFileLog.Info("OpenRA.Storage", "Locked SupportDir (package root)=" + lockedSupportDir);
			return lockedSupportDir;
		}

		static bool IsLegacyFilesOpenRA(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;
			// …/files/OpenRA or …/files/OpenRA/
			var n = path.Replace('\\', '/').TrimEnd('/');
			return n.EndsWith("/files/OpenRA", StringComparison.OrdinalIgnoreCase);
		}

		static void TryMigrateLegacy(string legacy, string dest)
		{
			try
			{
				if (!Directory.Exists(legacy))
					return;

				// If dest already has Content or mods, assume user is on new layout.
				if (Directory.Exists(Path.Combine(dest, "Content"))
				    || Directory.Exists(Path.Combine(dest, "mods"))
				    || Directory.Exists(Path.Combine(dest, "Logs")))
				{
					AndroidFileLog.Info("OpenRA.Storage", "New SupportDir already populated — skip migrate from " + legacy);
					return;
				}

				AndroidFileLog.Info("OpenRA.Storage", "Migrating " + legacy + " → " + dest);
				foreach (var entry in Directory.EnumerateFileSystemEntries(legacy))
				{
					var name = Path.GetFileName(entry);
					if (string.IsNullOrEmpty(name))
						continue;
					var target = Path.Combine(dest, name);
					try
					{
						if (Directory.Exists(entry))
						{
							if (!Directory.Exists(target))
								Directory.Move(entry, target);
						}
						else if (File.Exists(entry))
						{
							if (!File.Exists(target))
								File.Move(entry, target);
						}
					}
					catch (Exception e)
					{
						AndroidFileLog.Warn("OpenRA.Storage", "Migrate " + name + ": " + e.Message);
					}
				}
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Storage", "TryMigrateLegacy: " + e.Message);
			}
		}
	}
}
