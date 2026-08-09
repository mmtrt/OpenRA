#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port — Rusted Warfare style touch input.
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Primitives;
// Game.Settings for MouseControlStyle

namespace OpenRA.Platforms.Android
{
	/// <summary>
	/// Translates Android multi-touch into OpenRA MouseInput events
	/// (Rusted Warfare model: tap, double-tap, long-press, box select, pan, pinch).
	/// Uses OpenRA.Game MouseInput / MouseButton / Modifiers types.
	/// </summary>
	public sealed class AndroidInput
	{
		readonly List<TouchPoint> active = new();
		TouchPoint primary;
		float lastPinchDist;
		bool boxSelectActive;
		int2 boxStart, boxEnd;
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
		// Must not enumerate Queue while the other thread mutates it.
		readonly object queueLock = new();
		readonly Queue<MouseInput> mouseQueue = new();
		readonly Queue<float> zoomQueue = new();
		readonly Queue<int2> panQueue = new();
		readonly Queue<(int2 loc, int2 delta)> scrollQueue = new();
		int2 lastPointerLogical;
		public int2 LastPointerLogical => lastPointerLogical;

		/// <summary>Snapshot mouse events for this frame (thread-safe).</summary>

		/// <summary>
		/// MotionEvent coords are surface pixels. OpenRA hit-testing uses EffectiveWindowSize
		/// (logical) space. Divide by EffectiveWindowScale so buttons match when UIScale ≠ 1.
		/// </summary>
		// View-space size from MainActivity (MotionEvent is relative to the View).
		// May differ from EGL surface size if the SurfaceView is letterboxed.
		public static int ViewWidth;
		public static int ViewHeight;

		static void ToLogical(ref int x, ref int y)
		{
			var win = AndroidPlatformWindow.Current;
			if (win == null)
				return;

			// 1) Map View pixels → surface/native pixels when the view is not 1:1 with EGL
			var surf = win.SurfaceSize;
			if (ViewWidth > 0 && ViewHeight > 0 && surf.Width > 0 && surf.Height > 0
			    && (ViewWidth != surf.Width || ViewHeight != surf.Height))
			{
				x = (int)Math.Round(x * (double)surf.Width / ViewWidth);
				y = (int)Math.Round(y * (double)surf.Height / ViewHeight);
			}

			// 2) Surface/native → EffectiveWindowSize (UIScale / scaleModifier)
			var scale = win.EffectiveWindowScale;
			if (scale > 1e-6f && Math.Abs(scale - 1f) >= 1e-6f)
			{
				x = (int)Math.Round(x / scale);
				y = (int)Math.Round(y / scale);
			}

			// 3) Clamp to logical window so hit-tests never miss off-by-one at edges
			var eff = win.EffectiveWindowSize;
			if (x < 0) x = 0;
			if (y < 0) y = 0;
			if (x >= eff.Width) x = eff.Width - 1;
			if (y >= eff.Height) y = eff.Height - 1;
		}

		/// <summary>
		/// Classic / Modern / OtherRTS / RustedWarfare (if enum present on fork).
		/// </summary>
		static string CurrentMouseStyleName()
		{
			try
			{
				var s = Game.Settings?.Game?.MouseControlStyle;
				return s == null ? "Modern" : s.ToString();
			}
			catch
			{
				return "Modern";
			}
		}

		/// <summary>
		/// Map a logical "primary" or "secondary" action to OpenRA mouse buttons
		/// for the active control scheme (mirrors InputSettings.ResolveActionButton).
		/// </summary>
		static MouseButton PrimaryButton()
		{
			// Select / default left-click actions
			return MouseButton.Left;
		}

		static MouseButton CommandButton()
		{
			// Classic: command with left; Modern / OtherRTS / RustedWarfare: right
			var name = CurrentMouseStyleName();
			if (name == "Classic")
				return MouseButton.Left;
			return MouseButton.Right;
		}



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

		/// <summary>Snapshot pinch zoom ratios for this frame (thread-safe).</summary>
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

		/// <summary>UI menu wheel-equivalent: (location, delta) pairs.</summary>
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

		// Legacy accessors — do not enumerate across threads; prefer Drain*.
		public IReadOnlyCollection<MouseInput> PendingMouse
		{
			get { lock (queueLock) return mouseQueue.ToArray(); }
		}

		public IReadOnlyCollection<float> PendingZoom
		{
			get { lock (queueLock) return zoomQueue.ToArray(); }
		}

		public IReadOnlyCollection<int2> PendingPan
		{
			get { lock (queueLock) return panQueue.ToArray(); }
		}

		public void ClearFrame()
		{
			// Must take the same lock as Enqueue/Drain* — Queue<T> isn't thread-safe, and a
			// lock only protects a resource if every access path uses it. This was the one
			// remaining unlocked touch of these queues; left as-is it reintroduces the exact
			// race the Drain*/queueLock changes elsewhere in this file were fixing.
			lock (queueLock)
			{
				mouseQueue.Clear();
				zoomQueue.Clear();
				panQueue.Clear();
			}
		}


		public void OnTouchDown(int id, int x, int y, long timeMs)
		{
			ToLogical(ref x, ref y);
			lastPointerLogical = new int2(x, y);
			var pt = new TouchPoint(id, x, y, timeMs);
			active.Add(pt);

			if (active.Count == 1)
			{
				primary = pt;
				singleDragging = false;
			}
			else if (active.Count == 2)
			{
				// GeneralsZH / RW: second finger cancels one-finger box and starts
				// two-finger pan + pinch-zoom (not box select on second finger).
				if (singleDragging)
				{
					Enqueue(MouseInputEvent.Up, PrimaryButton(), lastPointerLogical, 1);
					singleDragging = false;
				}

				boxSelectActive = false;
				twoFingerPanActive = true;
				twoFingerMid = Mid(active[0], active[1]);
				twoFingerDist = Dist(active[0], active[1]);
				lastPinchDist = twoFingerDist;

				// Middle-button pan (OpenRA camera drag) — Down at midpoint
				Enqueue(MouseInputEvent.Down, MouseButton.Middle, twoFingerMid, 1);
			}
		}

		public void OnTouchMove(int id, int x, int y)
		{
			ToLogical(ref x, ref y);
			var i = IndexOf(id);
			if (i < 0) return;

			var old = active[i];
			active[i] = new TouchPoint(id, x, y, old.DownMs);
			lastPointerLogical = new int2(x, y);

			if (active.Count == 1 && primary.Id == id)
			{
				var dx = x - old.X;
				var dy = y - old.Y;
				var moved = Math.Abs(x - primary.X) + Math.Abs(y - primary.Y);

				// One-finger drag past slop → left-button drag (box select / order drag).
				// NEVER emit Scroll here — Scroll is map-zoom in WorldInteractionController.
				if (moved > TapSlop)
				{
					if (!singleDragging)
					{
						singleDragging = true;
						Enqueue(MouseInputEvent.Down, PrimaryButton(), new int2(primary.X, primary.Y), 1);
					}

					if (Math.Abs(dx) > 0 || Math.Abs(dy) > 0)
						Enqueue(MouseInputEvent.Move, PrimaryButton(), new int2(x, y), 1);
				}
			}
			else if (active.Count == 2)
			{
				var a = active[0];
				var b = active[1];
				var mid = Mid(a, b);
				var dist = Dist(a, b);

				// Pinch zoom only when distance changes enough (GeneralsZH pinch/anchor zoom)
				if (lastPinchDist > 1f)
				{
					var ratio = dist / lastPinchDist;
					if (Math.Abs(ratio - 1f) > 0.03f)
						lock (queueLock) zoomQueue.Enqueue(ratio);
				}
				lastPinchDist = dist;

				// Two-finger pan: midpoint translation → middle-button drag (camera)
				var mdx = mid.X - twoFingerMid.X;
				var mdy = mid.Y - twoFingerMid.Y;
				if (twoFingerPanActive && (Math.Abs(mdx) > 0 || Math.Abs(mdy) > 0))
				{
					// Delta is movement; Location is current midpoint
					lock (queueLock)
						mouseQueue.Enqueue(new MouseInput(
							MouseInputEvent.Move, MouseButton.Middle,
							mid, new int2(mdx, mdy), Modifiers.None, 0));
				}

				// Stable-distance two-finger vertical drag → UI scroll (menus only useful
				// when ScrollPanel is under the pointer — world ignores small scroll if
				// zoom threshold not met; still better than one-finger zoom).
				if (Math.Abs(dist - twoFingerDist) < 12f && Math.Abs(mdy) > Math.Abs(mdx) && Math.Abs(mdy) > PanSlop)
				{
					var scale = 1f;
					try
					{
						var win = AndroidPlatformWindow.Current;
						if (win != null)
							scale = Math.Max(1f, win.EffectiveWindowScale);
					}
					catch { /* ignore */ }
					var scrollDy = (int)Math.Round(mdy / (10f * scale));
					if (scrollDy == 0)
						scrollDy = mdy > 0 ? 1 : -1;
					if (scrollDy > 12) scrollDy = 12;
					if (scrollDy < -12) scrollDy = -12;
					lock (queueLock)
						scrollQueue.Enqueue((mid, new int2(0, scrollDy)));
				}

				twoFingerMid = mid;
				twoFingerDist = dist;
			}
		}

		public void OnTouchUp(int id, int x, int y, long timeMs)
		{
			ToLogical(ref x, ref y);
			var i = IndexOf(id);
			if (i < 0) return;

			var pt = active[i];
			active.RemoveAt(i);

			if (active.Count == 1 && twoFingerPanActive)
			{
				// Lifted one of two fingers — end middle pan
				Enqueue(MouseInputEvent.Up, MouseButton.Middle, twoFingerMid, 1);
				twoFingerPanActive = false;
				primary = active[0];
				return;
			}

			if (active.Count == 0)
			{
				if (twoFingerPanActive)
				{
					Enqueue(MouseInputEvent.Up, MouseButton.Middle, twoFingerMid, 1);
					twoFingerPanActive = false;
				}
				else if (singleDragging)
				{
					Enqueue(MouseInputEvent.Up, PrimaryButton(), new int2(x, y), 1);
					singleDragging = false;
				}
				else if (primary.Id == id)
				{
					var held = timeMs - pt.DownMs;
					var moved = Math.Abs(x - pt.X) + Math.Abs(y - pt.Y);
					var loc = new int2(x, y);

					if (held >= LongPressMs && moved <= TapSlop)
					{
						// Long-press = command (Right in Modern / OtherRTS / RustedWarfare)
						var cmd = CommandButton();
						Enqueue(MouseInputEvent.Down, cmd, loc, 1);
						Enqueue(MouseInputEvent.Up, cmd, loc, 1);
					}
					else if (moved <= TapSlop)
					{
						var multi = 1;
						if (timeMs - lastTapMs <= DoubleTapMs &&
						    Math.Abs(x - lastTapPos.X) <= DoubleTapSlop &&
						    Math.Abs(y - lastTapPos.Y) <= DoubleTapSlop)
							multi = 2;

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
				if (twoFingerPanActive)
				{
					Enqueue(MouseInputEvent.Up, MouseButton.Middle, twoFingerMid, 1);
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
				// Do not Move before Up — that can re-enter hover/tooltip logic every release
				// and feels like an input "loop" when combined with production UI.
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