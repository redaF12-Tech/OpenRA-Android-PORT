#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class TouchGestureMapperTests
	{
		static List<MouseInput> Pump(TouchGestureMapper g, in TouchGestureMapper.TouchSample sample)
		{
			return g.ProcessTouch(sample).ToList();
		}

		static List<MouseInput> Tick(TouchGestureMapper g, long nowMs)
		{
			return g.ProcessTime(nowMs).ToList();
		}

		static TouchGestureMapper.TouchSample Sample(TouchGestureMapper.RawTouchAction action, int id, int x, int y, long t, int count = 1)
		{
			return new TouchGestureMapper.TouchSample { Action = action, PointerId = id, X = x, Y = y, TimeMs = t, PointerCount = count };
		}

		static string Describe(IEnumerable<MouseInput> inputs)
		{
			return string.Join(", ", inputs.Select(mi => $"{mi.Event}:{mi.Button}@{mi.Location}"));
		}

		[Test]
		public void SingleTapEmitsLeftDownThenLeftUp()
		{
			var g = new TouchGestureMapper();

			var down = Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));
			Assert.That(down.Count, Is.EqualTo(1), Describe(down));
			Assert.That(down[0].Event, Is.EqualTo(MouseInputEvent.Down));
			Assert.That(down[0].Button, Is.EqualTo(MouseButton.Left));
			Assert.That(down[0].Location, Is.EqualTo(new int2(100, 100)));

			var up = Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 102, 101, 80));
			Assert.That(up.Count, Is.EqualTo(1), Describe(up));
			Assert.That(up[0].Event, Is.EqualTo(MouseInputEvent.Up));
			Assert.That(up[0].Button, Is.EqualTo(MouseButton.Left));
		}

		[Test]
		public void TapDoesNotEmitExtraClickOnLift()
		{
			var g = new TouchGestureMapper();
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 50, 50, 0));
			var events = Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 50, 50, 60));

			Assert.That(events.Count, Is.EqualTo(1));
			Assert.That(events[0].Event, Is.EqualTo(MouseInputEvent.Up));
		}

		[Test]
		public void DoubleTapWithinWindowReportsMultiTapCount()
		{
			var g = new TouchGestureMapper();

			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 100, 100, 40));

			var secondDown = Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 104, 100, 120));
			Assert.That(secondDown[0].MultiTapCount, Is.EqualTo(2), Describe(secondDown));
		}

		[Test]
		public void DistantTapsAreNotADoubleTap()
		{
			var g = new TouchGestureMapper();

			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 100, 100, 40));

			var secondDown = Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 400, 400, 120));
			Assert.That(secondDown[0].MultiTapCount, Is.EqualTo(1), Describe(secondDown));
		}

		[Test]
		public void DragEmitsDownMoveUpInOrder()
		{
			var g = new TouchGestureMapper();

			var all = new List<MouseInput>();
			all.AddRange(Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0)));
			all.AddRange(Pump(g, Sample(TouchGestureMapper.RawTouchAction.Move, 0, 140, 100, 10)));
			all.AddRange(Pump(g, Sample(TouchGestureMapper.RawTouchAction.Move, 0, 200, 120, 20)));
			all.AddRange(Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 205, 120, 30)));

			Assert.That(all.Select(mi => mi.Event).ToArray(),
				Is.EqualTo(new[] { MouseInputEvent.Down, MouseInputEvent.Move, MouseInputEvent.Move, MouseInputEvent.Up }));
			Assert.That(all.Count(mi => mi.Button == MouseButton.Left), Is.EqualTo(all.Count), Describe(all));
		}

		[Test]
		public void DragDoesNotEmitSyntheticTap()
		{
			var g = new TouchGestureMapper();

			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Move, 0, 200, 200, 10));
			var up = Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 200, 200, 20));

			Assert.That(up.Count, Is.EqualTo(1));
			Assert.That(up[0].Event, Is.EqualTo(MouseInputEvent.Up));
			Assert.That(up[0].Button, Is.EqualTo(MouseButton.Left));
		}

		[Test]
		public void LongPressFiresRightClickWithoutMoveEvents()
		{
			var g = new TouchGestureMapper();

			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));

			// No moves arrive (stationary finger): the long press must still fire via ProcessTime.
			var fired = Tick(g, 500);
			Assert.That(fired.Select(mi => $"{mi.Event}:{mi.Button}").ToArray(),
				Is.EqualTo(new[] { "Up:Left", "Down:Right" }), Describe(fired));
		}

		[Test]
		public void EarlyLiftCancelsLongPressAndEmitsLeftClick()
		{
			var g = new TouchGestureMapper();

			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));

			// Lift before the long-press threshold: an ordinary left click completes.
			var up = Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 100, 100, 300));
			Assert.That(up.Select(mi => $"{mi.Event}:{mi.Button}").ToArray(), Is.EqualTo(new[] { "Up:Left" }), Describe(up));

			// Nothing further must fire after the finger is gone.
			Assert.That(Tick(g, 800), Is.Empty);
		}

		[Test]
		public void MovementBeyondSlopBeforeThresholdCancelsLongPress()
		{
			var g = new TouchGestureMapper();

			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Move, 0, 130, 100, 100));

			// Past the threshold but the finger is dragging: no right click may fire.
			Assert.That(Tick(g, 600), Is.Empty);
		}

		[Test]
		public void LongPressRightClickReleasesRightOnLiftWithoutLeftResidue()
		{
			var g = new TouchGestureMapper();

			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));
			Tick(g, 500);
			var up = Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 102, 100, 700));

			Assert.That(up.Count, Is.EqualTo(1), Describe(up));
			Assert.That(up[0].Event, Is.EqualTo(MouseInputEvent.Up));
			Assert.That(up[0].Button, Is.EqualTo(MouseButton.Right));
		}

		[Test]
		public void SecondFingerStartsTwoFingerPanAndReleasesLeft()
		{
			var g = new TouchGestureMapper();

			var all = new List<MouseInput>();
			all.AddRange(Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0)));
			all.AddRange(Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 1, 200, 100, 10, 2)));

			// The left press must be released before the right press begins, so the engine
			// never sees both buttons held at once.
			Assert.That(all.Select(mi => $"{mi.Event}:{mi.Button}").ToArray(),
				Is.EqualTo(new[] { "Down:Left", "Up:Left", "Down:Right" }), Describe(all));
		}

		[Test]
		public void TwoFingerLiftDoesNotEmitStrayClicks()
		{
			var g = new TouchGestureMapper();

			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 1, 200, 100, 10, 2));
			var all = new List<MouseInput>();
			all.AddRange(Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 1, 200, 100, 20, 2)));
			all.AddRange(Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 100, 100, 40, 1)));

			// No Down events may be emitted by the lift sequence.
			Assert.That(all.Where(mi => mi.Event == MouseInputEvent.Down), Is.Empty, Describe(all));
			Assert.That(g.State, Is.EqualTo(TouchGestureMapper.GestureState.Idle));
		}

		[Test]
		public void PinchProducesScrollEventsWithDeltas()
		{
			var g = new TouchGestureMapper();

			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 1, 200, 100, 10, 2));

			// Spread the fingers apart: distance grows from 100 to 130 -> delta 30.
			var scrolls = new List<MouseInput>();
			scrolls.AddRange(Pump(g, Sample(TouchGestureMapper.RawTouchAction.Move, 0, 85, 100, 20, 2)));
			scrolls.AddRange(Pump(g, Sample(TouchGestureMapper.RawTouchAction.Move, 1, 215, 100, 20, 2)));

			var scroll = scrolls.FirstOrDefault(mi => mi.Event == MouseInputEvent.Scroll);
			Assert.That(scroll, Is.Not.Null, Describe(scrolls));
			Assert.That(scroll.Delta.Y, Is.GreaterThan(0));
			Assert.That(scroll.Modifiers, Is.EqualTo(Modifiers.Ctrl));
			Assert.That(scroll.Button, Is.EqualTo(MouseButton.None));

			// Pinch must not generate fake clicks or selection presses.
			Assert.That(scrolls.Where(mi => mi.Event == MouseInputEvent.Down), Is.Empty, Describe(scrolls));
		}

		[Test]
		public void CancelReleasesAllHeldButtons()
		{
			var g = new TouchGestureMapper();

			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));
			Tick(g, 500); // Long press: right is now held.

			var cancel = Pump(g, Sample(TouchGestureMapper.RawTouchAction.Cancel, 0, 100, 100, 600));

			Assert.That(cancel.Select(mi => $"{mi.Event}:{mi.Button}").ToArray(), Is.EqualTo(new[] { "Up:Right" }), Describe(cancel));
			Assert.That(g.State, Is.EqualTo(TouchGestureMapper.GestureState.Idle));

			// After cancellation nothing further fires.
			Assert.That(Tick(g, 2000), Is.Empty);
			Assert.That(Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 100, 100, 2100)), Is.Empty);
		}

		[Test]
		public void CancelDuringDragReleasesLeftOnly()
		{
			var g = new TouchGestureMapper();

			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Move, 0, 200, 200, 10));

			var cancel = Pump(g, Sample(TouchGestureMapper.RawTouchAction.Cancel, 0, 200, 200, 20));

			Assert.That(cancel.Select(mi => $"{mi.Event}:{mi.Button}").ToArray(), Is.EqualTo(new[] { "Up:Left" }), Describe(cancel));
		}

		[Test]
		public void StateIsIdleAfterEveryCompletedGesture()
		{
			var g = new TouchGestureMapper();

			// Tap.
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 0, 0, 0));
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 0, 0, 50));
			Assert.That(g.State, Is.EqualTo(TouchGestureMapper.GestureState.Idle));

			// Drag.
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 0, 0, 100));
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Move, 0, 100, 0, 110));
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 100, 0, 200));
			Assert.That(g.State, Is.EqualTo(TouchGestureMapper.GestureState.Idle));

			// Long press.
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 0, 0, 300));
			Tick(g, 800);
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 0, 0, 900));
			Assert.That(g.State, Is.EqualTo(TouchGestureMapper.GestureState.Idle));
		}

		[Test]
		public void DoubleTapAndLongPressDoNotConflict()
		{
			var g = new TouchGestureMapper();

			// First tap completes.
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 100, 100, 0));
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Up, 0, 100, 100, 40));

			// Second press is held: double-tap window expires, then the long press fires.
			Pump(g, Sample(TouchGestureMapper.RawTouchAction.Down, 0, 104, 100, 60));

			var before = Tick(g, 600); // 540ms after the second press started: past the threshold.
			Assert.That(before.Select(mi => $"{mi.Event}:{mi.Button}").ToArray(),
				Is.EqualTo(new[] { "Up:Left", "Down:Right" }), Describe(before));
		}
	}
}
