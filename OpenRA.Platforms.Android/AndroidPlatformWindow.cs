#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port.
 */
#endregion

using System;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	/// <summary>
	/// Android IPlatformWindow — owns RW-style AndroidInput and GLES context.
	/// Window size always tracks the real EGL surface (not desktop defaults).
	/// </summary>
	public sealed class AndroidPlatformWindow : IPlatformWindow
	{
		public static AndroidPlatformWindow Current { get; private set; }

		Size windowSize;
		readonly float scaleModifier;
		readonly GLProfile glProfile;
		readonly AndroidInput input = new();
		readonly AndroidGraphicsContext graphicsContext = new();
		bool suspended;

		public AndroidPlatformWindow(Size size, WindowMode windowMode, float scaleModifier,
			int vertexBatchSize, int indexBatchSize, int videoDisplay, GLProfile profile)
		{
			// Prefer live SurfaceView size so the GL viewport matches the phone display.
			if (AndroidEgl.IsReady && AndroidEgl.SurfaceWidth > 0 && AndroidEgl.SurfaceHeight > 0)
				windowSize = new Size(AndroidEgl.SurfaceWidth, AndroidEgl.SurfaceHeight);
			else if (size.Width > 0 && size.Height > 0)
				windowSize = size;
			else
				windowSize = new Size(1280, 720);

			this.scaleModifier = scaleModifier <= 0 ? 1f : scaleModifier;
			glProfile = profile;
			Current = this;
		}

		public AndroidInput Input => input;
		public IGraphicsContext Context => graphicsContext;

		public Size NativeWindowSize => windowSize;
		public Size EffectiveWindowSize => new(
			Math.Max(1, (int)(windowSize.Width / scaleModifier)),
			Math.Max(1, (int)(windowSize.Height / scaleModifier)));

		public float NativeWindowScale => 1.0f;
		public float EffectiveWindowScale => NativeWindowScale / scaleModifier;
		public Size SurfaceSize => NativeWindowSize;

		public int DisplayCount => 1;
		public int CurrentDisplay => 0;
		public bool HasInputFocus => !suspended;
		public bool IsSuspended => suspended;

		public event Action<float, float, float, float> OnWindowScaleChanged = (a, b, c, d) => { };

		public GLProfile GLProfile => glProfile;
		public GLProfile[] SupportedGLProfiles { get; } = { GLProfile.Embedded };

		public void SetSuspended(bool value) => suspended = value;

		/// <summary>Update size after SurfaceChanged (keeps Renderer viewport in sync).</summary>
		public void SyncSurfaceSize()
		{
			if (!AndroidEgl.IsReady)
				return;
			var w = AndroidEgl.SurfaceWidth;
			var h = AndroidEgl.SurfaceHeight;
			if (w <= 0 || h <= 0)
				return;
			if (windowSize.Width == w && windowSize.Height == h)
				return;
			var old = windowSize;
			windowSize = new Size(w, h);
			try
			{
				OnWindowScaleChanged?.Invoke(
					NativeWindowScale, NativeWindowScale,
					old.Width / (float)Math.Max(1, old.Height),
					w / (float)Math.Max(1, h));
			}
			catch { /* ignore */ }
		}

		public void PumpInput(IInputHandler inputHandler)
		{
			SyncSurfaceSize();

			if (inputHandler != null)
			{
				foreach (var mi in input.PendingMouse)
					inputHandler.OnMouseInput(mi);

				foreach (var z in input.PendingZoom)
				{
					inputHandler.OnMouseInput(new MouseInput(
						MouseInputEvent.Scroll, MouseButton.None,
						int2.Zero, new int2(0, (int)((z - 1f) * 120)), Modifiers.None, 0));
				}
			}

			input.ClearFrame();
		}

		public string GetClipboardText() => string.Empty;
		public bool SetClipboardText(string text) => false;
		public bool TryOpenUrl(string url) => false;

		public void GrabWindowMouseFocus() { }
		public void ReleaseWindowMouseFocus() { }

		public IHardwareCursor CreateHardwareCursor(string name, Size size, byte[] data, int2 hotspot, bool pixelDouble)
			=> null;

		public void SetHardwareCursor(IHardwareCursor cursor) { }
		public void SetWindowTitle(string title) { }
		public void SetRelativeMouseMode(bool mode) { }
		public void SetScaleModifier(float scale) { }

		public void Dispose()
		{
			if (ReferenceEquals(Current, this))
				Current = null;
		}
	}
}
