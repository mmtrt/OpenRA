#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port — silent sound backend stub.
 */
#endregion

using System.IO;

namespace OpenRA.Platforms.Android
{
	sealed class AndroidDummySoundEngine : ISoundEngine
	{
		public bool Dummy => true;

		public SoundDevice[] AvailableDevices()
		{
			return new[] { new SoundDevice(null, "No Sound Output (Android stub)") };
		}

		public ISoundSource AddSoundSourceFromMemory(byte[] data, int channels, int sampleBits, int sampleRate)
		{
			return new AndroidNullSoundSource();
		}

		public ISound Play2D(ISoundSource soundSource, bool loop, bool relative, WPos pos, float volume, bool attenuateVolume)
		{
			return new AndroidNullSound();
		}

		public ISound Play2DStream(Stream stream, int channels, int sampleBits, int sampleRate, bool loop, bool relative, WPos pos, float volume)
		{
			return null;
		}

		public float Volume
		{
			get => 0;
			set { }
		}

		public void PauseSound(ISound sound, bool paused) { }
		public void SetAllSoundsPaused(bool paused) { }
		public void SetSoundVolume(float volume, ISound music, ISound video) { }
		public void StopSound(ISound sound) { }
		public void StopAllSounds() { }
		public void SetListenerPosition(WPos position) { }
		public void SetSoundLooping(bool looping, ISound sound) { }
		public void SetSoundPosition(ISound sound, WPos position) { }
		public void Dispose() { }
	}

	sealed class AndroidNullSoundSource : ISoundSource
	{
		public void Dispose() { }
	}

	sealed class AndroidNullSound : ISound
	{
		public float Volume { get; set; }
		public float SeekPosition => 0;
		public bool Complete => true;
		public void SetPosition(WPos position) { }
	}
}
