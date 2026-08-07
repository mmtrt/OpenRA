// Single SupportDir preference. Public path when all-files granted.

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
		const string PrefsName = "openra";
		const string PrefSupportDir = "openra_support_dir";
		const string PrefAskedAllFiles = "openra_asked_all_files";

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
				MarkAskedAllFiles();
				var intent = new Intent(ActionManageAppAllFilesAccessPermission);
				intent.SetData(AUri.Parse("package:" + activity.PackageName));
				activity.StartActivity(intent);
				AndroidFileLog.Info("OpenRA.Storage", "Opened MANAGE_APP_ALL_FILES_ACCESS settings");
			}
			catch (Exception e)
			{
				try
				{
					activity.StartActivity(new Intent(ActionManageAllFilesAccessPermission));
				}
				catch (Exception e2)
				{
					AndroidFileLog.Warn("OpenRA.Storage",
						"Cannot open all-files settings: " + e.Message + " / " + e2.Message);
				}
			}
		}

		public static bool ShouldPromptAllFiles()
		{
			if ((int)Build.VERSION.SdkInt < 30)
				return false;
			if (HasAllFilesAccess())
				return false;
			try
			{
				var prefs = Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
				return !prefs.GetBoolean(PrefAskedAllFiles, false);
			}
			catch { return true; }
		}

		public static void MarkAskedAllFiles()
		{
			try
			{
				Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private)
					.Edit().PutBoolean(PrefAskedAllFiles, true).Apply();
			}
			catch { /* ignore */ }
		}

		/// <summary>
		/// Call on Resume after settings: if all-files now granted and we preferred public, unlock and use public.
		/// </summary>
		public static string ResolveSupportDir(bool allowRelock = false)
		{
			if (!string.IsNullOrEmpty(lockedSupportDir) && !allowRelock)
				return lockedSupportDir;

			if (HasAllFilesAccess() && IsWritable(PublicRoot))
			{
				Lock(PublicRoot);
				return lockedSupportDir;
			}

			try
			{
				var prefs = Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
				var saved = prefs.GetString(PrefSupportDir, null);
				if (!string.IsNullOrEmpty(saved) && IsWritable(saved))
				{
					// Prefer public if all-files and saved was app path
					if (HasAllFilesAccess() && IsWritable(PublicRoot) && saved != PublicRoot)
					{
						Lock(PublicRoot);
						return lockedSupportDir;
					}
					Lock(saved);
					return lockedSupportDir;
				}
			}
			catch { /* ignore */ }

			string chosen = null;
			if (HasAllFilesAccess() && IsWritable(PublicRoot))
				chosen = PublicRoot;

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
			try { System.IO.Directory.CreateDirectory(chosen); } catch { /* ignore */ }
			Lock(chosen);
			return lockedSupportDir;
		}

		public static void PreferPublicOnNextLaunch()
		{
			try
			{
				Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private)
					.Edit().PutString(PrefSupportDir, PublicRoot).Apply();
			}
			catch { /* ignore */ }
		}

		/// <summary>After returning from settings with all-files granted, switch to public this session.</summary>
		public static string TryPromoteToPublic()
		{
			if (!HasAllFilesAccess() || !IsWritable(PublicRoot))
				return lockedSupportDir ?? ResolveSupportDir();

			var old = lockedSupportDir;
			Lock(PublicRoot);
			AndroidFileLog.Info("OpenRA.Storage", "Promoted SupportDir " + old + " → " + lockedSupportDir);
			return lockedSupportDir;
		}

		static void Lock(string path)
		{
			lockedSupportDir = path;
			try
			{
				Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private)
					.Edit().PutString(PrefSupportDir, path).Apply();
			}
			catch { /* ignore */ }
			AndroidFileLog.Info("OpenRA.Storage", "Locked SupportDir=" + path);
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
			catch { return false; }
		}
	}
}
