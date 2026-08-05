#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of the unofficial OpenRA Android port effort.
 */
#endregion

using System;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	/// <summary>
	/// Android IPlatformWindow.
	/// Owns the Rusted-Warfare-style AndroidInput translator and will later
	/// own the EGL / Surface surface.
	/// </summary>
	sealed class AndroidPlatformWindow : IPlatformWindow
	{
		readonly Size windowSize;
		readonly float scaleModifier;
		readonly GLProfile glProfile;
		readonly AndroidInput input = new();

		bool suspended;

		// Optional callback used until real IInputHandler is wired
		public Action<MouseInput> OnSyntheticMouseInput;

		public AndroidPlatformWindow(Size size, WindowMode windowMode, float scaleModifier,
			int vertexBatchSize, int indexBatchSize, int videoDisplay, GLProfile profile)
		{
			this.windowSize = size;
			this.scaleModifier = scaleModifier;
			this.glProfile = profile;
		}

		/// <summary>Exposed so MainActivity can feed MotionEvents.</summary>
		public AndroidInput Input => input;

		readonly AndroidGraphicsContext graphicsContext = new();
		public IGraphicsContext Context => graphicsContext;

		public Size NativeWindowSize => windowSize;
		public Size EffectiveWindowSize => new Size(
			(int)(windowSize.Width / scaleModifier),
			(int)(windowSize.Height / scaleModifier));

		public float NativeWindowScale => 1.0f;
		public float EffectiveWindowScale => NativeWindowScale / scaleModifier;
		public Size SurfaceSize => NativeWindowSize;

		public int DisplayCount => 1;
		public int CurrentDisplay => 0;
		public bool HasInputFocus => !suspended;
		public bool IsSuspended => suspended;

		public event Action<float, float, float, float> OnWindowScaleChanged = (a, b, c, d) => { };

		public GLProfile GLProfile => glProfile;
		public GLProfile[] SupportedGLProfiles => new[] { GLProfile.Embedded };

		public void SetSuspended(bool value) => suspended = value;

		public void PumpInput(IInputHandler inputHandler)
		{
			foreach (var mi in input.PendingMouse)
			{
				if (inputHandler != null)
				{
					// Real path once OpenRA.Game types are referenced:
					// inputHandler.OnMouseInput(new OpenRA.MouseInput(...));
					// For now deliver via the synthetic callback so tests and
					// early integration can observe events.
				}
				OnSyntheticMouseInput?.Invoke(mi);
			}

			foreach (var z in input.PendingZoom)
			{
				var scroll = new MouseInput(
					MouseInputEvent.Scroll, MouseButton.None,
					int2.Zero, new int2(0, (int)(z * 120)), Modifiers.None, 0);
				OnSyntheticMouseInput?.Invoke(scroll);
			}

			// Pan deltas are available via input.PendingPan for a camera controller.
			// A future AndroidCameraController will consume them directly.

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

		public void Dispose() { }
	}
}
