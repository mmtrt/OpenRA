// Custom Application — earliest process hooks.
// Uses BootLog + CrashReporter only (no OpenRA.Game types) so early failures still leave a file.

using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Runtime;
using OpenRA.Platforms.Android;

namespace OpenRA.Android
{
	[Application]
	public class OpenRAApplication : Application
	{
		public OpenRAApplication(IntPtr javaReference, JniHandleOwnership transfer)
			: base(javaReference, transfer)
		{
		}

		public override void OnCreate()
		{
			base.OnCreate();

			try
			{
				BootLog.Init();
				BootLog.Info("OpenRAApplication.OnCreate pid=" + global::Android.OS.Process.MyPid());

				AppDomain.CurrentDomain.UnhandledException += (_, args) =>
				{
					try
					{
						var ex = args.ExceptionObject as Exception;
						CrashReporter.Report("AppDomain.UnhandledException", ex, args.IsTerminating);
					}
					catch { /* ignore */ }
				};

				TaskScheduler.UnobservedTaskException += (_, args) =>
				{
					try
					{
						CrashReporter.Report("TaskScheduler.UnobservedTaskException", args.Exception);
						args.SetObserved();
					}
					catch { /* ignore */ }
				};

				AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
				{
					try
					{
						CrashReporter.Report("AndroidEnvironment.UnhandledExceptionRaiser", args.Exception);
						// Keep default handling so process can still terminate cleanly
					}
					catch { /* ignore */ }
				};

				BootLog.Info("Crash hooks installed → Logs/crash.log under package data dir");
			}
			catch (Exception e)
			{
				try { global::Android.Util.Log.Error("OpenRA.App", "OnCreate failed: " + e); }
				catch { /* ignore */ }
			}
		}

		public override void OnTrimMemory(TrimMemory level)
		{
			base.OnTrimMemory(level);
			try
			{
				BootLog.Info("OnTrimMemory " + level);
				if (level == TrimMemory.RunningCritical || level == TrimMemory.Complete
				    || level == TrimMemory.Moderate || level == TrimMemory.RunningLow)
				{
					GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
				}
			}
			catch { /* ignore */ }
		}

		public override void OnLowMemory()
		{
			base.OnLowMemory();
			try
			{
				BootLog.Info("OnLowMemory");
				GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
			}
			catch { /* ignore */ }
		}
	}
}
