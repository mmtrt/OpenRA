// SurfaceView host — creates EGL ES 3.0 via OpenRA.Platforms.Android.AndroidEgl.

using System;
using Android.Content;
using Android.Views;
using OpenRA.Platforms.Android;
using AndroidFormat = Android.Graphics.Format;

namespace OpenRA.Android
{
	public class GameSurfaceView : SurfaceView, ISurfaceHolderCallback
	{
		bool surfaceReady;
		public bool IsSurfaceReady => surfaceReady && AndroidEgl.IsReady;
		public event Action SurfaceReady;
		public int SurfaceWidth { get; private set; }
		public int SurfaceHeight { get; private set; }

		public GameSurfaceView(Context context) : base(context) => InitHolder();
		public GameSurfaceView(Context context, global::Android.Util.IAttributeSet attrs) : base(context, attrs) => InitHolder();

		void InitHolder()
		{
			Holder.AddCallback(this);
			Holder.SetFormat(AndroidFormat.Rgba8888);
			Focusable = true;
			FocusableInTouchMode = true;
			KeepScreenOn = true;
		}

		public void SurfaceCreated(ISurfaceHolder holder)
		{
			AndroidFileLog.Info("OpenRA.Surface", "SurfaceCreated");
			var w = Width > 0 ? Width : Math.Max(1, SurfaceWidth > 0 ? SurfaceWidth : 1280);
			var h = Height > 0 ? Height : Math.Max(1, SurfaceHeight > 0 ? SurfaceHeight : 720);
			SurfaceWidth = w;
			SurfaceHeight = h;

			bool ok;
			if (AndroidEgl.IsReady)
			{
				ok = true;
			}
			else if (AndroidEgl.HasDisplayContext)
			{
				// Soft recovery after SurfaceDestroyed — context still valid.
				ok = AndroidEgl.RecreateSurface(holder, w, h);
			}
			else
			{
				ok = AndroidEgl.Initialize(holder, w, h);
			}

			surfaceReady = ok && AndroidEgl.IsReady;
			if (surfaceReady)
			{
				AndroidFileLog.Info("OpenRA.Surface", "EGL ready " + w + "x" + h);
				try { AndroidPlatformWindow.Current?.SetSuspended(false); } catch { /* ignore */ }
				SurfaceReady?.Invoke();
			}
			else
			{
				AndroidFileLog.Error("OpenRA.Surface", "EGL init failed: " + AndroidEgl.LastError);
			}
		}

		public void SurfaceChanged(ISurfaceHolder holder, AndroidFormat format, int width, int height)
		{
			AndroidFileLog.Info("OpenRA.Surface", "SurfaceChanged " + width + "x" + height);
			SurfaceWidth = width;
			SurfaceHeight = height;

			if (!AndroidEgl.Resize(holder, width, height))
				AndroidFileLog.Error("OpenRA.Surface", "EGL resize failed: " + AndroidEgl.LastError);
			else
			{
				surfaceReady = AndroidEgl.IsReady;
				try { AndroidPlatformWindow.Current?.SyncSurfaceSize(); } catch { /* ignore */ }
			}
		}

		public void SurfaceDestroyed(ISurfaceHolder holder)
		{
			AndroidFileLog.Info("OpenRA.Surface", "SurfaceDestroyed");
			surfaceReady = false;
			try { AndroidPlatformWindow.Current?.SetSuspended(true); } catch { /* ignore */ }
			// Drop window surface only — keep GL context for fast resume.
			AndroidEgl.DestroySurfaceOnly();
		}

		public bool MakeCurrent() => AndroidEgl.MakeCurrent();
		public void Present() => AndroidEgl.SwapBuffers();
	}
}
