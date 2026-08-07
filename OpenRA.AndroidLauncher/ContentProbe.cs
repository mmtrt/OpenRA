// Detects whether Red Alert base content is installed under SupportDir/Content/ra/v2.

using System.IO;
using System.Linq;

namespace OpenRA.Android
{
	public static class ContentProbe
	{
		/// <summary>Relative to SupportDir — matches OpenRA ra-content TestFiles.</summary>
		public static readonly string[] RequiredBaseFiles =
		{
			"Content/ra/v2/allies.mix",
			"Content/ra/v2/conquer.mix",
			"Content/ra/v2/interior.mix",
			"Content/ra/v2/hires.mix",
			"Content/ra/v2/lores.mix",
			"Content/ra/v2/local.mix",
			"Content/ra/v2/speech.mix",
			"Content/ra/v2/russian.mix",
			"Content/ra/v2/snow.mix",
			"Content/ra/v2/sounds.mix",
			"Content/ra/v2/temperat.mix",
		};

		public static string ContentRaV2(string supportDir)
			=> Path.Combine(supportDir, "Content", "ra", "v2");

		public static bool IsBaseContentInstalled(string supportDir)
		{
			if (string.IsNullOrEmpty(supportDir))
				return false;

			return RequiredBaseFiles.All(rel =>
			{
				var path = Path.Combine(supportDir, rel.Replace('/', Path.DirectorySeparatorChar));
				return File.Exists(path) && new FileInfo(path).Length > 0;
			});
		}

		public static string MissingSummary(string supportDir)
		{
			var missing = RequiredBaseFiles
				.Where(rel =>
				{
					var path = Path.Combine(supportDir, rel.Replace('/', Path.DirectorySeparatorChar));
					return !File.Exists(path) || new FileInfo(path).Length == 0;
				})
				.ToArray();

			return missing.Length == 0
				? "ok"
				: string.Join(", ", missing.Select(Path.GetFileName));
		}
	}
}
