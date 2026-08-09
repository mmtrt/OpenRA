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
using System.IO;
using OpenRA.FileFormats;
using OpenRA.FileSystem;
using OpenRA.Mods.Common.FileSystem;
using OpenRA.Mods.Common.Widgets.Logic;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.LoadScreens
{
	public class BlankLoadScreen : ILoadScreen
	{
		public LaunchArguments Launch;
		protected IReadOnlyFileSystem fileSystem;
		bool initialized;

		public virtual void Init(Manifest manifest, IReadOnlyFileSystem fileSystem)
		{
			this.fileSystem = fileSystem;
		}

		public virtual void Display()
		{
			if (Game.Renderer == null || initialized)
				return;

			// Draw a black screen
			Game.Renderer.BeginUI();
			Game.Renderer.EndFrame(new NullInputHandler());

			// PERF: draw the screen only once
			initialized = true;
		}

		public virtual void StartGame(Arguments args)
		{
			Launch = new LaunchArguments(args);
			Ui.ResetAll();
			Game.Settings.Save();

			if (!string.IsNullOrEmpty(Launch.Benchmark))
			{
				Console.WriteLine($"Saving benchmark data into {Path.Combine(Platform.SupportDir, "Logs")}");

				Game.BenchmarkMode(Launch.Benchmark);
			}

			// Join a server directly
			var connect = Launch.GetConnectEndPoint();
			if (connect != null)
			{
				Game.LoadShellMap();
				Game.RemoteDirectConnect(connect);
				return;
			}

			// Start a map directly
			if (!string.IsNullOrEmpty(Launch.Map))
			{
				Game.LoadMap(Launch.Map);
				return;
			}

			// Load a replay directly
			if (!string.IsNullOrEmpty(Launch.Replay))
			{
				ReplayMetadata replayMeta = null;
				try
				{
					replayMeta = ReplayMetadata.Read(Launch.Replay);
				}
				catch { }

				if (ReplayUtils.PromptConfirmReplayCompatibility(replayMeta, Game.ModData, Game.LoadShellMap))
					Game.JoinReplay(Launch.Replay);

				if (replayMeta != null)
				{
					var modID = replayMeta.GameInfo.Mod;
					if (modID != null && modID != Game.ModData.Manifest.Id && Game.Mods.TryGetValue(modID, out var mod))
						Game.InitializeMod(mod, args);
				}

				return;
			}

			Game.LoadShellMap();
			Game.Settings.Save();
		}

		protected virtual void Dispose(bool disposing) { }

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		public virtual bool BeforeLoad(ModData modData)
		{
			var graphicSettings = Game.Settings.Graphics;

			// Reset the UI scaling if the user has configured a UI scale that pushes us below
			// the minimum allowed effective resolution.
			//
			// ANDROID: the panel size is fixed (e.g. 2400x1080). UIScale intentionally shrinks
			// EffectiveWindowSize (Resolution) — at 1.75× height becomes ~617px which is below
			// MinEffectiveResolution height (often 720). The desktop safety clamp then forced
			// UIScale back to 1.0 and Settings.Save() stripped it from settings.yaml.
			// Skip the auto-reset on Android so player-chosen scales persist.
			var minResolution = modData.GetOrCreate<WorldViewportSizes>().MinEffectiveResolution;
			var resolution = Game.Renderer.Resolution;
			var isAndroid =
#if ANDROID
				true;
#else
				false;
#endif
			try { isAndroid = isAndroid || Platform.CurrentPlatform.ToString() == "Android"; }
			catch { /* older trees */ }

			if (!isAndroid
				&& (resolution.Width < minResolution.Width || resolution.Height < minResolution.Height)
				&& Game.Settings.Graphics.UIScale > 1.0f)
			{
				graphicSettings.UIScale = 1.0f;
				Game.Renderer.SetUIScale(1.0f);
			}

			// Saved settings may have been invalidated by a hardware change
			graphicSettings.VideoDisplay = Game.Renderer.CurrentDisplay;
			if (graphicSettings.GLProfile != GLProfile.Automatic && graphicSettings.GLProfile != Game.Renderer.GLProfile)
				graphicSettings.GLProfile = GLProfile.Automatic;

			if (modData.FileSystemLoader is not IFileSystemExternalContent content)
				return true;

			return !content.InstallContentIfRequired(modData);
		}
	}
}
