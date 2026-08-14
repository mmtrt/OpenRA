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
		Label = "@string/app_name",
		MainLauncher = true,
		ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
		ScreenOrientation = ScreenOrientation.SensorLandscape,
		Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
		LaunchMode = LaunchMode.SingleInstance,
		WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateAlwaysHidden)]
	public class MainActivity : Activity, View.IOnTouchListener
	{
		public static OpenRA.Platforms.Android.AndroidPlatformWindow PlatformWindow;

		/// <summary>Active activity — used to Finish after main-menu Quit.</summary>
		public static MainActivity Current { get; private set; }

		GameSurfaceView surfaceView;
		InstallContentView installView;
		TextView statusOverlay;
		FrameLayout root;
		bool bootstrapAttempted;
		bool engineStartRequested;
		CancellationTokenSource installCts;
		AndroidAudioFocus audioFocus;
		const int StoragePermissionRequest = 1001;

		protected override void OnCreate(Bundle savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			Current = this;

			try
			{
				BootLog.Init();
				BootLog.Info("MainActivity.OnCreate begin");
				AndroidFileLog.Init();
				AndroidFileLog.Info("OpenRA.Main", "OnCreate begin");

				try
				{
					RequestStorageIfNeeded();
				}
				catch (Exception e)
				{
					AndroidFileLog.Warn("OpenRA.Main", "RequestStorage: " + e.Message);
				}

				// Native libs deferred until engine start — LoadLibrary in OnCreate can abort the process
				AndroidFileLog.Info("OpenRA.Main", "Building UI");

				ApplyImmersiveMode();
				try { VolumeControlStream = global::Android.Media.Stream.Music; } catch { /* ignore */ }
				try
				{
					// Keep screen on during play; avoid thermal throttling surprises mid-match
					Window.AddFlags(WindowManagerFlags.KeepScreenOn | WindowManagerFlags.Fullscreen);
					if ((int)Build.VERSION.SdkInt >= 28 && Window.Attributes != null)
					{
						var lp = Window.Attributes;
						lp.LayoutInDisplayCutoutMode = global::Android.Views.LayoutInDisplayCutoutMode.Default;
						Window.Attributes = lp;
					}
				}
				catch { /* older API */ }

				root = new FrameLayout(this);
				surfaceView = new GameSurfaceView(this);
				surfaceView.SetOnTouchListener(this);
				surfaceView.SurfaceReady += () =>
				{
					RunOnUiThread(() =>
					{
						try
						{
							// Content MIX alone is not enough: first run must finish APK extract
							// (mods + assemblies) or the engine crashes; second launch then works.
							if (installView == null
							    && ContentBootstrap.IsReady
							    && ContentProbe.IsBaseContentInstalled(ContentBootstrap.SupportDir))
								TryStartEngine();
						}
						catch (Exception e)
						{
							AndroidFileLog.Exception("OpenRA.Main.SurfaceReady", e);
						}
					});
				};
				root.AddView(surfaceView, new FrameLayout.LayoutParams(
					ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

				try
				{
					AndroidSoftKeyboard.Attach(this, root, surfaceView);
					OpenRA.Platforms.Android.AndroidKeyboardBridge.SetWanted = AndroidSoftKeyboard.SetWanted;
				}
				catch (Exception e) { AndroidFileLog.Warn("OpenRA.Main", "SoftKeyboard: " + e.Message); }

				try
				{
					audioFocus = new AndroidAudioFocus(this);
					audioFocus.Start();
				}
				catch (Exception e) { AndroidFileLog.Warn("OpenRA.Main", "AudioFocus: " + e.Message); }

				statusOverlay = new TextView(this)
				{
					Text = "OpenRA Android",
					Gravity = GravityFlags.Center,
					TextSize = 13f
				};
				statusOverlay.SetBackgroundColor(AColor.Argb(0, 0, 0, 0)); // transparent — do not cover GL
				statusOverlay.SetTextColor(AColor.White);
				root.AddView(statusOverlay, new FrameLayout.LayoutParams(
					ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
				{
					Gravity = GravityFlags.Bottom
				});

				SetContentView(root);
				AndroidFileLog.Info("OpenRA.Main", "ContentView set");

				ContentBootstrap.EnsureLayout(null);
				var support = ContentBootstrap.SupportDir;
				// Open SupportDir/Logs channels ASAP so install/UI lines are not logcat-only.
				try { EngineBootstrap.InitOfficialLogging(support); }
				catch (Exception e) { AndroidFileLog.Warn("OpenRA.Main", "InitOfficialLogging: " + e.Message); }
				AndroidFileLog.Info("OpenRA.Main", "SupportDir=" + support);

				if (ContentProbe.IsBaseContentInstalled(support))
				{
					AndroidFileLog.Info("OpenRA.Main", "Content present — skip install UI");
					statusOverlay.Text = "Content found — starting engine…\n" + support;
				}
				else
				{
					AndroidFileLog.Info("OpenRA.Main", "Content missing: " + ContentProbe.MissingSummary(support));
					ShowInstallUi();
				}

				AndroidFileLog.Info("OpenRA.Main", "OnCreate done");
			}
			catch (Exception e)
			{
				try { BootLog.Exception("MainActivity.OnCreate", e); } catch { /* ignore */ }
				try { AndroidFileLog.Exception("OpenRA.Main.OnCreate", e); }
				catch { ALog.Error("OpenRA", "OnCreate fatal: " + e); }

				try
				{
					Toast.MakeText(this, "Startup error — see SupportDir/Logs/", ToastLength.Long).Show();
				}
				catch { /* ignore */ }
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
			installView.CancelDownloadClicked += () =>
			{
				try { installCts?.Cancel(); } catch { /* ignore */ }
			};
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
			installView?.ShowDownloadProgress();
			installView?.SetProgress("Fetching mirror list…", 0);

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
				RunOnUiThread(() => installView?.ShowChoice("Cancelled."));
			}
			catch (Exception e)
			{
				AndroidFileLog.Exception("OpenRA.Install", e);
				RunOnUiThread(() =>
				{
					installView?.ShowChoice("Failed: " + e.Message);
					Toast.MakeText(this, "Quick Install failed — see SupportDir/Logs/", ToastLength.Long).Show();
				});
			}
		}

		void OnAdvancedInstall()
		{
			var mod = ModInfo.Current;
			var path = ContentProbe.ContentRoot(ContentBootstrap.SupportDir);
			var markers = string.Join(", ", mod.MarkerFiles.Select(System.IO.Path.GetFileName));
			var msg =
				"Copy original " + mod.DisplayName + " files into:\n" + path + "\n\n" +
				"Markers: " + markers + "\n\n" +
				"Then restart the app or tap check again.";
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
				.SetNegativeButton("OK", (s, e) => { })
				.Show();
		}

		void TryStartEngine()
		{
			if (engineStartRequested)
				return;
			if (!ContentBootstrap.IsReady)
			{
				// Compile-in: assemblies may only become "staged" after marker / Load.
				try
				{
					ContentBootstrap.EnsureCompileInMarker();
					ContentBootstrap.EnsureLayout(ContentBootstrap.SupportDir);
				}
				catch (Exception e)
				{
					AndroidFileLog.Warn("OpenRA.Main", "EnsureLayout retry: " + e.Message);
				}
			}
			if (!ContentBootstrap.IsReady)
			{
				AndroidFileLog.Warn("OpenRA.Main",
					"TryStartEngine deferred — ready=" + ContentBootstrap.IsReady
					+ " mods=" + ContentBootstrap.HasAnyMod()
					+ " asm=" + ContentBootstrap.HasStagedModAssembly());
				statusOverlay.Text = "Preparing engine assets…
mods=" + ContentBootstrap.HasAnyMod()
					+ " asm=" + ContentBootstrap.HasStagedModAssembly();
				// Retry shortly — first launch extract can finish after first surface frame
				try
				{
					statusOverlay.PostDelayed(() =>
					{
						try { if (!engineStartRequested) TryStartEngine(); }
						catch { /* ignore */ }
					}, 750);
				}
				catch { /* ignore */ }
				return;
			}
			if (!ContentProbe.IsBaseContentInstalled(ContentBootstrap.SupportDir))
			{
				ShowInstallUi();
				return;
			}

			if (!surfaceView.IsSurfaceReady || !OpenRA.Platforms.Android.AndroidEgl.IsReady)
			{
				statusOverlay.Text = "Waiting for graphics surface…";
				AndroidFileLog.Info("OpenRA.Main", "Engine deferred — surface/EGL not ready");
				if (surfaceView.Width > 0 && surfaceView.Holder?.Surface != null
				    && !OpenRA.Platforms.Android.AndroidEgl.IsReady)
				{
					if (OpenRA.Platforms.Android.AndroidEgl.Initialize(surfaceView.Holder, surfaceView.Width, surfaceView.Height))
						AndroidFileLog.Info("OpenRA.Main", "EGL re-init ok");
				}
				if (!surfaceView.IsSurfaceReady || !OpenRA.Platforms.Android.AndroidEgl.IsReady)
					return;
			}

			engineStartRequested = true;
			bootstrapAttempted = true;
			statusOverlay.Text = "Starting OpenRA...\n" + ContentBootstrap.SupportDir;
			try
			{
				// Load native libs here (not OnCreate)
				OpenRA.Platforms.Android.AndroidNativeBootstrap.Init();
				OpenRA.Platforms.Android.AndroidEgl.ReleaseCurrent();
				EngineBootstrap.Start(surfaceView, BuildConfig.ModId);

				// Engine owns the GL surface — drop the opaque status strip so it
				// cannot cover rendered frames (was full-screen black with only text).
				statusOverlay.PostDelayed(() =>
				{
					try
					{
						if (EngineBootstrap.IsRunning)
						{
							statusOverlay.Visibility = ViewStates.Gone;
							AndroidFileLog.Info("OpenRA.Main", "Status overlay hidden — engine running");
						}
						else
						{
							statusOverlay.Text = "Engine exited — see SupportDir/Logs/";
							statusOverlay.Visibility = ViewStates.Visible;
						}
					}
					catch (Exception e)
					{
						AndroidFileLog.Warn("OpenRA.Main", "overlay: " + e.Message);
					}
				}, 3000);
			}
			catch (Exception ex)
			{
				engineStartRequested = false;
				AndroidFileLog.Exception("OpenRA.Main", ex);
				statusOverlay.Visibility = ViewStates.Visible;
				statusOverlay.Text = "Engine start failed — see SupportDir/Logs/";
			}
		}

		public bool OnTouch(View v, MotionEvent e)
		{
			try { AndroidSoftKeyboard.NotifyUserTouch(); } catch { /* ignore */ }

			try
			{
				if (v != null)
				{
					OpenRA.Platforms.Android.AndroidInput.ViewWidth = v.Width;
					OpenRA.Platforms.Android.AndroidInput.ViewHeight = v.Height;
				}
			}
			catch { /* ignore */ }

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
						input.OnTouchMove(e.GetPointerId(i), (int)e.GetX(i), (int)e.GetY(i), time);
					break;
				case MotionEventActions.Up:
				case MotionEventActions.PointerUp:
					input.OnTouchUp(id, x, y, time);
					break;
				case MotionEventActions.Cancel:
					// Whole gesture cancelled (palm rejection, etc.) — clear all contacts.
					input.OnTouchCancel(-1);
					break;
			}

			return true;
		}

		protected override void OnResume()
		{
			base.OnResume();
			ApplyImmersiveMode();
			try { PlatformWindow?.SetSuspended(false); } catch { /* ignore */ }
			try { OpenRA.Platforms.Android.AndroidAudioBridge.SetSuspended?.Invoke(false); } catch { /* ignore */ }
			try { audioFocus?.Start(); } catch { /* ignore */ }
			AndroidFileLog.Info("OpenRA.Main", "OnResume");

			if (installView != null && ContentProbe.IsBaseContentInstalled(ContentBootstrap.SupportDir))
			{
				HideInstallUi();
				TryStartEngine();
			}
			else if (installView == null && !engineStartRequested
			         && !string.IsNullOrEmpty(ContentBootstrap.SupportDir)
			         && ContentProbe.IsBaseContentInstalled(ContentBootstrap.SupportDir)
			         && surfaceView != null && surfaceView.IsSurfaceReady)
			{
				TryStartEngine();
			}
		}

		void RequestStorageIfNeeded()
		{
			if ((int)Build.VERSION.SdkInt >= 30)
			{
				try
				{
					if (!AEnv.IsExternalStorageManager)
						AndroidFileLog.Info("OpenRA.Main", "Using app-private storage only");
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
			try { PlatformWindow?.SetSuspended(true); } catch { /* ignore */ }
			// Pause OpenAL + detach context so we do not burn CPU/battery in background
			try { OpenRA.Platforms.Android.AndroidAudioBridge.SetSuspended?.Invoke(true); } catch { /* ignore */ }
			try { AndroidSoftKeyboard.ForceHide(); } catch { /* ignore */ }
			AndroidFileLog.Info("OpenRA.Main", "OnPause");
		}

		public override void OnWindowFocusChanged(bool hasFocus)
		{
			base.OnWindowFocusChanged(hasFocus);
			if (hasFocus)
				ApplyImmersiveMode();
		}

		void ApplyImmersiveMode()
		{
			try
			{
				if (Window?.DecorView == null)
					return;
				Window.DecorView.SystemUiFlags =
					SystemUiFlags.HideNavigation
					| SystemUiFlags.Fullscreen
					| SystemUiFlags.ImmersiveSticky
					| SystemUiFlags.LayoutStable
					| SystemUiFlags.LayoutHideNavigation
					| SystemUiFlags.LayoutFullscreen;
			}
			catch { /* ignore */ }
		}

		protected override void OnDestroy()
		{
			if (ReferenceEquals(Current, this))
				Current = null;
			installCts?.Cancel();
			AndroidFileLog.Info("OpenRA.Main", "OnDestroy");
			try { audioFocus?.Stop(); } catch { /* ignore */ }
			try { EngineBootstrap.Stop(); }
			catch { /* ignore */ }
			try { OpenRA.Platforms.Android.AndroidEgl.Destroy(); }
			catch { /* ignore */ }
			base.OnDestroy();
		}
	}
}
