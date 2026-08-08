#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port — EGL ES 3.0 host.
 */
#endregion

using System;
using Android.Opengl;
using Android.Views;

namespace OpenRA.Platforms.Android
{
	public static class AndroidEgl
	{
		const int EglContextClientVersion = 0x3098;
		const int EglOpenGlEs3Bit = 0x0040;

		static readonly object Gate = new();

		static EGLDisplay display = EGL14.EglNoDisplay;
		static EGLContext context = EGL14.EglNoContext;
		static EGLSurface surface = EGL14.EglNoSurface;
		static EGLConfig config;
		static bool initialized;

		public static bool IsReady
		{
			get
			{
				lock (Gate)
					return initialized
						&& display != null && display != EGL14.EglNoDisplay
						&& surface != null && surface != EGL14.EglNoSurface
						&& context != null && context != EGL14.EglNoContext;
			}
		}

		public static int SurfaceWidth { get; private set; }
		public static int SurfaceHeight { get; private set; }
		public static string LastError { get; private set; } = "";

		/// <summary>Display+context alive (surface may be temporarily gone).</summary>
		public static bool HasDisplayContext
		{
			get
			{
				lock (Gate)
					return initialized
						&& display != null && display != EGL14.EglNoDisplay
						&& context != null && context != EGL14.EglNoContext
						&& config != null;
			}
		}

		public static bool Initialize(ISurfaceHolder holder, int width, int height)
		{
			lock (Gate)
			{
				try
				{
					DestroyUnlocked();
					SurfaceWidth = Math.Max(1, width);
					SurfaceHeight = Math.Max(1, height);

					display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
					if (display == null || display == EGL14.EglNoDisplay)
						return FailHard("eglGetDisplay failed");

					var version = new int[2];
					if (!EGL14.EglInitialize(display, version, 0, version, 1))
						return FailHard("eglInitialize failed: " + EglError());

					AndroidPlatformLog.Info("OpenRA.EGL", "EGL " + version[0] + "." + version[1]);

					int[] attribList =
					{
						EGL14.EglRedSize, 8,
						EGL14.EglGreenSize, 8,
						EGL14.EglBlueSize, 8,
						EGL14.EglAlphaSize, 8,
						EGL14.EglDepthSize, 16,
						EGL14.EglStencilSize, 8,
						EGL14.EglRenderableType, EglOpenGlEs3Bit,
						EGL14.EglSurfaceType, EGL14.EglWindowBit,
						EGL14.EglNone
					};

					var configs = new EGLConfig[1];
					var numConfigs = new int[1];
					if (!EGL14.EglChooseConfig(display, attribList, 0, configs, 0, configs.Length, numConfigs, 0)
					    || numConfigs[0] == 0)
					{
						AndroidPlatformLog.Warn("OpenRA.EGL", "ES3 config missing, trying ES2");
						attribList = new[]
						{
							EGL14.EglRedSize, 8,
							EGL14.EglGreenSize, 8,
							EGL14.EglBlueSize, 8,
							EGL14.EglAlphaSize, 8,
							EGL14.EglDepthSize, 16,
							EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit,
							EGL14.EglSurfaceType, EGL14.EglWindowBit,
							EGL14.EglNone
						};
						if (!EGL14.EglChooseConfig(display, attribList, 0, configs, 0, configs.Length, numConfigs, 0)
						    || numConfigs[0] == 0)
							return FailHard("eglChooseConfig failed: " + EglError());
					}

					config = configs[0];

					int[] ctxAttribs =
					{
						EglContextClientVersion, 3,
						EGL14.EglNone
					};
					context = EGL14.EglCreateContext(display, config, EGL14.EglNoContext, ctxAttribs, 0);
					if (context == null || context == EGL14.EglNoContext)
					{
						ctxAttribs = new[] { EglContextClientVersion, 2, EGL14.EglNone };
						context = EGL14.EglCreateContext(display, config, EGL14.EglNoContext, ctxAttribs, 0);
						if (context == null || context == EGL14.EglNoContext)
							return FailHard("eglCreateContext failed: " + EglError());
					}

					if (!CreateWindowSurfaceUnlocked(holder))
						return FailHard("eglCreateWindowSurface failed: " + LastError);

					// Leave unbound so the game thread can MakeCurrent.
					EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);

					initialized = true;
					LastError = "";
					AndroidPlatformLog.Info("OpenRA.EGL", "ready " + SurfaceWidth + "x" + SurfaceHeight + " (unbound for game thread)");
					return true;
				}
				catch (Exception e)
				{
					return FailHard("Exception: " + e);
				}
			}
		}

		/// <summary>
		/// Recreate the window surface only when size changes or the surface is gone.
		/// Never tear down display/context on a transient create failure (that caused
		/// endless "MakeCurrent: no display" after eglCreateWindowSurface 0x3003).
		/// </summary>
		public static bool Resize(ISurfaceHolder holder, int width, int height)
		{
			lock (Gate)
			{
				width = Math.Max(1, width);
				height = Math.Max(1, height);

				if (!initialized
				    || display == null || display == EGL14.EglNoDisplay
				    || context == null || context == EGL14.EglNoContext
				    || config == null)
					return Initialize(holder, width, height);

				// Same size + live surface → no-op (SurfaceChanged often fires spuriously).
				if (width == SurfaceWidth && height == SurfaceHeight
				    && surface != null && surface != EGL14.EglNoSurface)
				{
					AndroidPlatformLog.Info("OpenRA.EGL", "Resize no-op " + width + "x" + height);
					return true;
				}

				SurfaceWidth = width;
				SurfaceHeight = height;

				try
				{
					EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);

					if (surface != null && surface != EGL14.EglNoSurface)
					{
						EGL14.EglDestroySurface(display, surface);
						surface = EGL14.EglNoSurface;
					}

					if (!CreateWindowSurfaceUnlocked(holder))
					{
						// Soft fail: keep display + context so a later SurfaceCreated can recover.
						AndroidPlatformLog.Error("OpenRA.EGL",
							"Resize: create surface failed (keeping context): " + LastError);
						return false;
					}

					EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
					AndroidPlatformLog.Info("OpenRA.EGL", "Resize OK " + width + "x" + height);
					return true;
				}
				catch (Exception e)
				{
					AndroidPlatformLog.Error("OpenRA.EGL", "Resize exception: " + e.Message);
					return false;
				}
			}
		}

		/// <summary>
		/// Soft destroy: drop the window surface only (SurfaceDestroyed). Keep display/context
		/// so SurfaceCreated can reattach without a full re-init.
		/// </summary>
		public static void DestroySurfaceOnly()
		{
			lock (Gate)
			{
				if (display == null || display == EGL14.EglNoDisplay)
					return;

				EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
				if (surface != null && surface != EGL14.EglNoSurface)
				{
					EGL14.EglDestroySurface(display, surface);
					surface = EGL14.EglNoSurface;
					AndroidPlatformLog.Info("OpenRA.EGL", "Surface destroyed (context kept)");
				}
			}
		}

		/// <summary>
		/// After SurfaceDestroyed + SurfaceCreated: rebuild window surface on existing context.
		/// </summary>
		public static bool RecreateSurface(ISurfaceHolder holder, int width, int height)
		{
			lock (Gate)
			{
				width = Math.Max(1, width);
				height = Math.Max(1, height);
				SurfaceWidth = width;
				SurfaceHeight = height;

				if (!initialized
				    || display == null || display == EGL14.EglNoDisplay
				    || context == null || context == EGL14.EglNoContext
				    || config == null)
					return Initialize(holder, width, height);

				try
				{
					EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
					if (surface != null && surface != EGL14.EglNoSurface)
					{
						EGL14.EglDestroySurface(display, surface);
						surface = EGL14.EglNoSurface;
					}

					if (!CreateWindowSurfaceUnlocked(holder))
					{
						AndroidPlatformLog.Error("OpenRA.EGL", "RecreateSurface failed: " + LastError);
						return false;
					}

					EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
					AndroidPlatformLog.Info("OpenRA.EGL", "RecreateSurface OK " + width + "x" + height);
					return true;
				}
				catch (Exception e)
				{
					AndroidPlatformLog.Error("OpenRA.EGL", "RecreateSurface: " + e.Message);
					return false;
				}
			}
		}

		public static bool MakeCurrent()
		{
			lock (Gate)
			{
				if (display == null || display == EGL14.EglNoDisplay)
					return FailKeep("MakeCurrent: no display");
				if (surface == null || surface == EGL14.EglNoSurface)
					return FailKeep("MakeCurrent: no surface");
				if (context == null || context == EGL14.EglNoContext)
					return FailKeep("MakeCurrent: no context");

				if (!EGL14.EglMakeCurrent(display, surface, surface, context))
					return FailKeep("eglMakeCurrent failed: " + EglError());

				LastError = "";
				return true;
			}
		}

		public static void ReleaseCurrent()
		{
			lock (Gate)
			{
				if (display != null && display != EGL14.EglNoDisplay)
					EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
			}
		}

		public static void SwapBuffers()
		{
			lock (Gate)
			{
				if (!initialized || surface == null || surface == EGL14.EglNoSurface)
					return;
				if (!EGL14.EglSwapBuffers(display, surface))
					AndroidPlatformLog.Warn("OpenRA.EGL", "eglSwapBuffers: " + EglError());
			}
		}

		public static void Destroy()
		{
			lock (Gate) DestroyUnlocked();
		}

		static bool CreateWindowSurfaceUnlocked(ISurfaceHolder holder)
		{
			var nativeWindow = holder?.Surface;
			if (nativeWindow == null)
			{
				LastError = "SurfaceHolder.Surface is null";
				return false;
			}

			int[] surfaceAttribs = { EGL14.EglNone };
			surface = EGL14.EglCreateWindowSurface(display, config, nativeWindow, surfaceAttribs, 0);
			if (surface == null || surface == EGL14.EglNoSurface)
			{
				LastError = "eglCreateWindowSurface failed: " + EglError();
				surface = EGL14.EglNoSurface;
				return false;
			}

			LastError = "";
			return true;
		}

		static void DestroyUnlocked()
		{
			if (display != null && display != EGL14.EglNoDisplay)
			{
				EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
				if (surface != null && surface != EGL14.EglNoSurface)
					EGL14.EglDestroySurface(display, surface);
				if (context != null && context != EGL14.EglNoContext)
					EGL14.EglDestroyContext(display, context);
				EGL14.EglTerminate(display);
			}

			display = EGL14.EglNoDisplay;
			context = EGL14.EglNoContext;
			surface = EGL14.EglNoSurface;
			config = null;
			initialized = false;
		}

		static bool FailHard(string message)
		{
			LastError = message;
			AndroidPlatformLog.Error("OpenRA.EGL", message);
			DestroyUnlocked();
			return false;
		}

		static int failKeepCount;
		static string lastFailKeep;

		static bool FailKeep(string message)
		{
			LastError = message;
			// Rate-limit identical spam (SurfaceDestroyed → thousands of MakeCurrent fails).
			if (message != lastFailKeep)
			{
				lastFailKeep = message;
				failKeepCount = 0;
			}
			failKeepCount++;
			if (failKeepCount <= 3 || failKeepCount % 120 == 0)
				AndroidPlatformLog.Error("OpenRA.EGL", message + " (x" + failKeepCount + ")");
			return false;
		}

		static string EglError() => "0x" + EGL14.EglGetError().ToString("X");
	}
}
