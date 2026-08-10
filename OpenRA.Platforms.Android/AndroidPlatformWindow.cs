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
		float scaleModifier;
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
			AndroidPlatformLog.Info("OpenRA.GL.View",
				"Window ctor native=" + windowSize.Width + "x" + windowSize.Height
				+ " scaleModifier=" + this.scaleModifier
				+ " effective=" + EffectiveWindowSize.Width + "x" + EffectiveWindowSize.Height
				+ " profile=" + profile);
		}

		public AndroidInput Input => input;
		public IGraphicsContext Context => graphicsContext;

		public Size NativeWindowSize => windowSize;

		// Match Sdl2PlatformWindow: logical size shrinks as UIScale (scaleModifier) grows
		// so widgets are larger relative to the screen.
		public Size EffectiveWindowSize => new(
			Math.Max(1, (int)(windowSize.Width / scaleModifier)),
			Math.Max(1, (int)(windowSize.Height / scaleModifier)));

		// DPI scale (Android surface pixels == "native" points for our EGL path).
		public float NativeWindowScale => 1.0f;

		// Desktop: EffectiveWindowScale = windowScale * scaleModifier
		// (was wrongly NativeWindowScale / scaleModifier → content stuck in a corner when UIScale≠1)
		public float EffectiveWindowScale => NativeWindowScale * scaleModifier;

		// GL drawable size in pixels (full panel).
		public Size SurfaceSize => NativeWindowSize;

		public int DisplayCount => 1;
		public int CurrentDisplay => 0;
		public bool HasInputFocus => !suspended;
		public bool IsSuspended =>
			suspended || !AndroidEgl.IsReady; // pause render loop when window surface is gone

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

			// Drain under lock — UI thread may Enqueue during this frame.
			// (Was: foreach PendingMouse → Collection was modified; enumeration…)
			if (inputHandler != null)
			{
				foreach (var mi in input.DrainMouse())
					inputHandler.OnMouseInput(mi);

				foreach (var z in input.DrainZoom())
				{
					// Pinch zoom → wheel at last known pointer (menus need a location over the panel)
					var loc = input.LastPointerLogical;
					inputHandler.OnMouseInput(new MouseInput(
						MouseInputEvent.Scroll, MouseButton.None,
						loc, new int2(0, (int)((z - 1f) * 120)), Modifiers.None, 0));
				}

				foreach (var (loc, delta) in input.DrainScroll())
				{
					inputHandler.OnMouseInput(new MouseInput(
						MouseInputEvent.Scroll, MouseButton.None,
						loc, delta, Modifiers.None, 0));
				}

				foreach (var pan in input.DrainPan())
				{
					inputHandler.OnMouseInput(new MouseInput(
						MouseInputEvent.Move, MouseButton.None,
						pan, int2.Zero, Modifiers.None, 0));
				}

				foreach (var ki in input.DrainKey())
				{
					try { inputHandler.OnKeyInput(ki); }
					catch { /* ignore */ }
				}

				// Soft keyboard for TextFieldWidget — launcher registers the callback.
				try
				{
					var focus = OpenRA.Widgets.Ui.KeyboardFocusWidget;
					var needKb = focus != null && IsTextEntryName(focus.GetType().Name);
					AndroidKeyboardBridge.SetWanted?.Invoke(needKb);
				}
				catch { /* Ui / keyboard optional during bootstrap */ }
			}
			else
			{
				input.ClearFrame();
			}
		}

		static bool IsTextEntryName(string typeName)
		{
			if (string.IsNullOrEmpty(typeName))
				return false;
			// Strict match — do not open IME for ViewportController / random widgets
			return typeName == "TextFieldWidget"
				|| typeName == "PasswordFieldWidget"
				|| typeName == "TextInputWidget"
				|| typeName.EndsWith("TextFieldWidget", System.StringComparison.Ordinal)
				|| typeName.EndsWith("PasswordFieldWidget", System.StringComparison.Ordinal);
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
		public void SetScaleModifier(float scale)
		{
			if (scale <= 0f)
				scale = 1f;
			if (Math.Abs(scaleModifier - scale) < 1e-6f)
				return;
			var oldMod = scaleModifier;
			scaleModifier = scale;
			var native = NativeWindowScale;
			try
			{
				// (oldNative, oldEffective, newNative, newEffective) — match desktop event
				OnWindowScaleChanged?.Invoke(
					native, native * oldMod,
					native, native * scaleModifier);
			}
			catch { /* ignore */ }

			AndroidPlatformLog.Info("OpenRA.GL.View",
				"SetScaleModifier " + oldMod + " → " + scaleModifier
				+ " effective=" + EffectiveWindowSize.Width + "x" + EffectiveWindowSize.Height
				+ " surface=" + SurfaceSize.Width + "x" + SurfaceSize.Height
				+ " effScale=" + EffectiveWindowScale);
		}

		public void Dispose()
		{
			if (ReferenceEquals(Current, this))
				Current = null;
		}
	}
}
