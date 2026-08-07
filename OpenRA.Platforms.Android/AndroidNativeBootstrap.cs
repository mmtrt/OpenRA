#region Copyright & License Information
/*
 * Load arm64 native libraries. SDL2 is NOT loaded here — JNI_OnLoad aborts
 * without org.libsdl.app.SDLActivity. Prototype uses AndroidEgl + GLES only.
 */
#endregion

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using ALog = global::Android.Util.Log;

namespace OpenRA.Platforms.Android
{
	public static class AndroidNativeBootstrap
	{
		static bool loaded;

		public static void Init()
		{
			if (loaded)
				return;
			loaded = true;

			NativeLibrary.SetDllImportResolver(typeof(AndroidNativeBootstrap).Assembly, Resolve);
			// FreeType DllImport lives on this assembly (AndroidFreeTypeFont)
			NativeLibrary.SetDllImportResolver(typeof(AndroidFreeTypeFont).Assembly, Resolve);
			try
			{
				var gameAsm = Assembly.Load("OpenRA.Game");
				NativeLibrary.SetDllImportResolver(gameAsm, Resolve);
			}
			catch (Exception e)
			{
				ALog.Warn("OpenRA.Native", "Could not set resolver on OpenRA.Game: " + e.Message);
			}

			// Intentionally skip SDL2 — causes abort: ClassNotFoundException org.libsdl.app.SDLActivity
			// Load("SDL2");
			Load("openal");
			Load("freetype");
			Load("lua5.1");
			Load("lua51");
		}

		static void Load(string name)
		{
			try
			{
				Java.Lang.JavaSystem.LoadLibrary(name);
				ALog.Info("OpenRA.Native", "Loaded lib" + name + ".so");
			}
			catch (Exception e)
			{
				ALog.Warn("OpenRA.Native", "LoadLibrary(" + name + "): " + e.Message);
			}
		}

		static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
		{
			var name = libraryName;
			if (name.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
				name = name[..^3];
			if (name.StartsWith("lib", StringComparison.Ordinal))
				name = name[3..];

			name = name switch
			{
				"lua51" or "lua5.1" or "lua" => "lua5.1",
				"SDL2" or "sdl2" => "SDL2",
				"openal" or "OpenAL" or "soft_oal" => "openal",
				"freetype" or "freetype6" or "freetype-6" => "freetype",
				_ => name
			};

			// Block accidental SDL2 resolve until Java side exists
			if (name == "SDL2")
			{
				ALog.Warn("OpenRA.Native", "SDL2 resolve blocked (no SDLActivity) — using EGL path");
				return IntPtr.Zero;
			}

			if (NativeLibrary.TryLoad(name, assembly, searchPath, out var handle))
				return handle;
			if (NativeLibrary.TryLoad("lib" + name + ".so", assembly, searchPath, out handle))
				return handle;

			ALog.Warn("OpenRA.Native", "DllImport resolve failed for " + libraryName);
			return IntPtr.Zero;
		}
	}
}
