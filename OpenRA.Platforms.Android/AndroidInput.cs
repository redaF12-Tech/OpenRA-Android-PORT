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
using System.Collections.Concurrent;
using Android.Views;
using OpenRA;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	// Translates Android MotionEvents (multi-touch) into OpenRA MouseInputs.
	//
	// This class is a thin adapter only:
	//   - Enqueue runs on the Android UI thread: it copies the primitive values out of the
	//     MotionEvent immediately (MotionEvent instances are recycled by Android and must not
	//     be retained) and pushes them into a lock-free queue.
	//   - PumpInput runs on the game thread: it drains the queue into the shared
	//     TouchGestureMapper (see OpenRA.Game/Input/TouchGestureMapper.cs), which owns all
	//     gesture logic (tap, drag/select, long-press right-click, two-finger pan, pinch zoom)
	//     and emits the MouseInput events the engine input system already understands.
	//
	// No gesture decisions are made here, and no GL or game calls happen on the UI thread.
	sealed class AndroidInput
	{
		readonly ConcurrentQueue<TouchGestureMapper.TouchSample> pending = new();
		readonly TouchGestureMapper gestures = new();

		// Enqueue is called from the UI thread via AndroidPlatformWindow.EnqueueMotion.
		public void Enqueue(MotionEvent e)
		{
			var action = e.ActionMasked;
			var index = e.ActionIndex;
			var timeMs = e.EventTime;

			switch (action)
			{
				case MotionEventActions.Down:
					pending.Enqueue(Sample(TouchGestureMapper.RawTouchAction.Down, e.GetPointerId(0), e.GetX(0), e.GetY(0), timeMs, e.PointerCount));
					break;

				case MotionEventActions.PointerDown:
					pending.Enqueue(Sample(TouchGestureMapper.RawTouchAction.Down, e.GetPointerId(index), e.GetX(index), e.GetY(index), timeMs, e.PointerCount));
					break;

				case MotionEventActions.Move:
					// Forward moves for every pointer in the batch; the gesture mapper filters
					// by the ids it currently tracks.
					for (var i = 0; i < e.PointerCount; i++)
						pending.Enqueue(Sample(TouchGestureMapper.RawTouchAction.Move, e.GetPointerId(i), e.GetX(i), e.GetY(i), timeMs, e.PointerCount));

					break;

				case MotionEventActions.PointerUp:
					pending.Enqueue(Sample(TouchGestureMapper.RawTouchAction.Up, e.GetPointerId(index), e.GetX(index), e.GetY(index), timeMs, e.PointerCount));
					break;

				case MotionEventActions.Up:
					// Up denotes the last pointer lifting. Emit Up for every tracked pointer so
					// the gesture state machine always returns to Idle even if an intermediate
					// PointerUp was coalesced by the system.
					for (var i = 0; i < e.PointerCount; i++)
						pending.Enqueue(Sample(TouchGestureMapper.RawTouchAction.Up, e.GetPointerId(i), e.GetX(i), e.GetY(i), timeMs, e.PointerCount));

					break;

				case MotionEventActions.Cancel:
					pending.Enqueue(new TouchGestureMapper.TouchSample { Action = TouchGestureMapper.RawTouchAction.Cancel, TimeMs = timeMs });
					break;
			}
		}

		static TouchGestureMapper.TouchSample Sample(TouchGestureMapper.RawTouchAction action, int pointerId, float x, float y, long timeMs, int pointerCount)
		{
			return new TouchGestureMapper.TouchSample
			{
				Action = action,
				PointerId = pointerId,
				X = (int)x,
				Y = (int)y,
				TimeMs = timeMs,
				PointerCount = pointerCount,
			};
		}

		// Called from Game.Loop via AndroidPlatformWindow.PumpInput, once per frame, on the
		// game thread. Environment.TickCount64 is a monotonic uptime clock on Android and is
		// comparable with MotionEvent.EventTime (also uptime milliseconds); the small skew is
		// irrelevant at the gesture-threshold scale (hundreds of ms).
		public void PumpInput(IInputHandler inputHandler, Size windowSize, Size surfaceSize, float scale)
		{
			while (pending.TryDequeue(out var sample))
			{
				foreach (var mi in gestures.ProcessTouch(sample))
					inputHandler.OnMouseInput(mi);
			}

			// Advance time-based logic (long-press) every frame, even when no new touch
			// events arrived, so a stationary finger still produces the right-click.
			foreach (var mi in gestures.ProcessTime(Environment.TickCount64))
				inputHandler.OnMouseInput(mi);
		}

		// Forward cancellation from lifecycle events (focus loss, pause, surface loss) so the
		// engine is never left with a button stuck "held".
		public void CancelGestures(IInputHandler inputHandler)
		{
			foreach (var mi in gestures.CancelGesture())
				inputHandler.OnMouseInput(mi);
		}
	}
}
