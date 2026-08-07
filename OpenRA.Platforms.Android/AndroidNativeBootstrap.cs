#region Copyright & License Information
/*
 * Load arm64 native libraries. SDL2 is NOT loaded — JNI_OnLoad aborts without SDLActivity.
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

			try
			{
				NativeLibrary.SetDllImportResolver(typeof(AndroidNativeBootstrap).Assembly, Resolve);
				try
				{
					NativeLibrary.SetDllImportResolver(typeof(AndroidFreeTypeFont).Assembly, Resolve);
				}
				catch (Exception e)
				{
					ALog.Warn("OpenRA.Native", "FreeType resolver: " + e.Message);
				}

				try
				{
					var gameAsm = Assembly.Load("OpenRA.Game");
					NativeLibrary.SetDllImportResolver(gameAsm, Resolve);
				}
				catch (Exception e)
				{
					ALog.Warn("OpenRA.Native", "Game resolver: " + e.Message);
				}

				Load("openal");
				Load("freetype");
				Load("lua5.1");
				Load("lua51");
			}
			catch (Exception e)
			{
				ALog.Error("OpenRA.Native", "Init failed: " + e);
			}
		}

		static void Load(string name)
		{
			try
			{
				Java.Lang.JavaSystem.LoadLibrary(name);
				ALog.Info("OpenRA.Native", "Loaded lib" + name + ".so");
			}
			catch (Java.Lang.Throwable t)
			{
				ALog.Warn("OpenRA.Native", "LoadLibrary(" + name + ") Throwable: " + t.Message);
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

			if (name == "SDL2")
			{
				ALog.Warn("OpenRA.Native", "SDL2 resolve blocked");
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
