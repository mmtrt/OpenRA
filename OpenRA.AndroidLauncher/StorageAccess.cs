// App-private SupportDir only (until the game is fully bootable).
// No public /storage/emulated/0/OpenRA dual-path.

using System;
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
		/// Prefer GetExternalFilesDir (app-specific external, no permission).
		/// Fall back to internal FilesDir. Never use /storage/emulated/0/OpenRA.
		/// </summary>
		public static string ResolveSupportDir(bool allowRelock = false)
		{
			if (!string.IsNullOrEmpty(lockedSupportDir) && !allowRelock)
				return lockedSupportDir;

			string chosen = null;
			try
			{
				var ext = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
				if (!string.IsNullOrEmpty(ext))
					chosen = System.IO.Path.Combine(ext, "OpenRA");
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Storage", "GetExternalFilesDir: " + e.Message);
			}

			chosen ??= System.IO.Path.Combine(Application.Context.FilesDir.AbsolutePath, "OpenRA");

			try { System.IO.Directory.CreateDirectory(chosen); }
			catch { /* ignore */ }

			lockedSupportDir = chosen;

			try
			{
				Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private)
					.Edit().PutString(PrefSupportDir, chosen).Apply();
			}
			catch { /* ignore */ }

			AndroidFileLog.Info("OpenRA.Storage", "Locked SupportDir (app-private)=" + lockedSupportDir);
			return lockedSupportDir;
		}
	}
}
