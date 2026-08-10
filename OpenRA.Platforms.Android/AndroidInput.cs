#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port — scheme-gated touch input.
 *
 * Touch gestures are mapped to MouseInput according to Game.Settings.Game.MouseControlStyle:
 *   Classic      — left does select+command; long-press / two-finger = right pan
 *   Modern       — left select; long-press = right command/confirm
 *   OtherRTS     — left select + confirm; long-press = right command
 *   RustedWarfare— RW mobile model (tap select, long-press command, 1-finger box,
 *                  two-finger pan, pinch zoom, double-tap select-all)
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	/// <summary>
	/// Translates Android multi-touch into OpenRA MouseInput events.
	/// Behaviour is gated by the active MouseControlStyle so Classic / Modern /
	/// OtherRTS / RustedWarfare each match their in-game description panel.
	/// </summary>
	public sealed class AndroidInput
	{
		readonly List<TouchPoint> active = new();
		TouchPoint primary;
		float lastPinchDist;
		bool boxSelectActive;
		bool singleDragging;
		bool twoFingerPanActive;
		int2 twoFingerMid;
		float twoFingerDist;

		long lastTapMs;
		int2 lastTapPos;

		const int DoubleTapMs = 300;
		const int DoubleTapSlop = 42;
		const int LongPressMs = 450;
		const int TapSlop = 22;
		const int PanSlop = 3;

		// UI thread enqueues (MainActivity.OnTouch); game thread drains (PumpInput).
		readonly object queueLock = new();
		readonly Queue<MouseInput> mouseQueue = new();
		readonly Queue<float> zoomQueue = new();
		readonly Queue<int2> panQueue = new();
		readonly Queue<(int2 loc, int2 delta)> scrollQueue = new();
		int2 lastPointerLogical;
		public int2 LastPointerLogical => lastPointerLogical;

		// View-space size from MainActivity (MotionEvent is relative to the View).
		public static int ViewWidth;
		public static int ViewHeight;

		static void ToLogical(ref int x, ref int y)
		{
			var win = AndroidPlatformWindow.Current;
			if (win == null)
				return;

			var surf = win.SurfaceSize;
			if (ViewWidth > 0 && ViewHeight > 0 && surf.Width > 0 && surf.Height > 0
			    && (ViewWidth != surf.Width || ViewHeight != surf.Height))
			{
				x = (int)Math.Round(x * (double)surf.Width / ViewWidth);
				y = (int)Math.Round(y * (double)surf.Height / ViewHeight);
			}

			var scale = win.EffectiveWindowScale;
			if (scale > 1e-6f && Math.Abs(scale - 1f) >= 1e-6f)
			{
				x = (int)Math.Round(x / scale);
				y = (int)Math.Round(y / scale);
			}

			var eff = win.EffectiveWindowSize;
			if (x < 0) x = 0;
			if (y < 0) y = 0;
			if (x >= eff.Width) x = eff.Width - 1;
			if (y >= eff.Height) y = eff.Height - 1;
		}

		enum Scheme { Classic, Modern, OtherRTS, RustedWarfare }

		static Scheme CurrentScheme()
		{
			try
			{
				var s = Game.Settings?.Game?.MouseControlStyle;
				if (s == null)
					return Scheme.Modern;
				return s.ToString() switch
				{
					"Classic" => Scheme.Classic,
					"OtherRTS" => Scheme.OtherRTS,
					"RustedWarfare" => Scheme.RustedWarfare,
					_ => Scheme.Modern
				};
			}
			catch
			{
				return Scheme.Modern;
			}
		}

		/// <summary>Select / place / support / confirm-left actions → always Left.</summary>
		static MouseButton PrimaryButton() => MouseButton.Left;

		/// <summary>
		/// Contextual command button per scheme (mirrors GameSettings.ResolveActionButton):
		/// Classic = Left (same as primary); Modern / OtherRTS / RW = Right.
		/// </summary>
		static MouseButton CommandButton(Scheme scheme)
			=> scheme == Scheme.Classic ? MouseButton.Left : MouseButton.Right;

		/// <summary>
		/// Camera pan button: Classic defaults to Right; others use Middle (standard
		/// middle-drag scroll). Alternate-scroll users can still remap in settings.
		/// </summary>
		static MouseButton PanButton(Scheme scheme)
			=> scheme == Scheme.Classic ? MouseButton.Right : MouseButton.Middle;

		/// <summary>
		/// Long-press issues a command click only for schemes where command ≠ primary.
		/// Classic commands with left (already covered by tap) — long-press is unused
		/// for orders there (two-finger / right-pan covers scroll instead).
		/// </summary>
		static bool LongPressIsCommand(Scheme scheme)
			=> scheme is Scheme.Modern or Scheme.OtherRTS or Scheme.RustedWarfare;

		/// <summary>
		/// Double-tap → select-all-of-type is the RW mobile convention and is useful
		/// on all schemes; keep it everywhere.
		/// </summary>
		static bool AllowDoubleTapSelectAll(Scheme scheme) => true;

		/// <summary>
		/// One-finger drag → box select is standard for every scheme (left-button drag).
		/// </summary>
		static bool OneFingerBoxSelect(Scheme scheme) => true;

		/// <summary>
		/// Pinch zoom is available on all schemes (maps to scroll-wheel zoom).
		/// </summary>
		static bool AllowPinchZoom(Scheme scheme) => true;

		public MouseInput[] DrainMouse()
		{
			lock (queueLock)
			{
				if (mouseQueue.Count == 0)
					return Array.Empty<MouseInput>();
				var a = mouseQueue.ToArray();
				mouseQueue.Clear();
				return a;
			}
		}

		public float[] DrainZoom()
		{
			lock (queueLock)
			{
				if (zoomQueue.Count == 0)
					return Array.Empty<float>();
				var a = zoomQueue.ToArray();
				zoomQueue.Clear();
				return a;
			}
		}

		public int2[] DrainPan()
		{
			lock (queueLock)
			{
				if (panQueue.Count == 0)
					return Array.Empty<int2>();
				var a = panQueue.ToArray();
				panQueue.Clear();
				return a;
			}
		}

		public (int2 loc, int2 delta)[] DrainScroll()
		{
			lock (queueLock)
			{
				if (scrollQueue.Count == 0)
					return Array.Empty<(int2, int2)>();
				var a = scrollQueue.ToArray();
				scrollQueue.Clear();
				return a;
			}
		}

		public IReadOnlyCollection<MouseInput> PendingMouse
		{
			get { lock (queueLock) return mouseQueue.ToArray(); }
		}

		public IReadOnlyCollection<float> PendingZoom
		{
			get { lock (queueLock) return zoomQueue.ToArray(); }
		}

		public void OnTouchDown(int id, int x, int y, long timeMs)
		{
			ToLogical(ref x, ref y);
			lastPointerLogical = new int2(x, y);
			active.Add(new TouchPoint(id, x, y, timeMs));

			if (active.Count == 1)
			{
				primary = active[0];
				boxSelectActive = false;
				singleDragging = false;
			}
			else if (active.Count == 2)
			{
				// Second finger cancels one-finger box and starts two-finger pan + pinch.
				if (singleDragging)
				{
					Enqueue(MouseInputEvent.Up, PrimaryButton(), lastPointerLogical, 1);
					singleDragging = false;
				}

				boxSelectActive = false;
				var scheme = CurrentScheme();
				twoFingerPanActive = true;
				twoFingerMid = Mid(active[0], active[1]);
				twoFingerDist = Dist(active[0], active[1]);
				lastPinchDist = twoFingerDist;
				Enqueue(MouseInputEvent.Down, PanButton(scheme), twoFingerMid, 1);
			}
		}

		public void OnTouchMove(int id, int x, int y, long timeMs)
		{
			ToLogical(ref x, ref y);
			lastPointerLogical = new int2(x, y);
			var i = IndexOf(id);
			if (i < 0)
				return;

			active[i] = new TouchPoint(id, x, y, active[i].DownMs);

			var scheme = CurrentScheme();

			if (active.Count == 1 && primary.Id == id)
			{
				var dx = x - primary.X;
				var dy = y - primary.Y;
				var moved = Math.Abs(dx) + Math.Abs(dy);

				if (!singleDragging && moved > TapSlop && OneFingerBoxSelect(scheme))
				{
					// One-finger drag past slop → left-button drag (box select / order drag).
					singleDragging = true;
					boxSelectActive = true;
					Enqueue(MouseInputEvent.Down, PrimaryButton(), new int2(primary.X, primary.Y), 1);
				}

				if (singleDragging)
					Enqueue(MouseInputEvent.Move, PrimaryButton(), new int2(x, y), 1);
			}
			else if (active.Count >= 2 && twoFingerPanActive)
			{
				var mid = Mid(active[0], active[1]);
				var dist = Dist(active[0], active[1]);

				// Pinch zoom (all schemes — equivalent to scroll wheel)
				if (AllowPinchZoom(scheme) && lastPinchDist > 1f)
				{
					var ratio = dist / lastPinchDist;
					if (Math.Abs(ratio - 1f) > 0.02f)
					{
						lock (queueLock) zoomQueue.Enqueue(ratio);
						lastPinchDist = dist;
					}
				}
				else
				{
					lastPinchDist = dist;
				}

				// Two-finger pan → scheme pan button drag
				var mdx = mid.X - twoFingerMid.X;
				var mdy = mid.Y - twoFingerMid.Y;
				if (Math.Abs(mdx) + Math.Abs(mdy) >= PanSlop)
				{
					lock (queueLock)
					{
						mouseQueue.Enqueue(new MouseInput(
							MouseInputEvent.Move, PanButton(scheme), mid, new int2(mdx, mdy), Modifiers.None, 0));
					}
					twoFingerMid = mid;

					// Vertical component also drives UI menu scroll (wheel equivalent)
					if (Math.Abs(mdy) >= PanSlop)
					{
						var scrollDy = Math.Clamp(mdy, -48, 48);
						lock (queueLock)
							scrollQueue.Enqueue((mid, new int2(0, scrollDy)));
					}
				}

				twoFingerDist = dist;
			}
		}

		public void OnTouchUp(int id, int x, int y, long timeMs)
		{
			ToLogical(ref x, ref y);
			lastPointerLogical = new int2(x, y);
			var i = IndexOf(id);
			if (i < 0)
				return;

			var pt = active[i];
			active.RemoveAt(i);

			var scheme = CurrentScheme();

			if (twoFingerPanActive && active.Count < 2)
			{
				Enqueue(MouseInputEvent.Up, PanButton(scheme), twoFingerMid, 1);
				twoFingerPanActive = false;
				lastPinchDist = 0;
			}

			if (active.Count == 0)
			{
				if (singleDragging)
				{
					Enqueue(MouseInputEvent.Up, PrimaryButton(), new int2(x, y), 1);
				}
				else
				{
					var held = timeMs - pt.DownMs;
					var moved = Math.Abs(x - pt.X) + Math.Abs(y - pt.Y);
					var loc = new int2(x, y);

					if (held >= LongPressMs && moved <= TapSlop && LongPressIsCommand(scheme))
					{
						// Modern / OtherRTS / RustedWarfare only: long-press = command button
						var cmd = CommandButton(scheme);
						Enqueue(MouseInputEvent.Down, cmd, loc, 1);
						Enqueue(MouseInputEvent.Up, cmd, loc, 1);
					}
					else if (moved <= TapSlop)
					{
						var multi = 1;
						if (AllowDoubleTapSelectAll(scheme) &&
						    timeMs - lastTapMs <= DoubleTapMs &&
						    Math.Abs(x - lastTapPos.X) <= DoubleTapSlop &&
						    Math.Abs(y - lastTapPos.Y) <= DoubleTapSlop)
							multi = 2;

						// Double-tap → Ctrl+Left (select all of type) — RW convention, useful everywhere
						var mods = multi > 1 ? Modifiers.Ctrl : Modifiers.None;
						Enqueue(MouseInputEvent.Down, PrimaryButton(), loc, multi, mods);
						Enqueue(MouseInputEvent.Up, PrimaryButton(), loc, multi, mods);
						lastTapMs = timeMs;
						lastTapPos = loc;
					}
				}

				primary = default;
				boxSelectActive = false;
				singleDragging = false;
			}
			else if (active.Count == 1)
			{
				primary = active[0];
			}
		}

		public void OnTouchCancel(int id)
		{
			var i = IndexOf(id);
			if (i >= 0) active.RemoveAt(i);
			if (active.Count == 0)
			{
				var scheme = CurrentScheme();
				if (twoFingerPanActive)
				{
					Enqueue(MouseInputEvent.Up, PanButton(scheme), twoFingerMid, 1);
					twoFingerPanActive = false;
				}
				if (singleDragging)
				{
					Enqueue(MouseInputEvent.Up, PrimaryButton(), lastPointerLogical, 1);
					singleDragging = false;
				}
				primary = default;
				boxSelectActive = false;
			}
		}

		static int2 Mid(TouchPoint a, TouchPoint b)
			=> new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

		void Enqueue(MouseInputEvent ev, MouseButton button, int2 loc, int multi, Modifiers mods = Modifiers.None)
		{
			lock (queueLock)
			{
				// Move before Down so CursorPosition matches the finger (placement/orders).
				if (ev == MouseInputEvent.Down)
					mouseQueue.Enqueue(new MouseInput(MouseInputEvent.Move, MouseButton.None, loc, int2.Zero, Modifiers.None, 0));
				mouseQueue.Enqueue(new MouseInput(ev, button, loc, int2.Zero, mods, multi));
			}
		}

		int IndexOf(int id)
		{
			for (var i = 0; i < active.Count; i++)
				if (active[i].Id == id) return i;
			return -1;
		}

		static float Dist(TouchPoint a, TouchPoint b)
		{
			var dx = a.X - b.X;
			var dy = a.Y - b.Y;
			return (float)Math.Sqrt(dx * dx + dy * dy);
		}

		readonly struct TouchPoint
		{
			public readonly int Id, X, Y;
			public readonly long DownMs;
			public TouchPoint(int id, int x, int y, long downMs)
			{
				Id = id; X = x; Y = y; DownMs = downMs;
			}
		}
	}
}
