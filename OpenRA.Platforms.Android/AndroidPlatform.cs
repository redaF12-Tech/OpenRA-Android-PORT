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
using System.Reflection;
using System.Runtime.InteropServices;
using OpenRA;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	// Android implementation of IPlatform. The Activity creates the window (it owns the
	// SurfaceView lifecycle) and registers it via Window before Game.InitializeAndRun is called.
	public class AndroidPlatform : IPlatform
	{
		public static AndroidPlatformWindow Window { get; private set; }

		static AndroidPlatform()
		{
			// .NET Android does not apply the legacy Mono dllmap from *.dll.config files, so the
			// P/Invokes in OpenAL-CS ("soft_oal"), OpenRA.Platforms.Default-derived font code
			// ("freetype6") and Eluant ("lua51") fail to resolve to the .so files shipped in the
			// APK. Pre-load each library via Java's loader (which searches the app's
			// nativeLibraryDir) and register explicit DllImport resolvers as a fallback.
			Preload("soft_oal");
			Preload("freetype6");
			Preload("lua51");

			TryRegisterResolver("OpenAL-CS", "soft_oal");
			TryRegisterResolver(typeof(AndroidPlatform).Assembly.GetName().Name, "freetype6");
			TryRegisterResolver("Eluant", "lua51");
		}

		// Pre-load a native library through the Android Java class loader so it lands in the
		// process exactly once, with its DT_NEEDED dependencies resolved by the system linker.
		static void Preload(string soname)
		{
			try
			{
				Java.Lang.JavaSystem.LoadLibrary(soname);
				global::Android.Util.Log.Info("OpenRA", $"Preloaded native library {soname}");
			}
			catch (Exception e)
			{
				// Not fatal here: the NativeLibrary resolvers below may still find it by path.
				global::Android.Util.Log.Warn("OpenRA", $"Preload of native library {soname} failed: {e.Message}");
			}
		}

		static void TryRegisterResolver(string assemblyName, string libraryName)
		{
			try
			{
				var assembly = Assembly.Load(assemblyName);
				NativeLibrary.SetDllImportResolver(assembly, (name, asm, searchPath) =>
				{
					if (name != libraryName)
						return IntPtr.Zero;

					// Try the name variants the APK may contain / Android may search for.
					foreach (var candidate in new[] { libraryName, $"lib{libraryName}.so", $"lib{libraryName}" })
						if (NativeLibrary.TryLoad(candidate, asm, DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.UserDirectories, out var handle))
							return handle;

					return IntPtr.Zero;
				});
			}
			catch (Exception e)
			{
				global::Android.Util.Log.Warn("OpenRA", $"Native resolver for {libraryName} ({assemblyName}) not registered: {e.Message}");
			}
		}

		public static void SetWindow(AndroidPlatformWindow window) => Window = window;

		public IPlatformWindow CreateWindow(
			Size size, WindowMode windowMode, float scaleModifier, int vertexBatchSize, int indexBatchSize, int videoDisplay, GLProfile profile)
		{
			// The window has already been created by the Activity (it needs the SurfaceView on the
			// UI thread). Apply the engine's requested scale modifier and hand back the existing instance.
			if (scaleModifier > 0)
				Window.SetScaleModifier(scaleModifier);

			// The EGL surface is created asynchronously by the Activity's SurfaceHolder callback on
			// the UI thread. The Renderer constructor accesses Window.Context immediately after this
			// returns, so we must block here until the surface is ready AND GL has been initialized
			// on this (the game) thread.
			Window.WaitForSurfaceAndInitializeGl();

			return Window;
		}

		public ISoundEngine CreateSound(string device)
		{
			try
			{
				global::Android.Util.Log.Info("OpenRA", "CreateSound: initializing OpenAL...");
				var engine = new OpenAlSoundEngine(device);
				global::Android.Util.Log.Info("OpenRA", "CreateSound: OpenAL initialized successfully");
				return engine;
			}
			catch (Exception e)
			{
				global::Android.Util.Log.Error("OpenRA", $"CreateSound: OpenAL failed: {e}");
				Log.Write("sound", "Failed to initialize OpenAL device. Error was");
				Log.Write("sound", e);
				return new DummySoundEngine();
			}
		}

		public IFont CreateFont(byte[] data)
		{
			return new FreeTypeFont(data);
		}
	}
}
