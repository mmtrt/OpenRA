// Android 11–16 storage: all-files access for /storage/emulated/0/OpenRA when possible.

using System;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using AEnv = global::Android.OS.Environment;
using AUri = Android.Net.Uri;

namespace OpenRA.Android
{
	public static class StorageAccess
	{
		public static bool HasAllFilesAccess()
		{
			if ((int)Build.VERSION.SdkInt < 30)
				return true;
			try
			{
				return AEnv.IsExternalStorageManager;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Opens system Settings page so the user can grant "All files access" (Android 11+ / 16).
		/// Required to write /storage/emulated/0/OpenRA outside the app sandbox.
		/// </summary>
		public static void RequestAllFilesAccess(Activity activity)
		{
			if ((int)Build.VERSION.SdkInt < 30)
				return;
			if (HasAllFilesAccess())
				return;

			try
			{
				var uri = AUri.Parse("package:" + activity.PackageName);
				var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission, uri);
				activity.StartActivity(intent);
				AndroidFileLog.Info("OpenRA.Storage", "Opened MANAGE_APP_ALL_FILES_ACCESS settings");
			}
			catch (Exception e)
			{
				try
				{
					var intent = new Intent(Settings.ActionManageAllFilesAccessPermission);
					activity.StartActivity(intent);
				}
				catch (Exception e2)
				{
					AndroidFileLog.Warn("OpenRA.Storage", "Cannot open all-files settings: " + e.Message + " / " + e2.Message);
				}
			}
		}

		public static string ResolveSupportDir()
		{
			// 1) Public OpenRA if all-files (or legacy) access works
			const string publicRoot = "/storage/emulated/0/OpenRA";
			if (HasAllFilesAccess() || (int)Build.VERSION.SdkInt < 30)
			{
				try
				{
					DirectoryCreate(publicRoot);
					var probe = System.IO.Path.Combine(publicRoot, ".write_test");
					System.IO.File.WriteAllText(probe, "ok");
					System.IO.File.Delete(probe);
					return publicRoot;
				}
				catch (Exception e)
				{
					AndroidFileLog.Info("OpenRA.Storage", "Public OpenRA not writable: " + e.Message);
				}
			}

			// 2) App-specific external — always OK on Android 16, no special permission
			try
			{
				var ext = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
				if (!string.IsNullOrEmpty(ext))
					return System.IO.Path.Combine(ext, "OpenRA");
			}
			catch { /* ignore */ }

			// 3) Internal
			return System.IO.Path.Combine(Application.Context.FilesDir.AbsolutePath, "OpenRA");
		}

		static void DirectoryCreate(string path)
		{
			System.IO.Directory.CreateDirectory(path);
		}
	}
}
