#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port — scheme-gated touch input.
 *
 * Gesture model (VanillaRA-inspired, avoids pinch vs two-finger-pan fights):
 *   Tap              → primary click
 *   Double-tap       → select-all-of-type (Ctrl+Left)
 *   Quick drag       → left box-select (starts after BoxSlop, before hold delay)
 *   Hold then drag   → camera pan (hold ≥ HoldPanMs without moving, then drag)
 *   Long-press lift  → right/command click (scheme-dependent; no drag)
 *   Two-finger drag  → camera pan
 *   Two-finger tap   → right-click / cancel
 *   Three-finger swipe up/down → zoom in/out (pinch disabled)
 *
 * Schemes:
 *   Classic / Touch — left select+command; long-press right cancel; pan = Right
 *   Modern                  — left select; long-press right command/confirm; pan = Middle
 *   OtherRTS                — left select+confirm; long-press right command; pan = Middle
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	/// <summary>
	/// Translates Android multi-touch into OpenRA MouseInput events.
	/// One-finger: quick-drag = box select; hold-then-drag = pan (VanillaRA style).
	/// Two-finger: pan only (no pinch) so zoom does not steal scroll.
	/// </summary>
	public sealed class AndroidInput
	{
		readonly List<TouchPoint> active = new();
		TouchPoint primary;

		// One-finger mode after we commit
		enum OneFingerMode { None, BoxSelect, HoldPan }
		OneFingerMode oneFingerMode;
		bool holdPanArmed; // held long enough, waiting for move to start pan drag
		int2 holdPanOrigin;
		int2 lastPanLogical;

		bool twoFingerPanActive;
		int2 twoFingerMid;
		int2 twoFingerStartMid;
		long twoFingerStartMs;
		int twoFingerMaxMoved; // max L1 travel of midpoint while 2 fingers down
		bool twoFingerDidPan;

		// Three-finger zoom (vertical swipe)
		bool threeFingerZoomActive;
		int threeFingerLastY;
		int threeFingerAccumDy;

		long lastTapMs;
		int2 lastTapPos;

		const int DoubleTapMs = 300;
		const int DoubleTapSlop = 42;
		/// <summary>Stationary hold → arm pan (VanillaRA-style). Shorter than LongPressMs.</summary>
		const int HoldPanMs = 280;
		/// <summary>Stationary hold + lift → right/command click.</summary>
		const int LongPressMs = 450;
		const int TapSlop = 22;
		/// <summary>Movement before HoldPanMs commits box-select instead of pan.</summary>
		const int BoxSlop = 28;
		const int PanSlop = 3;
		/// <summary>Max midpoint travel for a two-finger gesture to count as a tap (right-click).</summary>
		const int TwoFingerTapSlop = 36;
		const int TwoFingerTapMaxMs = 400;
		/// <summary>Vertical travel (px) of 3-finger centroid per zoom step.</summary>
		const int ThreeFingerZoomStep = 28;
		const float ThreeFingerZoomRatioIn = 1.15f;
		const float ThreeFingerZoomRatioOut = 1f / 1.15f;

		readonly object queueLock = new();
		// Reused drain buffers (PumpInput only foreach's within the same call).
		MouseInput[] mouseDrainBuf = Array.Empty<MouseInput>();
		float[] zoomDrainBuf = Array.Empty<float>();
		int2[] panDrainBuf = Array.Empty<int2>();
		(int2 loc, int2 delta)[] scrollDrainBuf = Array.Empty<(int2, int2)>();
		KeyInput[] keyDrainBuf = Array.Empty<KeyInput>();
		readonly Queue<MouseInput> mouseQueue = new();
		readonly Queue<float> zoomQueue = new();
		readonly Queue<int2> panQueue = new();
		readonly Queue<(int2 loc, int2 delta)> scrollQueue = new();
		readonly Queue<KeyInput> keyQueue = new();
		readonly Queue<string> textQueue = new();
		int2 lastPointerLogical;
		public int2 LastPointerLogical => lastPointerLogical;

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

		enum Scheme { Classic, Modern, OtherRTS, Touch }

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
					"Touch" => Scheme.Touch,
					_ => Scheme.Modern
				};
			}
			catch
			{
				return Scheme.Modern;
			}
		}

		static MouseButton PrimaryButton() => MouseButton.Left;

		static MouseButton CommandButton(Scheme scheme)
			=> scheme is Scheme.Classic or Scheme.Touch ? MouseButton.Left : MouseButton.Right;

		static MouseButton PanButton(Scheme scheme)
			=> scheme is Scheme.Classic or Scheme.Touch ? MouseButton.Right : MouseButton.Middle;

		static bool LongPressIsCommand(Scheme scheme)
			=> scheme is Scheme.Modern or Scheme.OtherRTS;

		static bool LongPressIsRightClick(Scheme scheme)
			=> scheme is Scheme.Classic or Scheme.Touch;

		public MouseInput[] DrainMouse()
		{
			lock (queueLock)
			{
				var n = mouseQueue.Count;
				if (n == 0)
					return Array.Empty<MouseInput>();
				// Exact-length array required (foreach uses .Length). Reuse when capacity matches.
				if (mouseDrainBuf.Length != n)
					mouseDrainBuf = new MouseInput[n];
				mouseQueue.CopyTo(mouseDrainBuf, 0);
				mouseQueue.Clear();
				return mouseDrainBuf;
			}
		}

		public float[] DrainZoom()
		{
			lock (queueLock)
			{
				var n = zoomQueue.Count;
				if (n == 0)
					return Array.Empty<float>();
				// Exact-length array required (foreach uses .Length). Reuse when capacity matches.
				if (zoomDrainBuf.Length != n)
					zoomDrainBuf = new float[n];
				zoomQueue.CopyTo(zoomDrainBuf, 0);
				zoomQueue.Clear();
				return zoomDrainBuf;
			}
		}

		public int2[] DrainPan()
		{
			lock (queueLock)
			{
				var n = panQueue.Count;
				if (n == 0)
					return Array.Empty<int2>();
				// Exact-length array required (foreach uses .Length). Reuse when capacity matches.
				if (panDrainBuf.Length != n)
					panDrainBuf = new int2[n];
				panQueue.CopyTo(panDrainBuf, 0);
				panQueue.Clear();
				return panDrainBuf;
			}
		}

		public (int2 loc, int2 delta)[] DrainScroll()
		{
			lock (queueLock)
			{
				var n = scrollQueue.Count;
				if (n == 0)
					return Array.Empty<(int2, int2)>();
				if (scrollDrainBuf.Length != n)
					scrollDrainBuf = new (int2 loc, int2 delta)[n];
				scrollQueue.CopyTo(scrollDrainBuf, 0);
				scrollQueue.Clear();
				return scrollDrainBuf;
			}
		}

		/// <summary>Drop pending events when no input handler is attached this frame.</summary>
		public void ClearFrame()
		{
			lock (queueLock)
			{
				mouseQueue.Clear();
				zoomQueue.Clear();
				panQueue.Clear();
				scrollQueue.Clear();
				keyQueue.Clear();
				textQueue.Clear();
			}
		}

		public KeyInput[] DrainKey()
		{
			lock (queueLock)
			{
				var n = keyQueue.Count;
				if (n == 0)
					return Array.Empty<KeyInput>();
				// Exact-length array required (foreach uses .Length). Reuse when capacity matches.
				if (keyDrainBuf.Length != n)
					keyDrainBuf = new KeyInput[n];
				keyQueue.CopyTo(keyDrainBuf, 0);
				keyQueue.Clear();
				return keyDrainBuf;
			}
		}

		public string[] DrainText()
		{
			lock (queueLock)
			{
				if (textQueue.Count == 0)
					return Array.Empty<string>();
				var a = textQueue.ToArray();
				textQueue.Clear();
				return a;
			}
		}

		/// <summary>Inject a key event from Android soft/hardware keyboard.</summary>
		public void EnqueueKey(KeyInput ki)
		{
			lock (queueLock)
				keyQueue.Enqueue(ki);
		}

		/// <summary>Type a Unicode string into the focused text field (Down+Up per char).</summary>
		public void EnqueueText(string text)
		{
			// OpenRA TextFieldWidget inserts characters via HandleTextInput / OnTextInput,
			// NOT via KeyInput.UnicodeChar (that path only handles BACKSPACE etc.).
			if (string.IsNullOrEmpty(text))
				return;
			lock (queueLock)
				textQueue.Enqueue(text);
		}

		public void EnqueueSpecialKey(Keycode key, KeyInputEvent ev)
		{
			lock (queueLock)
			{
				keyQueue.Enqueue(new KeyInput
				{
					Event = ev,
					Key = key,
					Modifiers = Modifiers.None,
					UnicodeChar = (char)0
				});
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

		void ResetOneFinger()
		{
			oneFingerMode = OneFingerMode.None;
			holdPanArmed = false;
		}

		public void OnTouchDown(int id, int x, int y, long timeMs)
		{
			ToLogical(ref x, ref y);
			lastPointerLogical = new int2(x, y);

			// Android can re-deliver the same pointer id; replace instead of stacking ghosts.
			var existing = IndexOf(id);
			if (existing >= 0)
				active.RemoveAt(existing);
			active.Add(new TouchPoint(id, x, y, timeMs));

			// Never track more than 3 contacts — drop oldest ghosts if driver glitches.
			while (active.Count > 3)
				active.RemoveAt(0);

			if (active.Count == 1)
			{
				primary = active[0];
				ResetOneFinger();
				holdPanOrigin = new int2(x, y);
				ResetMultiFingerState();
			}
			else if (active.Count == 2)
			{
				// Second finger: cancel one-finger modes. Do not pan yet — wait for
				// move (pan) vs lift (two-finger tap = right-click / cancel).
				var scheme = CurrentScheme();
				if (oneFingerMode == OneFingerMode.BoxSelect)
					Enqueue(MouseInputEvent.Up, PrimaryButton(), lastPointerLogical, 1);
				else if (oneFingerMode == OneFingerMode.HoldPan)
					Enqueue(MouseInputEvent.Up, PanButton(scheme), lastPanLogical, 1);
				ResetOneFinger();

				twoFingerPanActive = false;
				twoFingerDidPan = false;
				twoFingerMaxMoved = 0;
				twoFingerMid = Mid(active[0], active[1]);
				twoFingerStartMid = twoFingerMid;
				twoFingerStartMs = timeMs;
				threeFingerZoomActive = false;
				threeFingerAccumDy = 0;
			}
			else if (active.Count >= 3)
			{
				// Third finger: end any two-finger pan, start three-finger zoom tracking.
				var scheme = CurrentScheme();
				if (twoFingerPanActive)
				{
					Enqueue(MouseInputEvent.Up, PanButton(scheme), twoFingerMid, 1);
					twoFingerPanActive = false;
				}
				twoFingerDidPan = true; // suppress two-finger tap if third finger joined
				threeFingerZoomActive = true;
				threeFingerLastY = CentroidY();
				threeFingerAccumDy = 0;
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
				var held = timeMs - primary.DownMs;

				if (oneFingerMode == OneFingerMode.None)
				{
					// Quick drag past BoxSlop before hold delay → box select
					if (moved > BoxSlop && held < HoldPanMs)
					{
						oneFingerMode = OneFingerMode.BoxSelect;
						Enqueue(MouseInputEvent.Down, PrimaryButton(), new int2(primary.X, primary.Y), 1);
						Enqueue(MouseInputEvent.Move, PrimaryButton(), new int2(x, y), 1);
					}
					// Stationary long enough → arm hold-pan (VanillaRA)
					else if (held >= HoldPanMs && moved <= TapSlop)
					{
						holdPanArmed = true;
						holdPanOrigin = new int2(x, y);
					}
					// Armed and started moving → commit pan
					else if (holdPanArmed && moved > PanSlop)
					{
						oneFingerMode = OneFingerMode.HoldPan;
						holdPanArmed = false;
						lastPanLogical = holdPanOrigin;
						Enqueue(MouseInputEvent.Down, PanButton(scheme), holdPanOrigin, 1);
						var mdx = x - holdPanOrigin.X;
						var mdy = y - holdPanOrigin.Y;
						EnqueuePanMove(scheme, new int2(x, y), mdx, mdy);
						lastPanLogical = new int2(x, y);
					}
				}
				else if (oneFingerMode == OneFingerMode.BoxSelect)
				{
					Enqueue(MouseInputEvent.Move, PrimaryButton(), new int2(x, y), 1);
				}
				else if (oneFingerMode == OneFingerMode.HoldPan)
				{
					var mdx = x - lastPanLogical.X;
					var mdy = y - lastPanLogical.Y;
					if (Math.Abs(mdx) + Math.Abs(mdy) >= PanSlop)
					{
						EnqueuePanMove(scheme, new int2(x, y), mdx, mdy);
						lastPanLogical = new int2(x, y);
					}
				}
			}
			else if (active.Count == 2)
			{
				var mid = Mid(active[0], active[1]);
				var travel = Math.Abs(mid.X - twoFingerStartMid.X) + Math.Abs(mid.Y - twoFingerStartMid.Y);
				if (travel > twoFingerMaxMoved)
					twoFingerMaxMoved = travel;

				var mdx = mid.X - twoFingerMid.X;
				var mdy = mid.Y - twoFingerMid.Y;
				var step = Math.Abs(mdx) + Math.Abs(mdy);

				// Commit pan once midpoint moves past tap slop
				if (!twoFingerPanActive && twoFingerMaxMoved > TwoFingerTapSlop)
				{
					twoFingerPanActive = true;
					twoFingerDidPan = true;
					Enqueue(MouseInputEvent.Down, PanButton(scheme), twoFingerStartMid, 1);
					twoFingerMid = twoFingerStartMid;
					mdx = mid.X - twoFingerMid.X;
					mdy = mid.Y - twoFingerMid.Y;
				}

				if (twoFingerPanActive && step >= PanSlop)
				{
					// Pan only — do NOT emit Scroll here (OpenRA treats vertical scroll as zoom).
					EnqueuePanMove(scheme, mid, mdx, mdy);
					twoFingerMid = mid;
				}
			}
			else if (active.Count >= 3 && threeFingerZoomActive)
			{
				// Three-finger vertical swipe: up → zoom in, down → zoom out
				var cy = CentroidY();
				var dy = cy - threeFingerLastY; // +dy = fingers moved down on screen
				threeFingerLastY = cy;
				threeFingerAccumDy += dy;

				while (threeFingerAccumDy <= -ThreeFingerZoomStep)
				{
					// Swipe up → zoom in
					lock (queueLock) zoomQueue.Enqueue(ThreeFingerZoomRatioIn);
					threeFingerAccumDy += ThreeFingerZoomStep;
				}
				while (threeFingerAccumDy >= ThreeFingerZoomStep)
				{
					// Swipe down → zoom out
					lock (queueLock) zoomQueue.Enqueue(ThreeFingerZoomRatioOut);
					threeFingerAccumDy -= ThreeFingerZoomStep;
				}
			}
		}

		void EnqueuePanMove(Scheme scheme, int2 loc, int mdx, int mdy)
		{
			lock (queueLock)
			{
				mouseQueue.Enqueue(new MouseInput(
					MouseInputEvent.Move, PanButton(scheme), loc, new int2(mdx, mdy), Modifiers.None, 0));
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

			// Leaving 3-finger zoom
			if (active.Count < 3)
				threeFingerZoomActive = false;

			// Leaving two-finger: either end pan or fire two-finger tap = right-click/cancel
			if (active.Count < 2)
			{
				if (twoFingerPanActive)
				{
					Enqueue(MouseInputEvent.Up, PanButton(scheme), twoFingerMid, 1);
					twoFingerPanActive = false;
				}
				else if (!twoFingerDidPan
				         && twoFingerMaxMoved <= TwoFingerTapSlop
				         && (timeMs - twoFingerStartMs) <= TwoFingerTapMaxMs)
				{
					// Two-finger tap → right-click / cancel at midpoint
					var loc = twoFingerStartMid;
					Enqueue(MouseInputEvent.Down, MouseButton.Right, loc, 1);
					Enqueue(MouseInputEvent.Up, MouseButton.Right, loc, 1);
				}
				twoFingerDidPan = false;
				twoFingerMaxMoved = 0;
			}

			if (active.Count == 0)
			{
				// Only emit one-finger click if this lift was never part of multi-touch.
				// (twoFingerDidPan / three-finger path already consumed the gesture.)
				var wasMulti = twoFingerDidPan || twoFingerPanActive;
				ResetMultiFingerState();

				if (oneFingerMode == OneFingerMode.BoxSelect)
				{
					Enqueue(MouseInputEvent.Up, PrimaryButton(), new int2(x, y), 1);
				}
				else if (oneFingerMode == OneFingerMode.HoldPan)
				{
					Enqueue(MouseInputEvent.Up, PanButton(scheme), lastPanLogical, 1);
				}
				else if (!wasMulti)
				{
					// No committed drag mode — tap or long-press click
					var held = timeMs - pt.DownMs;
					var moved = Math.Abs(x - pt.X) + Math.Abs(y - pt.Y);
					var loc = new int2(x, y);

					if (held >= LongPressMs && moved <= TapSlop)
					{
						if (LongPressIsCommand(scheme))
						{
							var cmd = CommandButton(scheme);
							Enqueue(MouseInputEvent.Down, cmd, loc, 1);
							Enqueue(MouseInputEvent.Up, cmd, loc, 1);
						}
						else if (LongPressIsRightClick(scheme))
						{
							Enqueue(MouseInputEvent.Down, MouseButton.Right, loc, 1);
							Enqueue(MouseInputEvent.Up, MouseButton.Right, loc, 1);
						}
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
				ResetOneFinger();
			}
			else if (active.Count == 1)
			{
				// Remaining finger after multi-touch: re-base as a fresh one-finger contact
				// so leftover DownMs / modes do not keep us stuck in pan/zoom paths.
				var rem = active[0];
				active[0] = new TouchPoint(rem.Id, rem.X, rem.Y, timeMs);
				primary = active[0];
				ResetOneFinger();
				ResetMultiFingerState();
				holdPanOrigin = new int2(rem.X, rem.Y);
			}
			else if (active.Count == 2)
			{
				// Dropped out of three-finger zoom into two-finger — restart pan baseline.
				threeFingerZoomActive = false;
				threeFingerAccumDy = 0;
				twoFingerPanActive = false;
				twoFingerDidPan = true; // do not treat remaining as a two-finger tap
				twoFingerMaxMoved = 0;
				twoFingerMid = Mid(active[0], active[1]);
				twoFingerStartMid = twoFingerMid;
				twoFingerStartMs = timeMs;
			}
		}

		public void OnTouchCancel(int id)
		{
			// id < 0 means cancel entire gesture (MotionEvent.ACTION_CANCEL).
			if (id < 0)
			{
				ForceEndAllGestures();
				return;
			}

			var i = IndexOf(id);
			if (i >= 0) active.RemoveAt(i);
			if (active.Count == 0)
				ForceEndAllGestures();
			else if (active.Count == 1)
			{
				var rem = active[0];
				active[0] = new TouchPoint(rem.Id, rem.X, rem.Y, rem.DownMs);
				primary = active[0];
				ResetOneFinger();
				ResetMultiFingerState();
			}
			else if (active.Count == 2)
			{
				threeFingerZoomActive = false;
				threeFingerAccumDy = 0;
				twoFingerPanActive = false;
				twoFingerDidPan = true;
				twoFingerMid = Mid(active[0], active[1]);
				twoFingerStartMid = twoFingerMid;
			}
		}

		void ForceEndAllGestures()
		{
			var scheme = CurrentScheme();
			if (twoFingerPanActive)
			{
				Enqueue(MouseInputEvent.Up, PanButton(scheme), twoFingerMid, 1);
				twoFingerPanActive = false;
			}
			if (oneFingerMode == OneFingerMode.BoxSelect)
				Enqueue(MouseInputEvent.Up, PrimaryButton(), lastPointerLogical, 1);
			else if (oneFingerMode == OneFingerMode.HoldPan)
				Enqueue(MouseInputEvent.Up, PanButton(scheme), lastPanLogical, 1);

			active.Clear();
			primary = default;
			ResetOneFinger();
			ResetMultiFingerState();
		}

		int CentroidY()
		{
			if (active.Count == 0) return 0;
			var sum = 0;
			for (var i = 0; i < active.Count; i++)
				sum += active[i].Y;
			return sum / active.Count;
		}

		static int2 Mid(TouchPoint a, TouchPoint b)
			=> new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

		void Enqueue(MouseInputEvent ev, MouseButton button, int2 loc, int multi, Modifiers mods = Modifiers.None)
		{
			lock (queueLock)
			{
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
