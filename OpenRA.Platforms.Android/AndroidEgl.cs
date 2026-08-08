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
			get { lock (Gate) return initialized && surface != null && surface != EGL14.EglNoSurface && context != null && context != EGL14.EglNoContext; }
		}

		public static int SurfaceWidth { get; private set; }
		public static int SurfaceHeight { get; private set; }
		public static string LastError { get; private set; } = "";

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
						return Fail("eglGetDisplay failed");

					var version = new int[2];
					if (!EGL14.EglInitialize(display, version, 0, version, 1))
						return Fail("eglInitialize failed: " + EglError());

					AndroidPlatformLog.Info("OpenRA.EGL", $"EGL {version[0]}.{version[1]}");

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
							return Fail("eglChooseConfig failed: " + EglError());
					}

					config = configs[0];

					int[] ctxAttribs = { EglContextClientVersion, 3, EGL14.EglNone };
					context = EGL14.EglCreateContext(display, config, EGL14.EglNoContext, ctxAttribs, 0);
					if (context == null || context == EGL14.EglNoContext)
					{
						ctxAttribs = new[] { EglContextClientVersion, 2, EGL14.EglNone };
						context = EGL14.EglCreateContext(display, config, EGL14.EglNoContext, ctxAttribs, 0);
						if (context == null || context == EGL14.EglNoContext)
							return Fail("eglCreateContext failed: " + EglError());
					}

					if (!CreateWindowSurfaceUnlocked(holder))
						return false;

					// Leave unbound after init so the game thread can MakeCurrent cleanly
					EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);

					initialized = true;
					LastError = "";
					AndroidPlatformLog.Info("OpenRA.EGL", $"ready {SurfaceWidth}x{SurfaceHeight} (unbound for game thread)");
					return true;
				}
				catch (Exception e)
				{
					return Fail("Exception: " + e);
				}
			}
		}

		public static bool Resize(ISurfaceHolder holder, int width, int height)
		{
			lock (Gate)
			{
				if (!initialized)
					return Initialize(holder, width, height);

				SurfaceWidth = Math.Max(1, width);
				SurfaceHeight = Math.Max(1, height);

				if (surface != null && surface != EGL14.EglNoSurface)
				{
					EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
					EGL14.EglDestroySurface(display, surface);
					surface = EGL14.EglNoSurface;
				}

				if (!CreateWindowSurfaceUnlocked(holder))
					return false;

				EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
				return true;
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

		/// <summary>Release context from the current thread (call on UI before game thread owns GL).</summary>
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
				return Fail("SurfaceHolder.Surface is null");

			int[] surfaceAttribs = { EGL14.EglNone };
			surface = EGL14.EglCreateWindowSurface(display, config, nativeWindow, surfaceAttribs, 0);
			if (surface == null || surface == EGL14.EglNoSurface)
				return Fail("eglCreateWindowSurface failed: " + EglError());
			return true;
		}

		static void DestroyUnlocked()
		{
			if (display != null && display == EGL14.EglNoDisplay)
				return;

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

		static bool Fail(string message)
		{
			LastError = message;
			AndroidPlatformLog.Error("OpenRA.EGL", message);
			DestroyUnlocked();
			return false;
		}

		static bool FailKeep(string message)
		{
			LastError = message;
			AndroidPlatformLog.Error("OpenRA.EGL", message);
			return false;
		}

		static string EglError() => $"0x{EGL14.EglGetError():X}";
	}
}
