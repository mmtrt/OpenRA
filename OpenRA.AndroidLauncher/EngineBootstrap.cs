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
				ContentProbe.LogInventory(SupportDir);

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
				"OpenRA.Mods.D2k.dll",
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
				// Pin/merge UIScale BEFORE building args so yaml + argv agree.
				WriteAndroidSettings(SupportDir);
				var uiScale = ReadPinnedUIScale(SupportDir);
				if (uiScale > 1.0001f)
					MergeUIScaleIntoSettingsYaml(SupportDir, uiScale);

				var argsList = new System.Collections.Generic.List<string>
				{
					"Engine.Platform=Android",
					"Game.Mod=" + mod,
					"Engine.EngineDir=" + SupportDir,
					"Engine.ModSearchPaths=" + ContentBootstrap.ModsDir,
					"Graphics.DisableHardwareCursors=True",
					"Graphics.GLProfile=Embedded"
				};
				// Arguments override settings.yaml — survives Save() stripping defaults mid-session.
				if (uiScale >= 1f && uiScale <= 3f)
				{
					argsList.Add("Graphics.UIScale=" + uiScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
					AndroidFileLog.Info("OpenRA.Bootstrap", "Launch Graphics.UIScale=" + uiScale);
				}
				var args = argsList.ToArray();
				try { Game.HideCursor = true; } catch { /* older builds */ }

				// Eluant may load here — ensure DllImportResolver is on every assembly in the default ALC.
				AndroidNativeBootstrap.AttachResolversToLoadedAssemblies();
				AndroidFileLog.Info("OpenRA.Bootstrap", "InitializeAndRun " + string.Join(" ", args)
					+ " surface=" + AndroidEgl.SurfaceWidth + "x" + AndroidEgl.SurfaceHeight);
				Game.InitializeAndRun(args);
				AndroidFileLog.Info("OpenRA.Bootstrap", "InitializeAndRun returned (Quit/Exit)");
			}
			catch (Exception e)
			{
				AndroidFileLog.Exception("OpenRA.Bootstrap", e);
			}
			finally
			{
				IsRunning = false;
				AndroidFileLog.Info("OpenRA.Bootstrap", "Game thread exit");
				// Main menu Quit → Game.Exit() ends the run loop; close the Android app.
				try { RequestAndroidAppExit(); }
				catch (Exception ex) { AndroidFileLog.Warn("OpenRA.Bootstrap", "RequestAndroidAppExit: " + ex.Message); }
			}
		}



		const string UIScalePinFile = "android-uiscale";

		/// <summary>
		/// UIScale above 1.0 was vanishing from settings.yaml on relaunch: Settings.Save()
		/// omits fields that equal their defaults, and something was resetting Graphics.UIScale
		/// to 1.0 before Save. Pin the value next to settings.yaml and always re-apply via
		/// Engine args (Arguments override yaml) + merge back into settings.yaml on boot.
		/// </summary>
		static float ReadPinnedUIScale(string supportDir)
		{
			float fromYaml = 1f, fromPin = 1f;
			try
			{
				var path = Path.Combine(supportDir, "settings.yaml");
				if (File.Exists(path))
				{
					foreach (var raw in File.ReadAllLines(path))
					{
						var line = raw.Trim();
						if (line.StartsWith("#", StringComparison.Ordinal))
							continue;
						if (line.StartsWith("UIScale:", StringComparison.OrdinalIgnoreCase))
						{
							var v = line.Substring("UIScale:".Length).Trim().Trim('"');
							if (float.TryParse(v,
								System.Globalization.NumberStyles.Float,
								System.Globalization.CultureInfo.InvariantCulture,
								out var yamlScale) && yamlScale >= 1f && yamlScale <= 3f)
							{
								fromYaml = yamlScale;
								break;
							}
						}
					}
				}
			}
			catch { /* ignore */ }

			try
			{
				var pin = Path.Combine(supportDir, UIScalePinFile);
				if (File.Exists(pin) && float.TryParse(
					File.ReadAllText(pin).Trim(),
					System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture,
					out var pinned) && pinned >= 1f && pinned <= 3f)
					fromPin = pinned;
			}
			catch { /* ignore */ }

			// Prefer the higher value so a stale pin cannot force 1.0 over a good yaml,
			// and a stripped yaml still recovers from pin.
			var chosen = Math.Max(fromYaml, fromPin);
			// First-run default when neither yaml nor pin has a value yet
			if (chosen <= 1.0001f && fromYaml <= 1.0001f && fromPin <= 1.0001f)
				return 1.75f;
			return chosen;
		}

		static void WritePinnedUIScale(string supportDir, float scale)
		{
			try
			{
				if (scale < 1f || scale > 3f)
					return;
				var pin = Path.Combine(supportDir, UIScalePinFile);
				File.WriteAllText(pin, scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Bootstrap", "WritePinnedUIScale: " + e.Message);
			}
		}

		/// <summary>
		/// Ensure settings.yaml Graphics.UIScale matches the pinned value (re-insert if Save stripped it).
		/// </summary>
		static void MergeUIScaleIntoSettingsYaml(string supportDir, float scale)
		{
			try
			{
				if (scale <= 1.0001f)
					return; // default omitted by design
				var path = Path.Combine(supportDir, "settings.yaml");
				if (!File.Exists(path))
					return;
				var text = File.ReadAllText(path);
				var inv = System.Globalization.CultureInfo.InvariantCulture;
				var scaleStr = scale.ToString("0.###", inv);
				if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?im)^\s*UIScale\s*:"))
				{
					text = System.Text.RegularExpressions.Regex.Replace(
						text,
						@"(?im)^(\s*)UIScale\s*:.*$",
						m => m.Groups[1].Value + "UIScale: " + scaleStr);
				}
				else if (text.Contains("Graphics:"))
				{
					text = text.Replace(
						"Graphics:",
						"Graphics:" + Environment.NewLine + "\tUIScale: " + scaleStr);
				}
				else
				{
					text += Environment.NewLine + "Graphics:" + Environment.NewLine + "\tUIScale: " + scaleStr + Environment.NewLine;
				}
				File.WriteAllText(path, text);
				WritePinnedUIScale(supportDir, scale);
				AndroidFileLog.Info("OpenRA.Bootstrap", "Merged UIScale=" + scaleStr + " into settings.yaml");
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Bootstrap", "MergeUIScale: " + e.Message);
			}
		}

		static void WriteAndroidSettings(string supportDir)
		{
			try
			{
				var path = Path.Combine(supportDir, "settings.yaml");
				var w = Math.Max(1, AndroidEgl.SurfaceWidth);
				var h = Math.Max(1, AndroidEgl.SurfaceHeight);

				// CRITICAL: never overwrite an existing settings.yaml — that wiped every
				// in-game Settings change on the next launch (player name, UIScale, etc.).
				// OpenRA Settings.Save() writes this file; we only seed defaults once.
				if (File.Exists(path))
				{
					EnsureAndroidRequiredSettings(path, w, h);
					var scale = ReadPinnedUIScale(supportDir);
					if (scale > 1.0001f)
					{
						WritePinnedUIScale(supportDir, scale);
						MergeUIScaleIntoSettingsYaml(supportDir, scale);
					}
					try
					{
						var existing = File.ReadAllText(path);
						var hasScale = existing.IndexOf("UIScale", StringComparison.OrdinalIgnoreCase) >= 0;
						AndroidFileLog.Info("OpenRA.Bootstrap",
							"settings.yaml preserved path=" + path
							+ " hasUIScale=" + hasScale
							+ " pinnedScale=" + scale
							+ " len=" + existing.Length);
					}
					catch
					{
						AndroidFileLog.Info("OpenRA.Bootstrap", "settings.yaml preserved (exists) " + path);
					}
					return;
				}

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
					tab + "UIScale: 1.75" + nl +
					tab + "VSync: true" + nl +
					"Sound:" + nl +
					tab + "Device: " + nl +  // empty = default OpenAL device
					"Game:" + nl +
					tab + "MouseControlStyle: Touch" + nl;  // Android-only default control scheme
				// Battlefield camera Close is Graphics.ViewportDistance (OpenRA enum WorldViewport)
				yaml = yaml.Replace(
					tab + "UIScale: 1.75" + nl,
					tab + "UIScale: 1.75" + nl +
					tab + "ViewportDistance: Close" + nl);
				File.WriteAllText(path, yaml);
				AndroidFileLog.Info("OpenRA.Bootstrap",
					"Seeded settings.yaml Touch + UIScale 1.75 + ViewportDistance Close " + w + "x" + h);
				WritePinnedUIScale(supportDir, 1.75f);
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Bootstrap", "settings.yaml: " + e.Message);
			}
		}

		/// <summary>
		/// Patch only Android-required keys if missing; do not clobber user values.
		/// </summary>
		static void EnsureAndroidRequiredSettings(string path, int w, int h)
		{
			try
			{
				var text = File.ReadAllText(path);
				var changed = false;
				if (text.IndexOf("DisableHardwareCursors", StringComparison.Ordinal) < 0)
				{
					// Insert under Graphics: if present, else append
					if (text.Contains("Graphics:"))
						text = text.Replace("Graphics:", "Graphics:" + Environment.NewLine + "	DisableHardwareCursors: true");
					else
						text += Environment.NewLine + "Graphics:" + Environment.NewLine + "	DisableHardwareCursors: true" + Environment.NewLine;
					changed = true;
				}
				if (text.IndexOf("GLProfile", StringComparison.Ordinal) < 0)
				{
					if (text.Contains("Graphics:"))
						text = text.Replace("Graphics:", "Graphics:" + Environment.NewLine + "	GLProfile: Embedded");
					else
						text += Environment.NewLine + "Graphics:" + Environment.NewLine + "	GLProfile: Embedded" + Environment.NewLine;
					changed = true;
				}
				// Prefer real OpenAL over the old forced "Null" seed.
				if (text.IndexOf("Device: Null", StringComparison.Ordinal) >= 0)
				{
					text = text.Replace("Device: Null", "Device:");
					changed = true;
					AndroidFileLog.Info("OpenRA.Bootstrap", "Migrated Sound.Device Null → default (OpenAL)");
				}
				// Scheme rename: RustedWarfare → Touch (Classic-based mobile gestures)
				if (text.IndexOf("MouseControlStyle: RustedWarfare", StringComparison.Ordinal) >= 0)
				{
					text = text.Replace("MouseControlStyle: RustedWarfare", "MouseControlStyle: Touch");
					changed = true;
					AndroidFileLog.Info("OpenRA.Bootstrap", "Migrated MouseControlStyle RustedWarfare → Touch");
				}
				// Android defaults when keys never set (do not override user choices)
				if (text.IndexOf("MouseControlStyle", StringComparison.OrdinalIgnoreCase) < 0)
				{
					if (text.Contains("Game:"))
						text = text.Replace("Game:", "Game:" + Environment.NewLine + "	MouseControlStyle: Touch");
					else
						text += Environment.NewLine + "Game:" + Environment.NewLine + "	MouseControlStyle: Touch" + Environment.NewLine;
					changed = true;
					AndroidFileLog.Info("OpenRA.Bootstrap", "Defaulted MouseControlStyle → Touch");
				}
				if (text.IndexOf("ViewportDistance", StringComparison.OrdinalIgnoreCase) < 0)
				{
					if (text.Contains("Graphics:"))
						text = text.Replace("Graphics:", "Graphics:" + Environment.NewLine + "	ViewportDistance: Close");
					else
						text += Environment.NewLine + "Graphics:" + Environment.NewLine + "	ViewportDistance: Close" + Environment.NewLine;
					changed = true;
					AndroidFileLog.Info("OpenRA.Bootstrap", "Defaulted ViewportDistance → Close");
				}

				if (changed)
				{
					File.WriteAllText(path, text);
					AndroidFileLog.Info("OpenRA.Bootstrap", "Patched required Android keys into settings.yaml");
				}
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Bootstrap", "EnsureAndroidRequiredSettings: " + e.Message);
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

				// NEVER use timestamped:true before Game.Initialize — Log.WriteValue does
				// Game.Settings.Server.TimestampFormat and Settings is still null → NRE kills
				// the logging thread (UnhandledException isTerminating=True).
				TryAddChannel("android", "android.log", timestamped: false);
				TryAddChannel("debug", "debug.log", timestamped: false);
				TryAddChannel("graphics", "graphics.log", timestamped: false);
				TryAddChannel("sound", "sound.log", timestamped: false);
				TryAddChannel("perf", "perf.log", timestamped: false);
				TryAddChannel("client", "client.log", timestamped: false);
				TryAddChannel("server", "server.log", timestamped: false);

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

		
		/// <summary>
		/// Main-menu Quit calls Game.Exit() which ends InitializeAndRun. Close the activity
		/// and leave the process so the user is not left on a frozen SurfaceView.
		/// </summary>
		static void RequestAndroidAppExit()
		{
			var act = MainActivity.Current;
			if (act == null)
			{
				AndroidFileLog.Warn("OpenRA.Bootstrap", "RequestAndroidAppExit: no activity");
				return;
			}

			act.RunOnUiThread(() =>
			{
				try
				{
					AndroidFileLog.Info("OpenRA.Bootstrap", "Finishing activity after Game.Exit");
					act.FinishAffinity();
				}
				catch (Exception e)
				{
					AndroidFileLog.Warn("OpenRA.Bootstrap", "FinishAffinity: " + e.Message);
				}

				try
				{
					global::Java.Lang.JavaSystem.Exit(0);
				}
				catch
				{
					try { global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid()); }
					catch { /* ignore */ }
				}
			});
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