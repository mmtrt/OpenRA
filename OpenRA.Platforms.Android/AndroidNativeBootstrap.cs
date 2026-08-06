#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Load packaged arm64 native libraries and resolve DllImport names.
 */
#endregion

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Android.Util;

namespace OpenRA.Platforms.Android
{
	/// <summary>
	/// Call once at process start (before SDL/OpenAL/Lua/FreeType P/Invokes).
	/// </summary>
	public static class AndroidNativeBootstrap
	{
		static bool loaded;

		public static void Init()
		{
			if (loaded)
				return;
			loaded = true;

			NativeLibrary.SetDllImportResolver(typeof(AndroidNativeBootstrap).Assembly, Resolve);
			// Also register for OpenRA.Game if Eluant/SDL bind against it
			try
			{
				var gameAsm = Assembly.Load("OpenRA.Game");
				NativeLibrary.SetDllImportResolver(gameAsm, Resolve);
			}
			catch (Exception e)
			{
				Log.Warn("OpenRA.Native", "Could not set resolver on OpenRA.Game: " + e.Message);
			}

			Load("SDL2");
			Load("openal");
			Load("freetype");
			Load("lua5.1");
			// Eluant often DllImport("lua51")
			Load("lua51");
		}

		static void Load(string name)
		{
			try
			{
				Java.Lang.JavaSystem.LoadLibrary(name);
				Log.Info("OpenRA.Native", "Loaded lib" + name + ".so");
			}
			catch (Exception e)
			{
				Log.Warn("OpenRA.Native", "LoadLibrary(" + name + "): " + e.Message);
			}
		}

		static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
		{
			var name = libraryName;
			if (name.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
				name = name[..^3];
			if (name.StartsWith("lib", StringComparison.Ordinal))
				name = name[3..];

			// Map common desktop names → Android sonames
			name = name switch
			{
				"lua51" or "lua5.1" or "lua" => "lua5.1",
				"SDL2" or "sdl2" => "SDL2",
				"openal" or "OpenAL" or "soft_oal" => "openal",
				"freetype" or "freetype6" or "freetype-6" => "freetype",
				_ => name
			};

			if (NativeLibrary.TryLoad(name, assembly, searchPath, out var handle))
				return handle;
			if (NativeLibrary.TryLoad("lib" + name + ".so", assembly, searchPath, out handle))
				return handle;

			Log.Warn("OpenRA.Native", "DllImport resolve failed for " + libraryName);
			return IntPtr.Zero;
		}
	}
}
