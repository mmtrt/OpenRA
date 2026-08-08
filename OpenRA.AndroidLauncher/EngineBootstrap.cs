using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Android.App;
using OpenRA;
using OpenRA.Platforms.Android;

namespace OpenRA.Android
{
	public static class EngineBootstrap
	{
		public static bool IsRunning { get; private set; }
		public static string SupportDir { get; private set; }
		public static string CacheDir { get; private set; }

		static Thread gameThread;
		static int startAttempts;
		static GameSurfaceView boundSurface;
		static bool resolveHooked;

		public static void Start(GameSurfaceView surface, string mod = "ra")
		{
			if (IsRunning)
				return;

			boundSurface = surface;

			try
			{
				AndroidFileLog.Init(); // logcat-only until official channels exist
				AndroidFileLog.Info("OpenRA.Bootstrap", $"Start attempt={++startAttempts} mod={mod}");

				ContentBootstrap.EnsureLayout(ContentBootstrap.SupportDir ?? StorageAccess.ResolveSupportDir());
				SupportDir = ContentBootstrap.SupportDir;
				CacheDir = Path.Combine(SupportDir, "Cache");
				Directory.CreateDirectory(CacheDir);

				// Official engine logging under SupportDir/Logs/ (not a parallel openra.log).
				InitOfficialLogging(SupportDir);

				// ObjectCreator loads mod DLLs from Platform.BinDir (== BaseDirectory).
				// Place assemblies there and install a resolve hook so we never dual-load
				// (duplicate MobileInfo types break Chronoshiftable.HasTraitInfo<MobileInfo>()).
				EnsureSingleAssemblyLoad();
				ContentBootstrap.PlaceAssembliesForLoader();
				CopyModsToBinDir();

				AndroidNativeBootstrap.Init();
				AndroidNativeBootstrap.AttachResolversToLoadedAssemblies();

				Platform.AndroidFilesDir = SupportDir;
				Platform.AndroidCacheDir = CacheDir;
				Game.PlatformFactory = () => new AndroidPlatform();

				if (!ContentBootstrap.HasAnyMod())
				{
					AndroidFileLog.Warn("OpenRA.Bootstrap", "No mod.yaml — not starting.");
					return;
				}

				if (surface == null || !surface.IsSurfaceReady)
				{
					AndroidFileLog.Warn("OpenRA.Bootstrap", "Surface/EGL not ready — defer start");
					return;
				}

				IsRunning = true;
				gameThread = new Thread(() => RunEngine(mod))
				{
					IsBackground = true,
					Name = "OpenRA.Game"
				};
				gameThread.Start();
			}
			catch (Exception e)
			{
				IsRunning = false;
				AndroidFileLog.Exception("OpenRA.Bootstrap.Start", e);
			}
		}

		/// <summary>
		/// Prefer already-loaded assemblies by simple name so ObjectCreator's LoadFrom path
		/// cannot introduce a second copy of OpenRA.Mods.Common with different Type identities.
		/// </summary>
		static void EnsureSingleAssemblyLoad()
		{
			if (resolveHooked)
				return;
			resolveHooked = true;

			AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
			{
				try
				{
					var simple = new AssemblyName(args.Name).Name;
					if (string.IsNullOrEmpty(simple))
						return null;

					foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
					{
						try
						{
							if (a.GetName().Name == simple)
								return a;
						}
						catch { /* dynamic */ }
					}

					// Fall back: SupportDir / BaseDirectory
					foreach (var dir in new[]
					{
						AppDomain.CurrentDomain.BaseDirectory,
						SupportDir,
						ContentBootstrap.SupportDir
					})
					{
						if (string.IsNullOrEmpty(dir))
							continue;
						var path = Path.Combine(dir, simple + ".dll");
						if (File.Exists(path))
						{
							AndroidFileLog.Info("OpenRA.Bootstrap", "AssemblyResolve load " + path);
							return Assembly.LoadFrom(path);
						}
					}
				}
				catch (Exception e)
				{
					AndroidFileLog.Warn("OpenRA.Bootstrap", "AssemblyResolve: " + e.Message);
				}

				return null;
			};

			AndroidFileLog.Info("OpenRA.Bootstrap", "AssemblyResolve hook installed");
			AndroidFileLog.Info("OpenRA.Bootstrap", "BaseDirectory=" + AppDomain.CurrentDomain.BaseDirectory);
		}

		static void CopyModsToBinDir()
		{
			var bin = AppDomain.CurrentDomain.BaseDirectory;
			if (string.IsNullOrEmpty(bin) || string.IsNullOrEmpty(SupportDir))
				return;

			try
			{
				Directory.CreateDirectory(bin);
			}
			catch { /* ignore */ }

			foreach (var name in new[]
			{
				"OpenRA.Mods.Common.dll",
				"OpenRA.Mods.Cnc.dll",
				"Eluant.dll",
				"TagLibSharp.dll",
				"Newtonsoft.Json.dll",
				"ICSharpCode.SharpZipLib.dll",
				"Linguini.Bundle.dll",
				"Linguini.Shared.dll",
				"Linguini.Syntax.dll",
				"BeaconLib.dll",
				"FuzzyLogicLibrary.dll",
				"MP3Sharp.dll",
				"Mono.Nat.dll",
				"NVorbis.dll",
				"Pfim.dll",
				"Microsoft.Extensions.DependencyModel.dll"
			})
			{
				var src = Path.Combine(SupportDir, name);
				if (!File.Exists(src))
					continue;
				var dest = Path.Combine(bin, name);
				try
				{
					if (!File.Exists(dest) || new FileInfo(src).Length != new FileInfo(dest).Length)
					{
						File.Copy(src, dest, overwrite: true);
						AndroidFileLog.Info("OpenRA.Bootstrap", "BinDir ← " + name);
					}
				}
				catch (Exception e)
				{
					AndroidFileLog.Warn("OpenRA.Bootstrap", "BinDir copy " + name + ": " + e.Message);
				}
			}

			// Log which Mods.Common is in the domain already
			foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					var n = a.GetName().Name;
					if (n != null && n.StartsWith("OpenRA.Mods", StringComparison.Ordinal))
						AndroidFileLog.Info("OpenRA.Bootstrap", "Already loaded: " + n + " @ " + (a.Location ?? "(dynamic)"));
				}
				catch { /* ignore */ }
			}
		}

		static void RunEngine(string mod)
		{
			try
			{
				AndroidFileLog.Info("OpenRA.Bootstrap", "Game thread enter");

				try { Directory.SetCurrentDirectory(SupportDir); }
				catch (Exception e) { AndroidFileLog.Warn("OpenRA.Bootstrap", "cwd: " + e.Message); }

				AndroidFileLog.Info("OpenRA.Bootstrap", "cwd=" + Directory.GetCurrentDirectory());
				AndroidFileLog.Info("OpenRA.Bootstrap", "BinDir/BaseDirectory=" + AppDomain.CurrentDomain.BaseDirectory);

				if (!EnsureEglCurrent())
				{
					AndroidFileLog.Error("OpenRA.Bootstrap", "Cannot bind EGL — abort engine start");
					return;
				}

				var versionPath = Path.Combine(SupportDir, "VERSION");
				if (!File.Exists(versionPath))
					File.WriteAllText(versionPath, "android-port-dev");

				Directory.CreateDirectory(Path.Combine(SupportDir, "Logs"));

				// Engine.SupportDir intentionally omitted: ForceSupportDir already set
				// Platform.SupportDir. Passing it again makes Game.Initialize call
				// OverrideSupportDir which throws InvalidOperationException.
				var args = new[]
				{
					"Engine.Platform=Android",
					"Game.Mod=" + mod,
					"Engine.EngineDir=" + SupportDir,
					"Engine.ModSearchPaths=" + ContentBootstrap.ModsDir,
					"Graphics.DisableHardwareCursors=True",
					"Graphics.GLProfile=Embedded"
				};

				WriteAndroidSettings(SupportDir);
				try { Game.HideCursor = true; } catch { /* older builds */ }

				// Eluant may load here — ensure DllImportResolver is on every assembly in the default ALC.
				AndroidNativeBootstrap.AttachResolversToLoadedAssemblies();
				AndroidFileLog.Info("OpenRA.Bootstrap", "InitializeAndRun " + string.Join(" ", args)
					+ " surface=" + AndroidEgl.SurfaceWidth + "x" + AndroidEgl.SurfaceHeight);
				Game.InitializeAndRun(args);
				AndroidFileLog.Info("OpenRA.Bootstrap", "InitializeAndRun returned");
			}
			catch (Exception e)
			{
				AndroidFileLog.Exception("OpenRA.Bootstrap", e);
			}
			finally
			{
				IsRunning = false;
				AndroidFileLog.Info("OpenRA.Bootstrap", "Game thread exit");
			}
		}


		static void WriteAndroidSettings(string supportDir)
		{
			try
			{
				var path = Path.Combine(supportDir, "settings.yaml");
				var w = Math.Max(1, AndroidEgl.SurfaceWidth);
				var h = Math.Max(1, AndroidEgl.SurfaceHeight);
				var nl = Environment.NewLine;
				var tab = "	";
				var yaml =
					"Player:" + nl +
					tab + "Name: Android Commander" + nl +
					"Graphics:" + nl +
					tab + "Mode: Windowed" + nl +
					tab + "WindowedSize: " + w + "," + h + nl +
					tab + "FullscreenSize: " + w + "," + h + nl +
					tab + "DisableHardwareCursors: true" + nl +
					tab + "GLProfile: Embedded" + nl +
					tab + "VSync: true" + nl +
					"Sound:" + nl +
					tab + "Device: Null" + nl;
				File.WriteAllText(path, yaml);
				AndroidFileLog.Info("OpenRA.Bootstrap",
					"Wrote settings.yaml " + w + "x" + h + " DisableHardwareCursors=true");
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Bootstrap", "settings.yaml: " + e.Message);
			}
		}


		/// <summary>
		/// Point Platform.SupportDir at the Android support tree and open the same
		/// log channels the desktop engine uses (plus "android"). All launcher /
		/// platform diagnostics then go through OpenRA.Support.Log → SupportDir/Logs/.
		/// </summary>
		static int officialLoggingInited;

		public static void InitOfficialLogging(string supportDir)
		{
			// Idempotent — MainActivity + EngineBootstrap.Start both call this.
			if (System.Threading.Interlocked.CompareExchange(ref officialLoggingInited, 0, 0) == 1)
				return;

			try
			{
				BootLog.Init();
				if (string.IsNullOrEmpty(supportDir))
				{
					BootLog.Warn("InitOfficialLogging: supportDir empty");
					return;
				}

				var dir = supportDir;
				if (dir[^1] != Path.DirectorySeparatorChar && dir[^1] != Path.AltDirectorySeparatorChar)
					dir += Path.DirectorySeparatorChar;

				// OverrideSupportDir requires the directory to already exist.
				Directory.CreateDirectory(dir);
				Directory.CreateDirectory(Path.Combine(dir, "Logs"));
				BootLog.Info("InitOfficialLogging dir=" + dir);

				// Must run before any Platform.SupportDir access (Log.AddChannel uses it).
				// Upstream OverrideSupportDir → InitializeSupportDir uses SpecialFolder.UserProfile,
				// which is null on Android → Path.Combine throws. ForceSupportDir handles that.
				if (!ForceSupportDir(dir))
				{
					BootLog.Error("ForceSupportDir failed — official Log channels unavailable");
					return;
				}

				TryAddChannel("android", "android.log", timestamped: true);
				TryAddChannel("debug", "debug.log", timestamped: false);
				TryAddChannel("graphics", "graphics.log", timestamped: false);
				TryAddChannel("sound", "sound.log", timestamped: false);
				TryAddChannel("perf", "perf.log", timestamped: false);
				TryAddChannel("client", "client.log", timestamped: false);
				TryAddChannel("server", "server.log", timestamped: true);

				AndroidPlatformLog.MarkEngineLogReady();

				AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
				AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

				try
				{
					Log.Write("android", "Official logging ready SupportDir=" + dir);
					Log.Write("debug", "Android bootstrap logging channels open");
				}
				catch (Exception e)
				{
					BootLog.Warn("Log.Write after AddChannel: " + e.Message);
				}

				System.Threading.Interlocked.Exchange(ref officialLoggingInited, 1);
				BootLog.Info("InitOfficialLogging complete");
			}
			catch (Exception e)
			{
				try { BootLog.Exception("InitOfficialLogging", e); }
				catch { /* ignore */ }
				try { AndroidFileLog.Warn("OpenRA.Bootstrap", "InitOfficialLogging: " + e.Message); }
				catch { /* ignore */ }
			}
		}


		/// <summary>
		/// Set Platform support paths to <paramref name="dir"/> without relying on
		/// SpecialFolder.UserProfile (null on Android).
		/// Preferred path: Platform.OverrideSupportDir. Fallback: reflect private fields
		/// (see patches/Platform.AndroidSupportDir.fragment.cs for the proper fork fix).
		/// </summary>
		static bool ForceSupportDir(string dir)
		{
			try
			{
				Platform.OverrideSupportDir(dir);
				BootLog.Info("Platform.OverrideSupportDir OK");
				return true;
			}
			catch (InvalidOperationException ioe)
			{
				// Already initialized (second Start) — assume previous path is fine.
				BootLog.Warn("OverrideSupportDir: " + ioe.Message);
				return true;
			}
			catch (Exception e)
			{
				BootLog.Warn("OverrideSupportDir failed (expected on unpatched Android): " + e.Message);
			}

			try
			{
				var t = typeof(Platform);
				const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;

				// Match OverrideSupportDir: trailing separator, absolute path
				if (dir[^1] != Path.DirectorySeparatorChar && dir[^1] != Path.AltDirectorySeparatorChar)
					dir += Path.DirectorySeparatorChar;
				dir = Path.GetFullPath(dir);

				void Set(string name, object value)
				{
					var f = t.GetField(name, flags);
					if (f == null)
						throw new MissingFieldException(t.FullName, name);
					f.SetValue(null, value);
				}

				Set("systemSupportPath", dir);
				Set("legacyUserSupportPath", dir);
				Set("modernUserSupportPath", dir);
				Set("userSupportPath", dir);
				Set("supportDirInitialized", true);

				// Sanity: reading SupportDir must not throw and must match.
				var got = Platform.SupportDir;
				BootLog.Info("ForceSupportDir via reflection OK SupportDir=" + got);
				if (!string.Equals(Path.GetFullPath(got), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)
				    && !got.StartsWith(dir.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
				{
					BootLog.Warn("ForceSupportDir path mismatch got=" + got + " want=" + dir);
				}

				return true;
			}
			catch (Exception e)
			{
				BootLog.Error("ForceSupportDir reflection failed: " + e);
				return false;
			}
		}

		static void TryAddChannel(string name, string file, bool timestamped)
		{
			try
			{
				Log.AddChannel(name, file, timestamped);
				BootLog.Info("AddChannel " + name + " → " + file);
			}
			catch (Exception e)
			{
				BootLog.Warn("AddChannel " + name + ": " + e.Message);
				try { AndroidFileLog.Warn("OpenRA.Bootstrap", "AddChannel " + name + ": " + e.Message); }
				catch { /* ignore */ }
			}
		}

		static void OnUnhandled(object sender, UnhandledExceptionEventArgs args)
		{
			try
			{
				var ex = args.ExceptionObject as Exception;
				var text = ex != null
					? ex.ToString()
					: (args.ExceptionObject != null ? args.ExceptionObject.ToString() : "unknown");
				var line = "UnhandledException isTerminating=" + args.IsTerminating + " " + text;
				try { Log.Write("debug", line); } catch { /* ignore */ }
				AndroidPlatformLog.Error("OpenRA.Crash", line);
				AndroidFileLog.Flush();
			}
			catch { /* last resort */ }
		}


		static bool EnsureEglCurrent()
		{
			if (AndroidEgl.MakeCurrent())
			{
				AndroidFileLog.Info("OpenRA.Bootstrap", "EGL MakeCurrent ok");
				return true;
			}

			AndroidFileLog.Warn("OpenRA.Bootstrap", "EGL MakeCurrent failed: " + AndroidEgl.LastError);

			var surface = boundSurface;
			if (surface != null)
			{
				AndroidFileLog.Info("OpenRA.Bootstrap", "Re-initializing EGL from surface…");
				try
				{
					var holder = surface.Holder;
					var w = Math.Max(1, surface.SurfaceWidth);
					var h = Math.Max(1, surface.SurfaceHeight);
					if (AndroidEgl.Initialize(holder, w, h) && AndroidEgl.MakeCurrent())
					{
						AndroidFileLog.Info("OpenRA.Bootstrap", "EGL re-init + MakeCurrent ok");
						return true;
					}
					AndroidFileLog.Error("OpenRA.Bootstrap", "EGL re-init failed: " + AndroidEgl.LastError);
				}
				catch (Exception e)
				{
					AndroidFileLog.Exception("OpenRA.Bootstrap.EGL", e);
				}
			}

			return false;
		}

		public static void Stop()
		{
			try { Game.Exit(); }
			catch (Exception e) { AndroidFileLog.Exception("OpenRA.Bootstrap.Stop", e); }
			IsRunning = false;
			MainActivity.PlatformWindow = null;
		}
	}
}