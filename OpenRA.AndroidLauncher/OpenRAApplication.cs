// Custom Application — earliest process hooks.
// Uses BootLog only (no OpenRA.Game types) so ClassNotFound / early failures still leave a file.

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
						var text = ex != null
							? ex.ToString()
							: (args.ExceptionObject != null ? args.ExceptionObject.ToString() : "unknown");
						BootLog.Error("UnhandledException isTerminating=" + args.IsTerminating + " " + text);
						AndroidPlatformLog.Error("OpenRA.Crash", text);
					}
					catch { /* ignore */ }
				};

				TaskScheduler.UnobservedTaskException += (_, args) =>
				{
					try
					{
						BootLog.Error("UnobservedTaskException " + args.Exception);
						args.SetObserved();
					}
					catch { /* ignore */ }
				};

				AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
				{
					try
					{
						BootLog.Error("AndroidEnvironment exception " + args.Exception);
					}
					catch { /* ignore */ }
				};

				BootLog.Info("Crash hooks installed");
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
				// Only heavy GC on serious pressure — avoid hitching during play on moderate levels
				if (level == TrimMemory.RunningCritical || level == TrimMemory.Complete
				    || level == TrimMemory.ModComplete || level == TrimMemory.RunningLow)
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
