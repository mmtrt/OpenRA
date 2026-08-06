// Android entry point — arm64-v8a only, Rusted Warfare touch.

using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AColor = global::Android.Graphics.Color;
using ALog = global::Android.Util.Log;
using AManifest = global::Android.Manifest;
using AEnv = global::Android.OS.Environment;

namespace OpenRA.Android
{
	[Activity(
		Label = "OpenRA",
		MainLauncher = true,
		ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
		ScreenOrientation = ScreenOrientation.SensorLandscape,
		Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
		LaunchMode = LaunchMode.SingleInstance)]
	public class MainActivity : Activity, View.IOnTouchListener
	{
		public static OpenRA.Platforms.Android.AndroidPlatformWindow PlatformWindow;

		GameSurfaceView surfaceView;
		TextView statusOverlay;
		bool bootstrapAttempted;
		const int StoragePermissionRequest = 1001;

		protected override void OnCreate(Bundle savedInstanceState)
		{
			base.OnCreate(savedInstanceState);

			RequestStorageIfNeeded();
			AndroidFileLog.Init();
			AndroidFileLog.Info("OpenRA.Main", "OnCreate");

			OpenRA.Platforms.Android.AndroidNativeBootstrap.Init();

			Window.AddFlags(WindowManagerFlags.Fullscreen);
			if (Window.DecorView != null)
				Window.DecorView.SystemUiFlags =
					SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky;

			surfaceView = new GameSurfaceView(this);
			surfaceView.SetOnTouchListener(this);

			statusOverlay = new TextView(this)
			{
				Text = "OpenRA Android (arm64)\nRW touch ready\nLog: " + (AndroidFileLog.ActivePath ?? AndroidFileLog.PreferredPath),
				Gravity = GravityFlags.Center,
				TextSize = 12f
			};
			statusOverlay.SetBackgroundColor(AColor.Argb(160, 0, 0, 0));
			statusOverlay.SetTextColor(AColor.White);

			var layout = new FrameLayout(this);
			layout.AddView(surfaceView, new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
			layout.AddView(statusOverlay, new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
			{
				Gravity = GravityFlags.Bottom
			});
			SetContentView(layout);
		}

		void RequestStorageIfNeeded()
		{
			if ((int)Build.VERSION.SdkInt >= 30)
			{
				try
				{
					if (!AEnv.IsExternalStorageManager)
						AndroidFileLog.Warn("OpenRA.Main", "All-files access not granted; will try path then fall back");
				}
				catch { /* ignore */ }
				return;
			}

			if ((int)Build.VERSION.SdkInt >= 23)
			{
				if (CheckSelfPermission(AManifest.Permission.WriteExternalStorage) != Permission.Granted)
				{
					RequestPermissions(
						new[] { AManifest.Permission.WriteExternalStorage, AManifest.Permission.ReadExternalStorage },
						StoragePermissionRequest);
				}
			}
		}

		public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
		{
			base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
			if (requestCode == StoragePermissionRequest)
			{
				AndroidFileLog.Init();
				AndroidFileLog.Info("OpenRA.Main", "Storage permission result; log=" + AndroidFileLog.ActivePath);
			}
		}

		public bool OnTouch(View v, MotionEvent e)
		{
			if (!bootstrapAttempted && surfaceView.IsSurfaceReady)
			{
				bootstrapAttempted = true;
				try
				{
					EngineBootstrap.Start(surfaceView, "ra");
					statusOverlay.Text = "OpenRA Android (arm64)\nEngine starting…\nLog: " + AndroidFileLog.ActivePath;
				}
				catch (Exception ex)
				{
					AndroidFileLog.Exception("OpenRA.Main", ex);
					statusOverlay.Text = "Bootstrap failed — see error.log";
				}
			}

			PlatformWindow ??= OpenRA.Platforms.Android.AndroidPlatformWindow.Current;
			var input = PlatformWindow?.Input;
			if (input == null)
			{
				ALog.Debug("OpenRA.Touch", $"{e.ActionMasked} pointers={e.PointerCount}");
				return true;
			}

			var action = e.ActionMasked;
			var index = e.ActionIndex;
			var id = e.GetPointerId(index);
			var x = (int)e.GetX(index);
			var y = (int)e.GetY(index);
			var time = e.EventTime;

			switch (action)
			{
				case MotionEventActions.Down:
				case MotionEventActions.PointerDown:
					input.OnTouchDown(id, x, y, time);
					break;
				case MotionEventActions.Move:
					for (var i = 0; i < e.PointerCount; i++)
						input.OnTouchMove(e.GetPointerId(i), (int)e.GetX(i), (int)e.GetY(i));
					break;
				case MotionEventActions.Up:
				case MotionEventActions.PointerUp:
					input.OnTouchUp(id, x, y, time);
					break;
				case MotionEventActions.Cancel:
					input.OnTouchCancel(id);
					break;
			}

			return true;
		}

		protected override void OnPause()
		{
			base.OnPause();
			PlatformWindow?.SetSuspended(true);
			AndroidFileLog.Info("OpenRA.Main", "OnPause");
		}

		protected override void OnResume()
		{
			base.OnResume();
			PlatformWindow?.SetSuspended(false);
			OpenRA.Platforms.Android.AndroidEgl.MakeCurrent();
			AndroidFileLog.Info("OpenRA.Main", "OnResume");
		}

		protected override void OnDestroy()
		{
			AndroidFileLog.Info("OpenRA.Main", "OnDestroy");
			EngineBootstrap.Stop();
			base.OnDestroy();
		}
	}
}
