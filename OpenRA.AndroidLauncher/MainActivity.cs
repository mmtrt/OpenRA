// Android entry point — arm64-v8a only, Rusted Warfare touch.

using System;
using Android.App;
using Android.OS;
using Android.Views;
using Android.Content.PM;
using Android.Util;

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

		protected override void OnCreate(Bundle savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			OpenRA.Platforms.Android.AndroidNativeBootstrap.Init();

			Window.AddFlags(WindowManagerFlags.Fullscreen);
			if (Window.DecorView != null)
				Window.DecorView.SystemUiFlags =
					SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky;

			surfaceView = new GameSurfaceView(this);
			surfaceView.SetOnTouchListener(this);

			statusOverlay = new TextView(this)
			{
				Text = "OpenRA Android (arm64)\nRW touch ready\nSurfaceView active",
				Gravity = GravityFlags.Center,
				TextSize = 14f
			};
			statusOverlay.SetBackgroundColor(Android.Graphics.Color.Argb(160, 0, 0, 0));
			statusOverlay.SetTextColor(Android.Graphics.Color.White);

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

		public bool OnTouch(View v, MotionEvent e)
		{
			// Try bootstrap once the surface reports ready
			if (!bootstrapAttempted && surfaceView.IsSurfaceReady)
			{
				bootstrapAttempted = true;
				EngineBootstrap.Start(surfaceView, "ra");
				statusOverlay.Text = "OpenRA Android (arm64)\nEngine starting…\nPlatformFactory registered";
			}

			PlatformWindow ??= OpenRA.Platforms.Android.AndroidPlatformWindow.Current;
			var input = PlatformWindow?.Input;
			if (input == null)
			{
				Log.Debug("OpenRA.Touch", $"{e.ActionMasked} pointers={e.PointerCount}");
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
		}

		protected override void OnResume()
		{
			base.OnResume();
			PlatformWindow?.SetSuspended(false);
		}

		protected override void OnDestroy()
		{
			EngineBootstrap.Stop();
			PlatformWindow = null;
			base.OnDestroy();
		}
	}
}
