// Detects whether mod content under SupportDir is present for the built launcher mod.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenRA.Android
{
	public static class ContentProbe
	{
		public static ModInfo Mod => ModInfo.Current;

		public static string ContentRoot(string supportDir)
			=> Path.Combine(supportDir, Mod.ContentRelativeDir.Replace('/', Path.DirectorySeparatorChar));

		public static bool IsBaseContentInstalled(string supportDir)
		{
			if (string.IsNullOrEmpty(supportDir))
				return false;
			if (Mod.MarkerFiles.Length > 0
			    && Mod.MarkerFiles.All(rel =>
			    {
				    var path = Path.Combine(supportDir, rel.Replace('/', Path.DirectorySeparatorChar));
				    return File.Exists(path) && new FileInfo(path).Length > 0;
			    }))
				return true;

			// Fallback after quickinstall layout drift: content dir with several non-empty files
			var root = ContentRoot(supportDir);
			if (!Directory.Exists(root))
				return false;
			var n = 0;
			foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
			{
				if (new FileInfo(f).Length > 0 && ++n >= 5)
					return true;
			}
			return false;
		}

		/// <summary>Alias for full check — marker set is the required quickinstall baseline.</summary>
		public static bool IsFullRequiredContentInstalled(string supportDir)
			=> IsBaseContentInstalled(supportDir);

		public static string MissingSummary(string supportDir)
		{
			var missing = Mod.MarkerFiles
				.Where(rel =>
				{
					var path = Path.Combine(supportDir, rel.Replace('/', Path.DirectorySeparatorChar));
					return !File.Exists(path) || new FileInfo(path).Length == 0;
				})
				.Select(Path.GetFileName)
				.ToArray();
			return missing.Length == 0 ? "ok" : string.Join(", ", missing);
		}

		public static void LogInventory(string supportDir)
		{
			try
			{
				var root = ContentRoot(supportDir);
				if (!Directory.Exists(root))
				{
					AndroidFileLog.Warn("OpenRA.Content", "No content at " + root);
					return;
				}

				var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
				AndroidFileLog.Info("OpenRA.Content",
					"mod=" + Mod.Id + " files=" + files.Length
					+ " installed=" + IsBaseContentInstalled(supportDir)
					+ " missing=" + MissingSummary(supportDir));
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Content", "LogInventory: " + e.Message);
			}
		}
	}
}
