// Android entry — Install Content gate, then engine start.

using System;
using System.Threading;
using System.Threading.Tasks;
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
		InstallContentView installView;
		TextView statusOverlay;
		FrameLayout root;
		bool bootstrapAttempted;
		bool engineStartRequested;
		CancellationTokenSource installCts;
		const int StoragePermissionRequest = 1001;

		protected override void OnCreate(Bundle savedInstanceState)
		{
			base.OnCreate(savedInstanceState);

			RequestStorageIfNeeded();
			if ((int)Build.VERSION.SdkInt >= 30 && !StorageAccess.HasAllFilesAccess())
			{
				// Optional: user can grant for /storage/emulated/0/OpenRA; app works without it
				AndroidFileLog.Info("OpenRA.Main", "Android 11+ : app-external storage by default; all-files optional");
			}
			AndroidFileLog.Init();
			AndroidFileLog.Info("OpenRA.Main", "OnCreate");

			OpenRA.Platforms.Android.AndroidNativeBootstrap.Init();

			Window.AddFlags(WindowManagerFlags.Fullscreen);
			if (Window.DecorView != null)
				Window.DecorView.SystemUiFlags =
					SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky;

			root = new FrameLayout(this);
			surfaceView = new GameSurfaceView(this);
			surfaceView.SetOnTouchListener(this);
			surfaceView.SurfaceReady += () =>
			{
				RunOnUiThread(() =>
				{
					if (installView == null && ContentProbe.IsBaseContentInstalled(ContentBootstrap.SupportDir))
						TryStartEngine();
				});
			};
			root.AddView(surfaceView, new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

			statusOverlay = new TextView(this)
			{
				Text = "OpenRA Android",
				Gravity = GravityFlags.Center,
				TextSize = 13f
			};
			statusOverlay.SetBackgroundColor(AColor.Argb(160, 0, 0, 0));
			statusOverlay.SetTextColor(AColor.White);
			root.AddView(statusOverlay, new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
			{
				Gravity = GravityFlags.Bottom
			});

			SetContentView(root);

			// Resolve SupportDir early (creates public OpenRA tree when possible)
			ContentBootstrap.EnsureLayout(null);
			var support = ContentBootstrap.SupportDir;
			AndroidFileLog.Info("OpenRA.Main", "SupportDir=" + support);
			if ((int)Build.VERSION.SdkInt >= 30 && !StorageAccess.HasAllFilesAccess())
				OfferAllFilesAccessOnce();


			if (ContentProbe.IsBaseContentInstalled(support))
			{
				AndroidFileLog.Info("OpenRA.Main", "Content present — skip install UI");
				statusOverlay.Text = "Content found — starting engine…\n" + support;
				// Engine starts when surface is ready (OnTouch / surface path)
			}
			else
			{
				AndroidFileLog.Info("OpenRA.Main", "Content missing: " + ContentProbe.MissingSummary(support));
				ShowInstallUi();
			}
		}

		void ShowInstallUi()
		{
			if (installView != null)
				return;

			installView = new InstallContentView(this);
			installView.QuickInstallClicked += OnQuickInstall;
			installView.AdvancedInstallClicked += OnAdvancedInstall;
			installView.QuitClicked += () => Finish();
			root.AddView(installView, new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
			statusOverlay.Text = "Install required content to continue\n" + ContentBootstrap.SupportDir;
		}

		void HideInstallUi()
		{
			if (installView == null)
				return;
			root.RemoveView(installView);
			installView = null;
		}

		async void OnQuickInstall()
		{
			installCts?.Cancel();
			installCts = new CancellationTokenSource();
			var support = ContentBootstrap.SupportDir;
			installView?.SetBusy(true, "Starting Quick Install…");

			var progress = new Progress<QuickInstallService.Progress>(p =>
			{
				RunOnUiThread(() => installView?.SetProgress(p.Status, p.Fraction));
			});

			try
			{
				await QuickInstallService.InstallAsync(support, progress, installCts.Token)
					.ConfigureAwait(true);

				RunOnUiThread(() =>
				{
					HideInstallUi();
					statusOverlay.Text = "Content installed — starting engine…";
					TryStartEngine();
				});
			}
			catch (System.OperationCanceledException)
			{
				RunOnUiThread(() => installView?.SetBusy(false, "Cancelled."));
			}
			catch (Exception e)
			{
				AndroidFileLog.Exception("OpenRA.Install", e);
				RunOnUiThread(() =>
				{
					installView?.SetBusy(false, "Failed: " + e.Message);
					Toast.MakeText(this, "Quick Install failed — see openra.log", ToastLength.Long).Show();
				});
			}
		}

		void OnAdvancedInstall()
		{
			// Phase 1: point user at path; SAF picker can come next
			var path = ContentProbe.ContentRaV2(ContentBootstrap.SupportDir);
			var msg =
				"Copy original RA files into:\n" + path + "\n\n" +
				"Required: allies.mix, conquer.mix, interior.mix, hires.mix, lores.mix, " +
				"local.mix, speech.mix, russian.mix, snow.mix, sounds.mix, temperat.mix\n\n" +
				"Then tap Quick Install again or restart the app.";
			AndroidFileLog.Info("OpenRA.Install", "Advanced Install help shown");
			new AlertDialog.Builder(this)
				.SetTitle("Advanced Install")
				.SetMessage(msg)
				.SetPositiveButton("I copied files — check again", (s, e) =>
				{
					if (ContentProbe.IsBaseContentInstalled(ContentBootstrap.SupportDir))
					{
						HideInstallUi();
						statusOverlay.Text = "Content found — starting engine…";
						TryStartEngine();
					}
					else
					{
						Toast.MakeText(this,
							"Still missing: " + ContentProbe.MissingSummary(ContentBootstrap.SupportDir),
							ToastLength.Long).Show();
					}
				})
				.SetNeutralButton("All-files access", (s, e) => StorageAccess.RequestAllFilesAccess(this))
				.SetNegativeButton("OK", (s, e) => { })
				.Show();
		}

		void TryStartEngine()
		{
			if (engineStartRequested)
				return;
			if (!ContentProbe.IsBaseContentInstalled(ContentBootstrap.SupportDir))
			{
				ShowInstallUi();
				return;
			}

			if (!surfaceView.IsSurfaceReady)
			{
				statusOverlay.Text = "Waiting for surface…";
				AndroidFileLog.Info("OpenRA.Main", "Engine deferred until surface ready");
				return;
			}

			engineStartRequested = true;
			bootstrapAttempted = true;
			statusOverlay.Text = "Starting OpenRA…\n" + ContentBootstrap.SupportDir;
			try
			{
				EngineBootstrap.Start(surfaceView, "ra");
			}
			catch (Exception ex)
			{
				engineStartRequested = false;
				AndroidFileLog.Exception("OpenRA.Main", ex);
				statusOverlay.Text = "Engine start failed — see openra.log";
			}
		}

		public bool OnTouch(View v, MotionEvent e)
		{
			// Don't start engine while install UI is up
			if (installView != null)
				return true;

			if (!bootstrapAttempted && surfaceView.IsSurfaceReady)
			{
				if (ContentProbe.IsBaseContentInstalled(ContentBootstrap.SupportDir))
					TryStartEngine();
				else
					ShowInstallUi();
			}

			PlatformWindow ??= OpenRA.Platforms.Android.AndroidPlatformWindow.Current;
			var input = PlatformWindow?.Input;
			if (input == null)
				return true;

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

		protected override void OnResume()
		{
			base.OnResume();
			OpenRA.Platforms.Android.AndroidEgl.MakeCurrent();
			AndroidFileLog.Info("OpenRA.Main", "OnResume");
			// If user returned after copying files
			if (installView != null && ContentProbe.IsBaseContentInstalled(ContentBootstrap.SupportDir))
			{
				HideInstallUi();
				TryStartEngine();
			}
			else if (installView == null && !engineStartRequested
			         && ContentProbe.IsBaseContentInstalled(ContentBootstrap.SupportDir)
			         && surfaceView.IsSurfaceReady)
			{
				TryStartEngine();
			}
		}

		
		bool allFilesPromptShown;
		void OfferAllFilesAccessOnce()
		{
			if (allFilesPromptShown)
				return;
			allFilesPromptShown = true;
			new AlertDialog.Builder(this)
				.SetTitle("Storage access")
				.SetMessage(
					"To use /storage/emulated/0/OpenRA (survives uninstall), grant All files access.\n\n" +
					"Without it, data stays under Android/data/net.openra.android/files/OpenRA.")
				.SetPositiveButton("Open settings", (s, e) => StorageAccess.RequestAllFilesAccess(this))
				.SetNegativeButton("Use app folder", (s, e) => { })
				.Show();
		}

		void RequestStorageIfNeeded()
		{
			if ((int)Build.VERSION.SdkInt >= 30)
			{
				try
				{
					if (!AEnv.IsExternalStorageManager)
						AndroidFileLog.Warn("OpenRA.Main", "All-files access not granted; may fall back to app storage");
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
				ContentBootstrap.EnsureLayout(null);
				AndroidFileLog.Info("OpenRA.Main", "Storage result; SupportDir=" + ContentBootstrap.SupportDir);
			}
		}

		protected override void OnPause()
		{
			base.OnPause();
			PlatformWindow?.SetSuspended(true);
			AndroidFileLog.Info("OpenRA.Main", "OnPause");
		}

		protected override void OnDestroy()
		{
			installCts?.Cancel();
			AndroidFileLog.Info("OpenRA.Main", "OnDestroy");
			EngineBootstrap.Stop();
			base.OnDestroy();
		}
	}
}
