#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of the unofficial OpenRA Android port effort.
 */
#endregion

using System;
using System.IO;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	/// <summary>
	/// Placeholder sound engine so the game can initialise without audio.
	/// Real implementation (AAudio or OpenAL-soft) is Phase 5.
	/// Matches the ISoundEngine contract from OpenRA.Game/Sound/SoundDevice.cs.
	/// </summary>
	sealed class AndroidDummySoundEngine : ISoundEngine
	{
		public bool Dummy => true;
		public float Volume { get; set; } = 1f;

		public SoundDevice[] AvailableDevices() => Array.Empty<SoundDevice>();

		public ISoundSource AddSoundSourceFromMemory(byte[] data, int channels, int sampleBits, int sampleRate)
			=> new DummySoundSource();

		public ISound Play2D(ISoundSource sound, bool loop, bool relative, WPos pos, float volume, bool attenuateVolume)
			=> new DummySound();

		public ISound Play2DStream(Stream stream, int channels, int sampleBits, int sampleRate,
			bool loop, bool relative, WPos pos, float volume)
			=> new DummySound();

		public void PauseSound(ISound sound, bool paused) { }
		public void StopSound(ISound sound) { }
		public void SetAllSoundsPaused(bool paused) { }
		public void StopAllSounds() { }
		public void SetListenerPosition(WPos position) { }
		public void SetSoundVolume(float volume, ISound music, ISound video) { }
		public void SetSoundLooping(bool looping, ISound sound) { }
		public void SetSoundPosition(ISound sound, WPos position) { }

		public void Dispose() { }

		sealed class DummySoundSource : ISoundSource
		{
			public void Dispose() { }
		}

		sealed class DummySound : ISound
		{
			public float Volume { get; set; }
			public float SeekPosition => 0f;
			public bool Complete => true;
			public void SetPosition(WPos pos) { }
		}
	}
}
