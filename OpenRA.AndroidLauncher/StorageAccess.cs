// Android 11–16 storage: all-files access for /storage/emulated/0/OpenRA when possible.

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
		// Binding names vary by API pack; use platform action strings.
		const string ActionManageAppAllFilesAccessPermission =
			"android.settings.MANAGE_APP_ALL_FILES_ACCESS_PERMISSION";
		const string ActionManageAllFilesAccessPermission =
			"android.settings.MANAGE_ALL_FILES_ACCESS_PERMISSION";

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
		/// Opens system Settings so the user can grant "All files access" (Android 11+ / 16).
		/// </summary>
		public static void RequestAllFilesAccess(Activity activity)
		{
			if ((int)Build.VERSION.SdkInt < 30)
				return;
			if (HasAllFilesAccess())
				return;

			try
			{
				var intent = new Intent(ActionManageAppAllFilesAccessPermission);
				intent.SetData(AUri.Parse("package:" + activity.PackageName));
				activity.StartActivity(intent);
				AndroidFileLog.Info("OpenRA.Storage", "Opened MANAGE_APP_ALL_FILES_ACCESS settings");
			}
			catch (Exception e)
			{
				try
				{
					var intent = new Intent(ActionManageAllFilesAccessPermission);
					activity.StartActivity(intent);
				}
				catch (Exception e2)
				{
					AndroidFileLog.Warn("OpenRA.Storage",
						"Cannot open all-files settings: " + e.Message + " / " + e2.Message);
				}
			}
		}

		public static string ResolveSupportDir()
		{
			const string publicRoot = "/storage/emulated/0/OpenRA";
			if (HasAllFilesAccess() || (int)Build.VERSION.SdkInt < 30)
			{
				try
				{
					System.IO.Directory.CreateDirectory(publicRoot);
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

			try
			{
				var ext = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
				if (!string.IsNullOrEmpty(ext))
					return System.IO.Path.Combine(ext, "OpenRA");
			}
			catch { /* ignore */ }

			return System.IO.Path.Combine(Application.Context.FilesDir.AbsolutePath, "OpenRA");
		}
	}
}
