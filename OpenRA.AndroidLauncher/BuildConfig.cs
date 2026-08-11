// Compile-time mod selection (set via -p:OpenRAMod=ra|cnc|d2k|ts).

namespace OpenRA.Android
{
	public static class BuildConfig
	{
#if OPENRA_MOD_CNC
		public const string ModId = "cnc";
#elif OPENRA_MOD_D2K
		public const string ModId = "d2k";
#elif OPENRA_MOD_TS
		public const string ModId = "ts";
#else
		public const string ModId = "ra";
#endif
	}
}
