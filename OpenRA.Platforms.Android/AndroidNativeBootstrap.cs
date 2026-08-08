#region Copyright & License Information
/*
 * Load arm64 native libraries and install DllImport resolvers for Eluant/OpenAL/FreeType.
 * SDL2 is NOT loaded — JNI_OnLoad aborts without SDLActivity.
 *
 * Eluant uses [DllImport("lua51")]. NativeLibrary resolvers are per-assembly, so we must
 * attach the resolver to Eluant (and any other late-loaded mod assembly), not only to
 * OpenRA.Game / Platforms.Android.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenRA.Platforms.Android
{
	public static class AndroidNativeBootstrap
	{
		static bool loaded;
		static readonly HashSet<string> ResolvedAssemblies = new(StringComparer.Ordinal);
		static string nativeLibDir;

		public static void Init()
		{
			if (loaded)
				return;
			loaded = true;

			try
			{
				try
				{
					nativeLibDir = global::Android.App.Application.Context?.ApplicationInfo?.NativeLibraryDir;
					AndroidPlatformLog.Info("OpenRA.Native", "NativeLibraryDir=" + (nativeLibDir ?? "(null)"));
				}
				catch (Exception e)
				{
					AndroidPlatformLog.Warn("OpenRA.Native", "NativeLibraryDir: " + e.Message);
				}

				// Catch assemblies loaded later (Eluant, Mods.Common via ObjectCreator).
				AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
				{
					try { AttachResolver(args.LoadedAssembly); }
					catch (Exception e) { AndroidPlatformLog.Warn("OpenRA.Native", "AssemblyLoad resolver: " + e.Message); }
				};

				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
					AttachResolver(asm);

				// Prefer the name Eluant requests. Also load dotted form if packaged that way.
				Load("lua51");
				Load("lua5.1");
				Load("openal");
				Load("freetype");

				ListNativeDir();
			}
			catch (Exception e)
			{
				AndroidPlatformLog.Error("OpenRA.Native", "Init failed: " + e);
			}
		}

		/// <summary>
		/// Call after mod DLLs are copied to BinDir / before InitializeAndRun so Eluant is covered
		/// if it is already in the default ALC.
		/// </summary>
		public static void AttachResolversToLoadedAssemblies()
		{
			try
			{
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
					AttachResolver(asm);
			}
			catch (Exception e)
			{
				AndroidPlatformLog.Warn("OpenRA.Native", "AttachResolvers: " + e.Message);
			}
		}

		static void AttachResolver(Assembly asm)
		{
			if (asm == null || asm.IsDynamic)
				return;

			string name;
			try { name = asm.GetName().Name ?? ""; }
			catch { return; }

			if (string.IsNullOrEmpty(name) || !ResolvedAssemblies.Add(name))
				return;

			try
			{
				NativeLibrary.SetDllImportResolver(asm, Resolve);
				AndroidPlatformLog.Info("OpenRA.Native", "DllImportResolver → " + name);
			}
			catch (InvalidOperationException)
			{
				// Already has a resolver — ignore.
			}
			catch (Exception e)
			{
				AndroidPlatformLog.Warn("OpenRA.Native", "SetDllImportResolver(" + name + "): " + e.Message);
			}
		}

		static void Load(string name)
		{
			// Breadcrumb BEFORE LoadLibrary — if JNI_OnLoad aborts the process we still
			// know which .so was in flight (AndroidFileLog is AutoFlush=true).
			AndroidPlatformLog.Info("OpenRA.Native", "LoadLibrary BEGIN: " + name);
			try
			{
				Java.Lang.JavaSystem.LoadLibrary(name);
				AndroidPlatformLog.Info("OpenRA.Native", "JavaSystem.LoadLibrary(" + name + ") OK");
				return;
			}
			catch (Java.Lang.Throwable t)
			{
				AndroidPlatformLog.Warn("OpenRA.Native", "JavaSystem.LoadLibrary(" + name + "): " + t.Message);
			}
			catch (Exception e)
			{
				AndroidPlatformLog.Warn("OpenRA.Native", "JavaSystem.LoadLibrary(" + name + "): " + e.Message);
			}

			// Fallback: absolute path under the app's native lib dir.
			if (!string.IsNullOrEmpty(nativeLibDir))
			{
				foreach (var file in new[] { "lib" + name + ".so", name + ".so", "lib" + name })
				{
					var path = Path.Combine(nativeLibDir, file);
					if (!File.Exists(path))
						continue;
					try
					{
						if (NativeLibrary.TryLoad(path, out _))
						{
							AndroidPlatformLog.Info("OpenRA.Native", "TryLoad path OK: " + path);
							return;
						}
					}
					catch (Exception e)
					{
						AndroidPlatformLog.Warn("OpenRA.Native", "TryLoad " + path + ": " + e.Message);
					}
				}
			}
		}

		static void ListNativeDir()
		{
			if (string.IsNullOrEmpty(nativeLibDir) || !Directory.Exists(nativeLibDir))
				return;
			try
			{
				foreach (var f in Directory.GetFiles(nativeLibDir, "liblua*"))
					AndroidPlatformLog.Info("OpenRA.Native", "  found " + f);
				foreach (var f in Directory.GetFiles(nativeLibDir, "libopenal*"))
					AndroidPlatformLog.Info("OpenRA.Native", "  found " + f);
				foreach (var f in Directory.GetFiles(nativeLibDir, "libfreetype*"))
					AndroidPlatformLog.Info("OpenRA.Native", "  found " + f);
			}
			catch (Exception e)
			{
				AndroidPlatformLog.Warn("OpenRA.Native", "ListNativeDir: " + e.Message);
			}
		}

		static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
		{
			var raw = libraryName ?? "";
			var name = raw;
			if (name.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
				name = name[..^3];
			if (name.StartsWith("lib", StringComparison.Ordinal))
				name = name[3..];

			// Canonical names we package under lib/arm64-v8a/
			var candidates = name switch
			{
				"lua51" or "lua5.1" or "lua" => new[] { "lua51", "lua5.1" },
				"SDL2" or "sdl2" => new[] { "SDL2" },
				"openal" or "OpenAL" or "soft_oal" => new[] { "openal" },
				"freetype" or "freetype6" or "freetype-6" => new[] { "freetype" },
				_ => new[] { name }
			};

			if (candidates[0] == "SDL2")
			{
				AndroidPlatformLog.Warn("OpenRA.Native", "SDL2 resolve blocked");
				return IntPtr.Zero;
			}

			foreach (var c in candidates)
			{
				if (NativeLibrary.TryLoad(c, assembly, searchPath, out var handle) && handle != IntPtr.Zero)
					return handle;
				if (NativeLibrary.TryLoad("lib" + c + ".so", assembly, searchPath, out handle) && handle != IntPtr.Zero)
					return handle;

				if (!string.IsNullOrEmpty(nativeLibDir))
				{
					var path = Path.Combine(nativeLibDir, "lib" + c + ".so");
					if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle) && handle != IntPtr.Zero)
					{
						AndroidPlatformLog.Info("OpenRA.Native", "Resolved " + raw + " → " + path);
						return handle;
					}
				}
			}

			AndroidPlatformLog.Warn("OpenRA.Native", "DllImport resolve failed for '" + raw + "' (asm=" + (assembly?.GetName().Name ?? "?") + ")");
			return IntPtr.Zero;
		}
	}
}
