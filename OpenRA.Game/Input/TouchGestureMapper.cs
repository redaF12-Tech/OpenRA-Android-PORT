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

using System;
using System.Collections.Generic;

namespace OpenRA
{
	// Pure-C# touch gesture state machine shared between platform backends (currently the
	// Android port). It converts a stream of raw touch samples into the MouseInput events the
	// engine's input system already understands, so all existing world/UI interactions
	// (drag-select, orders, map pan, zoom) keep their desktop behavior:
	//
	//   - Single tap            -> Left Down + Left Up
	//   - Drag                  -> Left Down, Left Move..., Left Up (engine draws drag-select)
	//   - Long press            -> Left Up, Right Down, (Right Move...), Right Up
	//   - Second finger         -> ends the one-finger gesture, starts a Right-button pan
	//   - Pinch (two fingers)   -> Scroll events with the Ctrl modifier (viewport zoom)
	//
	// The class holds no platform state beyond its own fields and performs no GL/OS calls,
	// so it is fully unit-testable. Callers must pump it from the game thread only.
	public sealed class TouchGestureMapper
	{
		public enum RawTouchAction { Down, Move, Up, Cancel }

		public struct TouchSample
		{
			public RawTouchAction Action;
			public int PointerId;
			public int X;
			public int Y;

			// Monotonic milliseconds in the same clock domain for all samples (Android
			// MotionEvent.EventTime, i.e. uptime milliseconds).
			public long TimeMs;

			// Number of pointers reported by the current MotionEvent batch.
			public int PointerCount;
		}

		public enum GestureState
		{
			Idle,

			// One finger down, left button held, finger has not moved beyond the slop radius.
			Pressed,

			// One finger down and dragging: left button held, moves are forwarded.
			Dragging,

			// Long press fired: left was released, right button is held.
			LongPressed,

			// Two-finger pan/pinch in progress.
			TwoFinger,

			// A multi-finger gesture ended; the remaining finger is swallowed until it lifts.
			Settling,
		}

		// Tunable thresholds (milliseconds / pixels). Defaults chosen for typical phone use.
		public int LongPressMs = 500;
		public int TouchSlopPx = 16;
		public int DoubleTapWindowMs = 300;
		public int DoubleTapSlopPx = 32;
		public int MinPinchDeltaPx = 2;

		public GestureState State { get; private set; } = GestureState.Idle;

		// Button bits currently reported as pressed to the engine (MouseButton.Left/Right).
		// Used to release held buttons on cancellation without inventing extra clicks.
		MouseButton heldButtons;

		int primaryId = -1;
		int secondaryId = -1;
		int2 primaryDownPos;
		long primaryDownTime;
		int2 primaryLastPos;

		// Last observed positions of the tracked pointers, for two-finger geometry.
		int2 secondaryLastPos;

		// Pinch tracking.
		float lastPinchDist;

		// Double-tap detection: completed taps feed this.
		int lastTapCount;
		long lastTapTime;
		int2 lastTapPos;

		readonly List<MouseInput> output = [];

		/// <summary>Feed one raw touch sample; returns the MouseInput events it produced.</summary>
		public IReadOnlyList<MouseInput> ProcessTouch(in TouchSample sample)
		{
			output.Clear();
			var pos = new int2(sample.X, sample.Y);

			switch (sample.Action)
			{
				case RawTouchAction.Down: HandleDown(sample.PointerId, pos, sample.TimeMs, sample.PointerCount); break;
				case RawTouchAction.Move: HandleMove(sample.PointerId, pos); break;
				case RawTouchAction.Up: HandleUp(sample.PointerId, pos, sample.TimeMs); break;
				case RawTouchAction.Cancel: CancelGesture(); break;
			}

			return output;
		}

		/// <summary>
		/// Advance time-based logic (long-press firing) from the game thread. Call once per frame
		/// with the current time in the same clock domain as TouchSample.TimeMs.
		/// </summary>
		public IReadOnlyList<MouseInput> ProcessTime(long nowMs)
		{
			output.Clear();

			if (State == GestureState.Pressed && nowMs - primaryDownTime >= LongPressMs)
				FireLongPress(nowMs);

			return output;
		}

		/// <summary>
		/// Cancel any in-progress gesture and release all held mouse buttons. Used for
		/// ACTION_CANCEL, window focus loss, and surface teardown. Never emits clicks.
		/// </summary>
		public IReadOnlyList<MouseInput> CancelGesture()
		{
			output.Clear();

			ReleaseHeldButtons(primaryLastPos);

			State = GestureState.Idle;
			primaryId = secondaryId = -1;
			primaryDownTime = 0;
			lastPinchDist = 0;

			return output;
		}

		void HandleDown(int pointerId, int2 pos, long timeMs, int pointerCount)
		{
			if (State == GestureState.Settling)
				return;

			if (State == GestureState.Idle)
			{
				primaryId = pointerId;
				primaryDownPos = primaryLastPos = pos;
				primaryDownTime = timeMs;

				State = GestureState.Pressed;

				// Desktop convention: the tap count is computed at button-down time.
				var tapCount = DetectTap(pos, timeMs);
				output.Add(new MouseInput(MouseInputEvent.Down, MouseButton.Left, pos, int2.Zero, Modifiers.None, tapCount));
				heldButtons |= MouseButton.Left;
			}
			else if (secondaryId < 0)
			{
				// Second finger lands: cleanly end the single-finger gesture, then start the
				// two-finger pan/pinch. The old gesture's buttons are released so the engine
				// never sees both buttons held at once.
				secondaryId = pointerId;
				secondaryLastPos = pos;

				ReleaseHeldButtons(primaryLastPos);
				State = GestureState.TwoFinger;

				// Two-finger pan moves the map with the right button held, matching the desktop
				// right-button drag pan semantics.
				output.Add(new MouseInput(MouseInputEvent.Down, MouseButton.Right, primaryLastPos, int2.Zero, Modifiers.None, 1));
				heldButtons |= MouseButton.Right;

				lastPinchDist = 0;

				if (pointerCount >= 2)
					UpdatePinch(primaryLastPos, secondaryLastPos);
			}
		}

		void HandleMove(int pointerId, int2 pos)
		{
			switch (State)
			{
				case GestureState.Pressed when pointerId == primaryId:
					primaryLastPos = pos;

					if ((pos - primaryDownPos).Length >= TouchSlopPx)
					{
						// Moved beyond the slop radius: this is a drag, not a tap.
						State = GestureState.Dragging;
						output.Add(new MouseInput(MouseInputEvent.Move, MouseButton.Left, pos, int2.Zero, Modifiers.None, 0));
					}

					break;

				case GestureState.Dragging when pointerId == primaryId:
					primaryLastPos = pos;
					output.Add(new MouseInput(MouseInputEvent.Move, MouseButton.Left, pos, int2.Zero, Modifiers.None, 0));
					break;

				case GestureState.LongPressed when pointerId == primaryId:
					// Movement after a long press drags with the right button held (contextual
					// drag), matching desktop right-button drag semantics.
					primaryLastPos = pos;
					output.Add(new MouseInput(MouseInputEvent.Move, MouseButton.Right, pos, int2.Zero, Modifiers.None, 0));
					break;

				case GestureState.TwoFinger:
					if (pointerId == primaryId)
						primaryLastPos = pos;
					else if (pointerId == secondaryId)
						secondaryLastPos = pos;
					else
						break;

					// Two-finger pan (midpoint moves with the right button) + pinch zoom.
					output.Add(new MouseInput(MouseInputEvent.Move, MouseButton.Right,
						(primaryLastPos + secondaryLastPos) / 2, int2.Zero, Modifiers.None, 0));
					UpdatePinch(primaryLastPos, secondaryLastPos);
					break;
			}
		}

		void HandleUp(int pointerId, int2 pos, long timeMs)
		{
			if (State == GestureState.TwoFinger)
			{
				// A finger lifted mid-gesture: end the two-finger gesture cleanly without
				// producing stray clicks. Any remaining finger is swallowed until it lifts.
				ReleaseHeldButtons(pos);
				State = GestureState.Settling;
				secondaryId = -1;
				lastPinchDist = 0;
				return;
			}

			if (State == GestureState.Settling)
			{
				// The last remaining finger from the finished two-finger gesture lifted: back to Idle.
				if (pointerId == primaryId)
				{
					primaryId = -1;
					State = GestureState.Idle;
				}

				return;
			}

			if (pointerId != primaryId)
				return;

			// Release everything held, in order. Tap completion feeds double-tap detection.
			if (State == GestureState.Pressed)
			{
				ReleaseHeldButtons(pos);
				lastTapCount = DetectTap(pos, timeMs);
				lastTapTime = timeMs;
				lastTapPos = pos;
			}
			else
			{
				// Dragging or LongPressed: buttons release at the final position, no synthetic tap.
				ReleaseHeldButtons(pos);
				lastTapCount = 0;
			}

			State = GestureState.Idle;
			primaryId = -1;
		}

		/// <summary>Fire the long press: releases the held left button, then presses right.</summary>
		void FireLongPress(long nowMs)
		{
			State = GestureState.LongPressed;

			// The left button was down (Pressed state): release it before pressing right so the
			// engine never sees both buttons held, and so the pending press doesn't become a click.
			if ((heldButtons & MouseButton.Left) != 0)
			{
				output.Add(new MouseInput(MouseInputEvent.Up, MouseButton.Left, primaryLastPos, int2.Zero, Modifiers.None, 0));
				heldButtons &= ~MouseButton.Left;
			}

			output.Add(new MouseInput(MouseInputEvent.Down, MouseButton.Right, primaryLastPos, int2.Zero, Modifiers.None, 1));
			heldButtons |= MouseButton.Right;

			// A long press is not a tap: reset double-tap history.
			lastTapCount = 0;
			primaryDownTime = nowMs;
		}

		void ReleaseHeldButtons(int2 pos)
		{
			// Release right before left so any drag-selection ends before a right-button
			// order/context event is delivered, matching desktop event ordering.
			if ((heldButtons & MouseButton.Right) != 0)
			{
				output.Add(new MouseInput(MouseInputEvent.Up, MouseButton.Right, pos, int2.Zero, Modifiers.None, 0));
				heldButtons &= ~MouseButton.Right;
			}

			if ((heldButtons & MouseButton.Left) != 0)
			{
				output.Add(new MouseInput(MouseInputEvent.Up, MouseButton.Left, pos, int2.Zero, Modifiers.None, 0));
				heldButtons &= ~MouseButton.Left;
			}
		}

		void UpdatePinch(int2 a, int2 b)
		{
			var dx = b.X - a.X;
			var dy = b.Y - a.Y;
			var dist = (float)Math.Sqrt(dx * dx + dy * dy);

			if (lastPinchDist > 0)
			{
				var delta = (int)(dist - lastPinchDist);
				if (Math.Abs(delta) >= MinPinchDeltaPx)
				{
					// Scroll deltas are carried inside the event itself (no shared mutable state
					// between threads). Ctrl matches the desktop zoom modifier expectation.
					output.Add(new MouseInput(MouseInputEvent.Scroll, MouseButton.None,
						(a + b) / 2, new int2(0, delta), Modifiers.Ctrl, 0));
				}
			}

			lastPinchDist = dist;
		}

		int DetectTap(int2 pos, long timeMs)
		{
			if (lastTapCount > 0 &&
				timeMs - lastTapTime <= DoubleTapWindowMs &&
				(pos - lastTapPos).Length <= DoubleTapSlopPx)
				return lastTapCount + 1;

			return 1;
		}
	}
}
