// Single SupportDir for the process lifetime. Prefer public OpenRA when all-files is granted.

using System;
using Android.App;
using Android.Content;
using Android.OS;
using AEnv = global::Android.OS.Environment;
using AUri = Android.Net.Uri;

namespace OpenRA.Android
{
	public static class StorageAccess
	{
		const string ActionManageAppAllFilesAccessPermission =
			"android.settings.MANAGE_APP_ALL_FILES_ACCESS_PERMISSION";
		const string ActionManageAllFilesAccessPermission =
			"android.settings.MANAGE_ALL_FILES_ACCESS_PERMISSION";
		const string PrefKey = "openra_support_dir";

		public const string PublicRoot = "/storage/emulated/0/OpenRA";

		static string lockedSupportDir;

		public static bool HasAllFilesAccess()
		{
			if ((int)Build.VERSION.SdkInt < 30)
				return true;
			try { return AEnv.IsExternalStorageManager; }
			catch { return false; }
		}

		public static void RequestAllFilesAccess(Activity activity)
		{
			if ((int)Build.VERSION.SdkInt < 30 || HasAllFilesAccess())
				return;
			try
			{
				var intent = new Intent(ActionManageAppAllFilesAccessPermission);
				intent.SetData(AUri.Parse("package:" + activity.PackageName));
				activity.StartActivity(intent);
			}
			catch
			{
				try { activity.StartActivity(new Intent(ActionManageAllFilesAccessPermission)); }
				catch (Exception e)
				{
					AndroidFileLog.Warn("OpenRA.Storage", "Cannot open all-files settings: " + e.Message);
				}
			}
		}

		/// <summary>
		/// Resolve once and lock for this process. Changing mid-run caused dual paths + full re-extract.
		/// </summary>
		public static string ResolveSupportDir()
		{
			if (!string.IsNullOrEmpty(lockedSupportDir))
				return lockedSupportDir;

			// Prefer previously persisted path if still writable
			try
			{
				var prefs = Application.Context.GetSharedPreferences("openra", FileCreationMode.Private);
				var saved = prefs.GetString(PrefKey, null);
				if (!string.IsNullOrEmpty(saved) && IsWritable(saved))
				{
					lockedSupportDir = saved;
					AndroidFileLog.Info("OpenRA.Storage", "Using saved SupportDir=" + lockedSupportDir);
					return lockedSupportDir;
				}
			}
			catch { /* ignore */ }

			string chosen = null;

			// Public path only if all-files (or pre-30)
			if (HasAllFilesAccess() || (int)Build.VERSION.SdkInt < 30)
			{
				if (IsWritable(PublicRoot))
					chosen = PublicRoot;
			}

			if (chosen == null)
			{
				try
				{
					var ext = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
					if (!string.IsNullOrEmpty(ext))
						chosen = System.IO.Path.Combine(ext, "OpenRA");
				}
				catch { /* ignore */ }
			}

			chosen ??= System.IO.Path.Combine(Application.Context.FilesDir.AbsolutePath, "OpenRA");

			try { System.IO.Directory.CreateDirectory(chosen); }
			catch { /* ignore */ }

			lockedSupportDir = chosen;
			try
			{
				var prefs = Application.Context.GetSharedPreferences("openra", FileCreationMode.Private);
				prefs.Edit().PutString(PrefKey, lockedSupportDir).Apply();
			}
			catch { /* ignore */ }

			AndroidFileLog.Info("OpenRA.Storage", "Locked SupportDir=" + lockedSupportDir);
			return lockedSupportDir;
		}

		/// <summary>
		/// Call after user grants all-files: only takes effect on next cold start
		/// unless current dir is still the default app path and public is empty — then migrate once.
		/// </summary>
		public static void PreferPublicOnNextLaunch()
		{
			try
			{
				var prefs = Application.Context.GetSharedPreferences("openra", FileCreationMode.Private);
				prefs.Edit().PutString(PrefKey, PublicRoot).Apply();
			}
			catch { /* ignore */ }
		}

		static bool IsWritable(string path)
		{
			try
			{
				System.IO.Directory.CreateDirectory(path);
				var probe = System.IO.Path.Combine(path, ".write_test");
				System.IO.File.WriteAllText(probe, "ok");
				System.IO.File.Delete(probe);
				return true;
			}
			catch
			{
				return false;
			}
		}
	}
}
