// Desktop unit-test style harness for AndroidInput (Touch gestures).
// Can be compiled and run with plain `dotnet run` once a tiny test project is added,
// or copied into an xUnit / NUnit project that references OpenRA.Platforms.Android.
//
// These tests do not require an Android device or emulator.

using System;
using System.Linq;
using OpenRA.Platforms.Android;
using OpenRA.Primitives;

public static class AndroidInputGestureTests
{
	static int passed, failed;

	public static void Main()
	{
		TestSingleTap();
		TestDoubleTap();
		TestLongPress();
		TestTwoFingerBoxSelect();
		TestPan();
		TestPinchZoom();

		Console.WriteLine();
		Console.WriteLine($"Results: {passed} passed, {failed} failed");
		Environment.Exit(failed > 0 ? 1 : 0);
	}

	static void TestSingleTap()
	{
		var input = new AndroidInput();
		input.OnTouchDown(0, 100, 200, 1000);
		input.OnTouchUp(0, 102, 201, 1080); // short hold, tiny move

		var events = input.PendingMouse.ToArray();
		Assert(events.Length == 2, "SingleTap: expected Down+Up");
		Assert(events[0].Event == MouseInputEvent.Down && events[0].Button == MouseButton.Left, "SingleTap: Down Left");
		Assert(events[1].Event == MouseInputEvent.Up && events[1].Button == MouseButton.Left, "SingleTap: Up Left");
		Assert(events[0].MultiTapCount == 1, "SingleTap: MultiTapCount == 1");
		Assert(events[0].Modifiers == Modifiers.None, "SingleTap: no modifiers");
		Pass("SingleTap");
	}

	static void TestDoubleTap()
	{
		var input = new AndroidInput();
		// First tap
		input.OnTouchDown(0, 50, 50, 1000);
		input.OnTouchUp(0, 51, 50, 1050);
		input.ClearFrame();

		// Second tap quickly nearby
		input.OnTouchDown(0, 55, 52, 1200);
		input.OnTouchUp(0, 56, 53, 1250);

		var events = input.PendingMouse.ToArray();
		Assert(events.Length == 2, "DoubleTap: expected Down+Up");
		Assert(events[0].MultiTapCount == 2, "DoubleTap: MultiTapCount == 2");
		Assert(events[0].Modifiers == Modifiers.Ctrl, "DoubleTap: Ctrl modifier for select-all-type");
		Pass("DoubleTap");
	}

	static void TestLongPress()
	{
		var input = new AndroidInput();
		input.OnTouchDown(0, 300, 400, 2000);
		input.OnTouchUp(0, 301, 401, 2500); // 500 ms hold

		var events = input.PendingMouse.ToArray();
		Assert(events.Length == 2, "LongPress: expected Down+Up");
		Assert(events[0].Button == MouseButton.Right, "LongPress: Right button");
		Assert(events[1].Button == MouseButton.Right, "LongPress: Right button up");
		Pass("LongPress");
	}

	static void TestTwoFingerBoxSelect()
	{
		var input = new AndroidInput();
		input.OnTouchDown(0, 10, 10, 3000);
		input.OnTouchDown(1, 10, 10, 3010); // second finger
		input.OnTouchMove(1, 200, 150);     // drag to form box
		input.OnTouchUp(1, 200, 150, 3200); // release second finger → box complete

		var events = input.PendingMouse.ToArray();
		Assert(events.Length >= 3, "BoxSelect: expected Down/Move/Up sequence");
		Assert(events[0].Event == MouseInputEvent.Down, "BoxSelect: starts with Down");
		Assert(events.Any(e => e.Event == MouseInputEvent.Move), "BoxSelect: contains Move");
		Assert(events.Last().Event == MouseInputEvent.Up, "BoxSelect: ends with Up");
		Pass("TwoFingerBoxSelect");
	}

	static void TestPan()
	{
		var input = new AndroidInput();
		input.OnTouchDown(0, 100, 100, 4000);
		input.OnTouchMove(0, 130, 100); // horizontal drag
		input.OnTouchMove(0, 160, 100);

		var pans = input.PendingPan.ToArray();
		Assert(pans.Length >= 1, "Pan: expected at least one pan delta");
		Assert(pans.Sum(p => p.X) > 0, "Pan: positive X movement");
		Pass("Pan");
	}

	static void TestPinchZoom()
	{
		var input = new AndroidInput();
		input.OnTouchDown(0, 100, 100, 5000);
		input.OnTouchDown(1, 120, 100, 5010);
		// Move fingers apart
		input.OnTouchMove(0, 80, 100);
		input.OnTouchMove(1, 160, 100);

		var zooms = input.PendingZoom.ToArray();
		Assert(zooms.Length >= 1, "PinchZoom: expected zoom deltas");
		Pass("PinchZoom");
	}

	static void Assert(bool condition, string message)
	{
		if (!condition)
		{
			Console.WriteLine("FAIL: " + message);
			failed++;
		}
	}

	static void Pass(string name)
	{
		Console.WriteLine("PASS: " + name);
		passed++;
	}
}
