// Custom Application — earliest process hooks (logcat only until SupportDir + Log channels exist).

using System;
using System.Threading.Tasks;
using Android.App;
using Android.Runtime;
using OpenRA.Platforms.Android;

namespace OpenRA.Android
{
	[Application(AllowBackup = true, HardwareAccelerated = true, LargeHeap = true)]
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
				// Before Platform.OverrideSupportDir, only logcat is available.
				AndroidPlatformLog.Info("OpenRA.App",
					"OpenRAApplication.OnCreate pid=" + global::Android.OS.Process.MyPid());

				AppDomain.CurrentDomain.UnhandledException += (_, args) =>
				{
					try
					{
						var ex = args.ExceptionObject as Exception;
						var text = ex != null
							? ex.ToString()
							: (args.ExceptionObject != null ? args.ExceptionObject.ToString() : "unknown");
						AndroidPlatformLog.Error("OpenRA.Crash",
							"UnhandledException isTerminating=" + args.IsTerminating + " " + text);
					}
					catch { /* ignore */ }
				};

				TaskScheduler.UnobservedTaskException += (_, args) =>
				{
					try
					{
						AndroidPlatformLog.Error("OpenRA.Crash", "UnobservedTaskException " + args.Exception);
						args.SetObserved();
					}
					catch { /* ignore */ }
				};

				AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
				{
					try
					{
						AndroidPlatformLog.Error("OpenRA.Crash",
							"AndroidEnvironment exception " + args.Exception);
					}
					catch { /* ignore */ }
				};
			}
			catch (Exception e)
			{
				try { global::Android.Util.Log.Error("OpenRA.App", "OnCreate failed: " + e); }
				catch { /* ignore */ }
			}
		}
	}
}
