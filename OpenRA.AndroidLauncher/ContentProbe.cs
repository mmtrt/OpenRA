// Detects whether Red Alert content under SupportDir/Content/ra/v2 satisfies the engine.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenRA.Android
{
	public static class ContentProbe
	{
		/// <summary>Base mixes — ContentPackage@base TestFiles.</summary>
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

		/// <summary>Aftermath + desert — required by ra mod.yaml ContentPackages / RequiredContentFiles.</summary>
		public static readonly string[] RequiredExpansionFiles =
		{
			"Content/ra/v2/expand/expand2.mix",
			"Content/ra/v2/expand/hires1.mix",
			"Content/ra/v2/expand/lores1.mix",
			"Content/ra/v2/cnc/desert.mix",
			// Sample of RequiredContentFiles (.aud) — if these exist, expand/ mounted content is present.
			"Content/ra/v2/expand/chrotnk1.aud",
			"Content/ra/v2/expand/fixit1.aud",
			"Content/ra/v2/expand/jyes1.aud",
		};

		public static string ContentRaV2(string supportDir)
			=> Path.Combine(supportDir, "Content", "ra", "v2");

		public static bool IsBaseContentInstalled(string supportDir)
			=> AllExist(supportDir, RequiredBaseFiles);

		/// <summary>True when base + required expansion files are on disk (engine should not force ra-content).</summary>
		public static bool IsFullRequiredContentInstalled(string supportDir)
			=> AllExist(supportDir, RequiredBaseFiles) && AllExist(supportDir, RequiredExpansionFiles);

		public static string MissingSummary(string supportDir)
		{
			var missing = Missing(supportDir, RequiredBaseFiles)
				.Concat(Missing(supportDir, RequiredExpansionFiles))
				.ToArray();
			return missing.Length == 0 ? "ok" : string.Join(", ", missing.Select(Path.GetFileName));
		}

		public static void LogInventory(string supportDir)
		{
			try
			{
				var root = ContentRaV2(supportDir);
				if (!Directory.Exists(root))
				{
					AndroidFileLog.Warn("OpenRA.Content", "No Content/ra/v2 at " + root);
					return;
				}

				var mixes = Directory.GetFiles(root, "*.mix", SearchOption.AllDirectories);
				var auds = Directory.GetFiles(root, "*.aud", SearchOption.AllDirectories);
				AndroidFileLog.Info("OpenRA.Content",
					"Inventory mix=" + mixes.Length + " aud=" + auds.Length
					+ " fullRequired=" + IsFullRequiredContentInstalled(supportDir)
					+ " missing=" + MissingSummary(supportDir));
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Content", "LogInventory: " + e.Message);
			}
		}

		static bool AllExist(string supportDir, IEnumerable<string> rels)
		{
			if (string.IsNullOrEmpty(supportDir))
				return false;
			return rels.All(rel =>
			{
				var path = Path.Combine(supportDir, rel.Replace('/', Path.DirectorySeparatorChar));
				return File.Exists(path) && new FileInfo(path).Length > 0;
			});
		}

		static IEnumerable<string> Missing(string supportDir, IEnumerable<string> rels)
		{
			foreach (var rel in rels)
			{
				var path = Path.Combine(supportDir, rel.Replace('/', Path.DirectorySeparatorChar));
				if (!File.Exists(path) || new FileInfo(path).Length == 0)
					yield return rel;
			}
		}
	}
}
