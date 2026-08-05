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
	/// Translates Android multi-touch into OpenRA mouse-style events
	/// using the Rusted Warfare interaction model:
	///   single tap, double-tap (select all of type), long-press (right-click),
	///   two-finger box select, one-finger pan, pinch zoom.
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

		readonly Queue<MouseInput> mouseQueue = new();
		readonly Queue<float> zoomQueue = new();
		readonly Queue<int2> panQueue = new();

		public IReadOnlyCollection<MouseInput> PendingMouse => mouseQueue;
		public IReadOnlyCollection<float> PendingZoom => zoomQueue;
		public IReadOnlyCollection<int2> PendingPan => panQueue;

		public void ClearFrame()
		{
			mouseQueue.Clear();
			zoomQueue.Clear();
			panQueue.Clear();
		}

		public void OnTouchDown(int id, int x, int y, long timeMs)
		{
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
			var i = IndexOf(id);
			if (i < 0) return;

			var old = active[i];
			active[i] = new TouchPoint(id, x, y, old.DownMs);

			if (active.Count == 1 && primary.Id == id)
			{
				var dx = x - old.X;
				var dy = y - old.Y;
				if (Math.Abs(dx) > PanSlop || Math.Abs(dy) > PanSlop)
					panQueue.Enqueue(new int2(dx, dy));
			}
			else if (active.Count >= 2)
			{
				boxEnd = new int2(x, y);
				var d = Dist(active[0], active[1]);
				var delta = d - lastPinchDist;
				if (Math.Abs(delta) > 5f)
				{
					zoomQueue.Enqueue(delta * 0.004f);
					lastPinchDist = d;
					boxSelectActive = false;
				}
			}
		}

		public void OnTouchUp(int id, int x, int y, long timeMs)
		{
			var i = IndexOf(id);
			if (i < 0) return;

			var touch = active[i];
			active.RemoveAt(i);

			if (active.Count == 0)
			{
				var held = timeMs - touch.DownMs;
				var moved = Math.Abs(x - touch.X) + Math.Abs(y - touch.Y);

				if (held >= LongPressMs && moved < TapSlop + 8)
				{
					Enqueue(MouseInputEvent.Down, MouseButton.Right, x, y, 0);
					Enqueue(MouseInputEvent.Up, MouseButton.Right, x, y, 0);
				}
				else if (moved < TapSlop)
				{
					var isDouble = (timeMs - lastTapMs) < DoubleTapMs
						&& Math.Abs(x - lastTapPos.X) < DoubleTapSlop
						&& Math.Abs(y - lastTapPos.Y) < DoubleTapSlop;

					var multi = isDouble ? 2 : 1;
					var mods = isDouble ? Modifiers.Ctrl : Modifiers.None;

					Enqueue(MouseInputEvent.Down, MouseButton.Left, x, y, multi, mods);
					Enqueue(MouseInputEvent.Up, MouseButton.Left, x, y, multi, mods);

					lastTapMs = timeMs;
					lastTapPos = new int2(x, y);
				}

				boxSelectActive = false;
			}
			else if (boxSelectActive && active.Count == 1)
			{
				Enqueue(MouseInputEvent.Down, MouseButton.Left, boxStart.X, boxStart.Y, 0);
				Enqueue(MouseInputEvent.Move, MouseButton.Left, boxEnd.X, boxEnd.Y, 0);
				Enqueue(MouseInputEvent.Up, MouseButton.Left, boxEnd.X, boxEnd.Y, 0);
				boxSelectActive = false;
			}
		}

		public void OnTouchCancel(int id)
		{
			var i = IndexOf(id);
			if (i >= 0) active.RemoveAt(i);
			if (active.Count == 0) boxSelectActive = false;
		}

		void Enqueue(MouseInputEvent ev, MouseButton btn, int x, int y, int multiTap, Modifiers mods = Modifiers.None)
		{
			mouseQueue.Enqueue(new MouseInput(ev, btn, new int2(x, y), int2.Zero, mods, multiTap));
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
			public TouchPoint(int id, int x, int y, long downMs) { Id = id; X = x; Y = y; DownMs = downMs; }
		}
	}

	// Local mirrors of upstream types so this file compiles standalone.
	// Replaced by OpenRA.Game types once ProjectReferences are live.
	public enum MouseInputEvent { Down, Move, Up, Scroll }
	[Flags] public enum MouseButton { None = 0, Left = 1, Right = 2, Middle = 4 }
	[Flags] public enum Modifiers { None = 0, Shift = 1, Alt = 2, Ctrl = 4, Meta = 8 }

	public readonly struct MouseInput
	{
		public readonly MouseInputEvent Event;
		public readonly MouseButton Button;
		public readonly int2 Location;
		public readonly int2 Delta;
		public readonly Modifiers Modifiers;
		public readonly int MultiTapCount;

		public MouseInput(MouseInputEvent ev, MouseButton button, int2 location, int2 delta, Modifiers modifiers, int multiTapCount)
		{
			Event = ev; Button = button; Location = location; Delta = delta;
			Modifiers = modifiers; MultiTapCount = multiTapCount;
		}
	}
}
