// SurfaceView that will host the OpenGL ES 3.0 context.
// arm64-v8a only prototype.

using System;
using Android.Content;
using Android.Util;
using Android.Views;
using Android.Runtime;

namespace OpenRA.Android
{
	public class GameSurfaceView : SurfaceView, ISurfaceHolderCallback
	{
		// EGL handles (filled in once native EGL bindings are available)
		IntPtr eglDisplay = IntPtr.Zero;
		IntPtr eglContext = IntPtr.Zero;
		IntPtr eglSurface = IntPtr.Zero;
		bool surfaceReady;

		public bool IsSurfaceReady => surfaceReady;

		public GameSurfaceView(Context context) : base(context)
		{
			Holder.AddCallback(this);
			Focusable = true;
			FocusableInTouchMode = true;
			KeepScreenOn = true;
		}

		public GameSurfaceView(Context context, IAttributeSet attrs) : base(context, attrs)
		{
			Holder.AddCallback(this);
			Focusable = true;
			FocusableInTouchMode = true;
			KeepScreenOn = true;
		}

		public void SurfaceCreated(ISurfaceHolder holder)
		{
			Log.Info("OpenRA.Surface", "SurfaceCreated");
			// TODO Phase 3 — real EGL init:
			// 1. eglGetDisplay(EGL_DEFAULT_DISPLAY)
			// 2. eglInitialize
			// 3. Choose config with EGL_OPENGL_ES3_BIT + 8-bit RGB + depth
			// 4. eglCreateContext with client version 3
			// 5. eglCreateWindowSurface from holder.Surface
			// 6. eglMakeCurrent
			// For now only mark the surface as present so higher layers can proceed.
			surfaceReady = true;
			Log.Info("OpenRA.Surface", "Surface marked ready (EGL stub)");
		}

		public void SurfaceChanged(ISurfaceHolder holder, Android.Graphics.Format format, int width, int height)
		{
			Log.Info("OpenRA.Surface", $"SurfaceChanged {width}x{height}");
			// TODO: notify AndroidPlatformWindow / Renderer of new size
			// and re-create EGL surface if required by the driver.
		}

		public void SurfaceDestroyed(ISurfaceHolder holder)
		{
			Log.Info("OpenRA.Surface", "SurfaceDestroyed");
			surfaceReady = false;
			// TODO: eglMakeCurrent(NO_SURFACE), eglDestroySurface, eglDestroyContext
			eglSurface = IntPtr.Zero;
			eglContext = IntPtr.Zero;
			eglDisplay = IntPtr.Zero;
		}

		/// <summary>
		/// Called each frame once the engine is running.
		/// Will perform eglSwapBuffers when EGL is live.
		/// </summary>
		public void Present()
		{
			if (!surfaceReady || eglDisplay == IntPtr.Zero)
				return;
			// TODO: eglSwapBuffers(eglDisplay, eglSurface);
		}
	}
}
