// Single source of truth for per-mod Android launchers (AppImage-style naming).

using System;
using AColor = global::Android.Graphics.Color;

namespace OpenRA.Android
{
	/// <summary>
	/// Official AppImage names: OpenRA-Red-Alert, OpenRA-Tiberian-Dawn, OpenRA-Dune-2000.
	/// TS uses the same scheme (experimental upstream mod).
	/// </summary>
	public sealed class ModInfo
	{
		public string Id { get; init; }                 // ra, cnc, d2k, ts
		public string DisplayName { get; init; }         // Red Alert
		public string AppImageName { get; init; }        // OpenRA-Red-Alert
		public string ApplicationLabel { get; init; }    // OpenRA Red Alert
		public string MirrorListUrl { get; init; }
		/// <summary>Used when the official mirrors.txt is missing (404) or empty.</summary>
		public string[] FallbackPackageUrls { get; init; } = Array.Empty<string>();
		public string ContentRelativeDir { get; init; }  // Content/ra/v2
		public string[] MarkerFiles { get; init; }       // relative to SupportDir
		public int ThemePrimary { get; init; }           // ARGB card accent
		public int ThemeBackground { get; init; }        // ARGB card fill
		public int ThemeTitle { get; init; }
		public int ThemeBody { get; init; }
		public string InstallBlurb { get; init; }

		public static ModInfo Current => FromId(BuildConfig.ModId);

		public static ModInfo FromId(string id)
		{
			id = (id ?? "ra").Trim().ToLowerInvariant();
			return id switch
			{
				"cnc" => Cnc,
				"d2k" => D2k,
				"ts" => Ts,
				_ => Ra
			};
		}

		public static readonly ModInfo Ra = new()
		{
			Id = "ra",
			DisplayName = "Red Alert",
			AppImageName = "OpenRA-Red-Alert",
			ApplicationLabel = "OpenRA Red Alert",
			MirrorListUrl = "https://www.openra.net/packages/ra-quickinstall-mirrors.txt",
			FallbackPackageUrls = new[]
			{
				"https://cdn.mailaender.name/openra/ra-quickinstall.zip",
				"https://republic.community/hosted/files/command-and-conquer/openra/ra-quickinstall.zip",
				"https://openra.0x47.net/ra-quickinstall.zip",
			},
			ContentRelativeDir = "Content/ra/v2",
			MarkerFiles = new[]
			{
				"Content/ra/v2/allies.mix", "Content/ra/v2/conquer.mix", "Content/ra/v2/hires.mix",
				"Content/ra/v2/lores.mix", "Content/ra/v2/local.mix", "Content/ra/v2/speech.mix",
				"Content/ra/v2/sounds.mix", "Content/ra/v2/temperat.mix", "Content/ra/v2/snow.mix",
			},
			ThemePrimary = unchecked((int)0xFF902818),
			ThemeBackground = unchecked((int)0xDC1A1C14),
			ThemeTitle = unchecked((int)0xFFE8D8A8),
			ThemeBody = unchecked((int)0xFFC8C0A8),
			InstallBlurb =
				"Red Alert requires artwork and audio from the original game.\n\n" +
				"Quick Install will automatically download this content (without music " +
				"or videos) from a mirror of the 2008 Red Alert freeware release.\n\n" +
				"Advanced Install includes options for copying the music, videos, and " +
				"other content from an original game disc or digital installation."
		};

		public static readonly ModInfo Cnc = new()
		{
			Id = "cnc",
			DisplayName = "Tiberian Dawn",
			AppImageName = "OpenRA-Tiberian-Dawn",
			ApplicationLabel = "OpenRA Tiberian Dawn",
			// Official: mods/cnc-content/installer/downloads.yaml → cnc-mirrors.txt / cnc-packages.zip
			MirrorListUrl = "https://www.openra.net/packages/cnc-mirrors.txt",
			FallbackPackageUrls = new[]
			{
				"https://cdn.mailaender.name/openra/cnc-packages.zip",
				"https://republic.community/hosted/files/command-and-conquer/openra/cnc-packages.zip",
				"https://openra.0x47.net/cnc-packages.zip",
				"https://openra.ppmsite.com/cnc-packages.zip",
			},
			ContentRelativeDir = "Content/cnc",
			MarkerFiles = new[]
			{
				"Content/cnc/conquer.mix", "Content/cnc/desert.mix", "Content/cnc/general.mix",
				"Content/cnc/sounds.mix", "Content/cnc/speech.mix", "Content/cnc/temperat.mix",
				"Content/cnc/tempicnh.mix", "Content/cnc/transit.mix", "Content/cnc/winter.mix",
			},
			// TD main-menu red chrome
			ThemePrimary = unchecked((int)0xFFB02820),
			ThemeBackground = unchecked((int)0xDC1A1010),
			ThemeTitle = unchecked((int)0xFFE8C0A8),
			ThemeBody = unchecked((int)0xFFC8A898),
			InstallBlurb =
				"Tiberian Dawn requires artwork and audio from the original game.\n\n" +
				"Quick Install will automatically download this content (without music " +
				"or videos) from a mirror of the 2007 C&C Gold freeware release.\n\n" +
				"Advanced Install includes options for copying the music, videos, and " +
				"other content from an original game disc or digital installation."
		};

		public static readonly ModInfo D2k = new()
		{
			Id = "d2k",
			DisplayName = "Dune 2000",
			AppImageName = "OpenRA-Dune-2000",
			ApplicationLabel = "OpenRA Dune 2000",
			// Official: d2k-quickinstall-v3 → Content/d2k/v3/*.R16 (not legacy v2/R8)
			MirrorListUrl = "https://www.openra.net/packages/d2k-quickinstall-v3-mirrors.txt",
			FallbackPackageUrls = new[]
			{
				"https://cdn.mailaender.name/openra/d2k-quickinstall-v3.zip",
				"https://openra.ppmsite.com/d2k-quickinstall-v3.zip",
				"https://openra.0x47.net/d2k-quickinstall-v3.zip",
				"https://openra.baxxster.no/openra/d2k-v3/d2k-quickinstall-v3.zip",
			},
			// Zip root is v3/… — extract into Content/d2k → Content/d2k/v3/…
			ContentRelativeDir = "Content/d2k",
			MarkerFiles = new[]
			{
				"Content/d2k/v3/DATA.R16", "Content/d2k/v3/BLOXBASE.R16",
				"Content/d2k/v3/MOUSE.R16", "Content/d2k/v3/PALETTE.BIN",
			},
			ThemePrimary = unchecked((int)0xFFB08830),
			ThemeBackground = unchecked((int)0xDC1C1810),
			ThemeTitle = unchecked((int)0xFFE8D8A0),
			ThemeBody = unchecked((int)0xFFC8B890),
			InstallBlurb =
				"Dune 2000 requires artwork and audio from the original game.\n\n" +
				"Quick Install will automatically download this content (without music " +
				"or videos) from an online mirror of the game files.\n\n" +
				"Advanced Install includes options for copying the music, videos, and " +
				"other content from an original game disc."
		};

		public static readonly ModInfo Ts = new()
		{
			Id = "ts",
			DisplayName = "Tiberian Sun",
			AppImageName = "OpenRA-Tiberian-Sun",
			ApplicationLabel = "OpenRA Tiberian Sun",
			MirrorListUrl = "https://www.openra.net/packages/ts-quickinstall-mirrors.txt",
			FallbackPackageUrls = new[]
			{
				"https://cdn.mailaender.name/openra/ts-quickinstall.zip",
				"https://republic.community/hosted/files/command-and-conquer/openra/ts-quickinstall.zip",
				"https://openra.0x47.net/ts-quickinstall.zip",
			},
			ContentRelativeDir = "Content/ts",
			MarkerFiles = new[]
			{
				"Content/ts/sidenc01.mix", "Content/ts/tibsun.mix",
			},
			// Former CNC install green (Tiberian lineage)
			ThemePrimary = unchecked((int)0xFF3D7A28),
			ThemeBackground = unchecked((int)0xDC121A10),
			ThemeTitle = unchecked((int)0xFFD8E8A8),
			ThemeBody = unchecked((int)0xFFB8C8A0),
			InstallBlurb =
				"Tiberian Sun (experimental) requires artwork and audio from the original game.\n\n" +
				"Quick Install downloads the freeware content pack when mirrors are available."
		};

		public AColor PrimaryColor => new(ThemePrimary);
		public AColor BackgroundColor => new(ThemeBackground);
		public AColor TitleColor => new(ThemeTitle);
		public AColor BodyColor => new(ThemeBody);
	}
}
