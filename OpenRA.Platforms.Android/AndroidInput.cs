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
		long lastZoomSampleMs = -1; // only sample centroid once per MotionEvent

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
		/// <summary>Vertical travel (px) of 3-finger centroid per zoom step (logical px).</summary>
		const int ThreeFingerZoomStep = 18;
		/// <summary>Queue markers only — PlatformWindow maps these to ±120 wheel ticks.</summary>
		const float ThreeFingerZoomRatioIn = 1.25f;
		const float ThreeFingerZoomRatioOut = 0.8f;

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

		void ResetMultiFingerState()
		{
			twoFingerPanActive = false;
			twoFingerDidPan = false;
			twoFingerMaxMoved = 0;
			twoFingerMid = default;
			twoFingerStartMid = default;
			twoFingerStartMs = 0;
			DisarmThreeFingerZoom();
		}

		void DisarmThreeFingerZoom()
		{
			threeFingerZoomActive = false;
			threeFingerLastY = 0;
			threeFingerAccumDy = 0;
			lastZoomSampleMs = -1;
		}

		void ArmThreeFingerZoom()
		{
			threeFingerZoomActive = true;
			threeFingerLastY = active.Count > 0 ? CentroidY() : 0;
			threeFingerAccumDy = 0;
			// Do NOT stamp lastZoomSampleMs — first MOVE must be allowed to sample.
			lastZoomSampleMs = -1;
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
				threeFingerLastY = 0;
				lastZoomSampleMs = -1;
			}
			else if (active.Count >= 3)
			{
				// Third+ finger: end any two-finger pan, arm three-finger zoom.
				var scheme = CurrentScheme();
				if (twoFingerPanActive)
				{
					Enqueue(MouseInputEvent.Up, PanButton(scheme), twoFingerMid, 1);
					twoFingerPanActive = false;
				}
				twoFingerPanActive = false;
				twoFingerDidPan = true;
				ArmThreeFingerZoom();
			}
		}

		/// <summary>
		/// Update a pointer's logical position only (no gesture). Used when MainActivity
		/// refreshes all contacts in one MotionEvent before ProcessGestures.
		/// </summary>
		public void UpdatePointerPosition(int id, int x, int y, long timeMs)
		{
			ToLogical(ref x, ref y);
			lastPointerLogical = new int2(x, y);
			var i = IndexOf(id);
			if (i < 0)
			{
				// Simultaneous 3-finger: MOVE may arrive before POINTER_DOWN for every id.
				active.Add(new TouchPoint(id, x, y, timeMs));
				while (active.Count > 3)
					active.RemoveAt(0);
				return;
			}
			active[i] = new TouchPoint(id, x, y, active[i].DownMs);
		}

		/// <summary>Run gesture recognition once after all pointer positions for a frame are updated.</summary>
		public void ProcessGestures(long timeMs)
		{
			var scheme = CurrentScheme();
			ProcessGesturesCore(scheme, timeMs);
		}

		public void OnTouchMove(int id, int x, int y, long timeMs)
		{
			// Legacy single-call path: update then process (MainActivity multi-touch uses batch APIs).
			UpdatePointerPosition(id, x, y, timeMs);
			ProcessGestures(timeMs);
		}

		void ProcessGesturesCore(Scheme scheme, long timeMs)
		{
			if (active.Count == 1)
			{
				var pt = active[0];
				if (primary.Id != pt.Id)
					primary = pt;

				var x = pt.X;
				var y = pt.Y;
				var dx = x - primary.X;
				var dy = y - primary.Y;
				// primary stores DOWN position — use holdPanOrigin / Down coords for one-finger start
				var startX = primary.X;
				var startY = primary.Y;
				// TouchPoint primary is updated on move only through active[0]; keep down pos from DownMs point.
				// Re-read start from the stored primary at down — we overwrite active[0] on move so primary
				// fields X/Y are current. Use holdPanOrigin for start when in None/arming.
				var origin = holdPanOrigin;
				dx = x - origin.X;
				dy = y - origin.Y;
				var moved = Math.Abs(dx) + Math.Abs(dy);
				var held = timeMs - primary.DownMs;

				if (oneFingerMode == OneFingerMode.None)
				{
					if (moved > BoxSlop && held < HoldPanMs)
					{
						oneFingerMode = OneFingerMode.BoxSelect;
						Enqueue(MouseInputEvent.Down, PrimaryButton(), origin, 1);
						Enqueue(MouseInputEvent.Move, PrimaryButton(), new int2(x, y), 1);
					}
					else if (held >= HoldPanMs && moved <= TapSlop)
					{
						holdPanArmed = true;
					}
					else if (holdPanArmed && moved > PanSlop)
					{
						oneFingerMode = OneFingerMode.HoldPan;
						holdPanArmed = false;
						lastPanLogical = origin;
						Enqueue(MouseInputEvent.Down, PanButton(scheme), origin, 1);
						EnqueuePanMove(scheme, new int2(x, y), x - origin.X, y - origin.Y);
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
					EnqueuePanMove(scheme, mid, mdx, mdy);
					twoFingerMid = mid;
				}
			}
			else if (active.Count >= 3)
			{
				// Cancel two-finger pan so it cannot steal the gesture.
				if (twoFingerPanActive)
				{
					Enqueue(MouseInputEvent.Up, PanButton(scheme), twoFingerMid, 1);
					twoFingerPanActive = false;
				}
				twoFingerDidPan = true;

				if (!threeFingerZoomActive)
				{
					ArmThreeFingerZoom();
					// Baseline only this frame — apply dy on subsequent MOVE events.
					return;
				}

				// ProcessGestures is invoked once per MotionEvent; skip only true duplicates.
				if (timeMs != -1 && timeMs == lastZoomSampleMs)
					return;
				lastZoomSampleMs = timeMs;

				var cy = CentroidY();
				var dy = cy - threeFingerLastY;

				// Large jump = finger lifted/replaced mid-chord — re-baseline, do not zoom.
				if (Math.Abs(dy) > ThreeFingerZoomStep * 8)
				{
					threeFingerLastY = cy;
					threeFingerAccumDy = 0;
					return;
				}

				threeFingerLastY = cy;
				threeFingerAccumDy += dy;

				while (threeFingerAccumDy <= -ThreeFingerZoomStep)
				{
					lock (queueLock)
						zoomQueue.Enqueue(ThreeFingerZoomRatioIn);
					threeFingerAccumDy += ThreeFingerZoomStep;
				}
				while (threeFingerAccumDy >= ThreeFingerZoomStep)
				{
					lock (queueLock)
						zoomQueue.Enqueue(ThreeFingerZoomRatioOut);
					threeFingerAccumDy -= ThreeFingerZoomStep;
				}
			}
			else
			{
				// 0 fingers handled elsewhere; if we drop below 3 without Up (driver quirk), disarm.
				if (threeFingerZoomActive && active.Count < 3)
					DisarmThreeFingerZoom();
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

			// Leaving 3-finger zoom — full disarm so the next chord can arm cleanly
			if (active.Count < 3)
				DisarmThreeFingerZoom();

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
				DisarmThreeFingerZoom();
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
				DisarmThreeFingerZoom();
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
