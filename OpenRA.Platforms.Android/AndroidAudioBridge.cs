#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Bridge so the launcher can pause/resume audio without referencing game sound types.
 */
#endregion

using System;

namespace OpenRA.Platforms.Android
{
	/// <summary>
	/// Registered by <see cref="AndroidOpenAlSoundEngine"/>; invoked from MainActivity
	/// on lifecycle and audio-focus changes.
	/// </summary>
	public static class AndroidAudioBridge
	{
		/// <summary>true = pause all voices + detach ALC context; false = restore.</summary>
		public static Action<bool> SetSuspended;

		/// <summary>Master gain multiplier 0–1 while focus is ducked (phone call, nav prompt).</summary>
		public static Action<float> SetDuckGain;
	}
}
