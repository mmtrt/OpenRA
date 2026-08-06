// SurfaceView host — creates EGL ES 3.0 via OpenRA.Platforms.Android.AndroidEgl.

using System;
using Android.Content;
using Android.Views;
using OpenRA.Platforms.Android;
using AndroidFormat = Android.Graphics.Format;
using ALog = global::Android.Util.Log;

namespace OpenRA.Android
{
	public class GameSurfaceView : SurfaceView, ISurfaceHolderCallback
	{
		bool surfaceReady;
		public bool IsSurfaceReady => surfaceReady && AndroidEgl.IsReady;
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
			ALog.Info("OpenRA.Surface", "SurfaceCreated");
			var w = Width > 0 ? Width : 1280;
			var h = Height > 0 ? Height : 720;
			SurfaceWidth = w;
			SurfaceHeight = h;

			if (AndroidEgl.Initialize(holder, w, h))
			{
				surfaceReady = true;
				ALog.Info("OpenRA.Surface", $"EGL ready {w}x{h}");
			}
			else
			{
				surfaceReady = false;
				ALog.Error("OpenRA.Surface", "EGL init failed: " + AndroidEgl.LastError);
			}
		}

		public void SurfaceChanged(ISurfaceHolder holder, AndroidFormat format, int width, int height)
		{
			ALog.Info("OpenRA.Surface", $"SurfaceChanged {width}x{height}");
			SurfaceWidth = width;
			SurfaceHeight = height;
			if (!AndroidEgl.Resize(holder, width, height))
				ALog.Error("OpenRA.Surface", "EGL resize failed: " + AndroidEgl.LastError);
			else
				surfaceReady = AndroidEgl.IsReady;
		}

		public void SurfaceDestroyed(ISurfaceHolder holder)
		{
			ALog.Info("OpenRA.Surface", "SurfaceDestroyed");
			surfaceReady = false;
			AndroidEgl.Destroy();
		}

		public bool MakeCurrent() => AndroidEgl.MakeCurrent();
		public void Present() => AndroidEgl.SwapBuffers();
	}
}
