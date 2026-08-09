#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port — Rusted Warfare style touch input.
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Primitives;

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

		/// <summary>Snapshot mouse events for this frame (thread-safe).</summary>

		/// <summary>
		/// MotionEvent coords are surface pixels. OpenRA hit-testing uses EffectiveWindowSize
		/// (logical) space. Divide by EffectiveWindowScale so buttons match when UIScale ≠ 1.
		/// </summary>
		static void ToLogical(ref int x, ref int y)
		{
			var win = AndroidPlatformWindow.Current;
			if (win == null)
				return;
			var scale = win.EffectiveWindowScale;
			if (scale <= 1e-6f || Math.Abs(scale - 1f) < 1e-6f)
				return;
			x = (int)Math.Round(x / scale);
			y = (int)Math.Round(y / scale);
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
			var pt = new TouchPoint(id, x, y, timeMs);
			active.Add(pt);

			if (active.Count == 1)
				primary = pt;
			else if (active.Count == 2)
			{
				boxSelectActive = true;
				boxStart = new int2(active[0].X, active[0].Y);
				boxEnd = new int2(x, y);
				lastPinchDist = Dist(active[0], pt);
			}
		}

		public void OnTouchMove(int id, int x, int y)
		{
			ToLogical(ref x, ref y);
			var i = IndexOf(id);
			if (i < 0) return;

			var old = active[i];
			active[i] = new TouchPoint(id, x, y, old.DownMs);

			if (active.Count == 1 && primary.Id == id)
			{
				var dx = x - old.X;
				var dy = y - old.Y;
				if (Math.Abs(dx) > PanSlop || Math.Abs(dy) > PanSlop)
					lock (queueLock) panQueue.Enqueue(new int2(dx, dy));
			}
			else if (active.Count == 2)
			{
				boxEnd = new int2(active[1].X, active[1].Y);
				var d = Dist(active[0], active[1]);
				if (lastPinchDist > 1f)
				{
					var ratio = d / lastPinchDist;
					if (Math.Abs(ratio - 1f) > 0.02f)
						lock (queueLock) zoomQueue.Enqueue(ratio);
				}
				lastPinchDist = d;
			}
		}

		public void OnTouchUp(int id, int x, int y, long timeMs)
		{
			ToLogical(ref x, ref y);
			var i = IndexOf(id);
			if (i < 0) return;

			var pt = active[i];
			active.RemoveAt(i);

			if (active.Count == 0 && primary.Id == id)
			{
				var held = timeMs - pt.DownMs;
				var moved = Math.Abs(x - pt.X) + Math.Abs(y - pt.Y);
				var loc = new int2(x, y);

				if (held >= LongPressMs && moved <= TapSlop)
				{
					Enqueue(MouseInputEvent.Down, MouseButton.Right, loc, 1);
					Enqueue(MouseInputEvent.Up, MouseButton.Right, loc, 1);
				}
				else if (moved <= TapSlop)
				{
					var multi = 1;
					if (timeMs - lastTapMs <= DoubleTapMs &&
					    Math.Abs(x - lastTapPos.X) <= DoubleTapSlop &&
					    Math.Abs(y - lastTapPos.Y) <= DoubleTapSlop)
						multi = 2;

					var mods = multi > 1 ? Modifiers.Ctrl : Modifiers.None;
					Enqueue(MouseInputEvent.Down, MouseButton.Left, loc, multi, mods);
					Enqueue(MouseInputEvent.Up, MouseButton.Left, loc, multi, mods);
					lastTapMs = timeMs;
					lastTapPos = loc;
				}
			}
			else if (boxSelectActive && active.Count < 2)
			{
				// Two-finger box select: emit drag selection
				var a = boxStart;
				var b = boxEnd;
				Enqueue(MouseInputEvent.Down, MouseButton.Left, a, 1);
				Enqueue(MouseInputEvent.Move, MouseButton.Left, b, 1);
				Enqueue(MouseInputEvent.Up, MouseButton.Left, b, 1);
				boxSelectActive = false;
			}

			if (active.Count == 0)
			{
				primary = default;
				boxSelectActive = false;
			}
			else if (active.Count == 1)
				primary = active[0];
		}

		public void OnTouchCancel(int id)
		{
			var i = IndexOf(id);
			if (i >= 0) active.RemoveAt(i);
			if (active.Count == 0)
			{
				primary = default;
				boxSelectActive = false;
			}
		}

		void Enqueue(MouseInputEvent ev, MouseButton button, int2 loc, int multi, Modifiers mods = Modifiers.None)
		{
			lock (queueLock)
				mouseQueue.Enqueue(new MouseInput(ev, button, loc, int2.Zero, mods, multi));
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