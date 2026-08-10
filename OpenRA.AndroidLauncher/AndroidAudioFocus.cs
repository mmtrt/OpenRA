// Android audio focus — pause/duck game audio for calls, navigation, other apps.

using System;
using Android.Content;
using Android.Media;
using Android.OS;
using OpenRA.Platforms.Android;

namespace OpenRA.Android
{
	/// <summary>
	/// Requests AUDIOFOCUS_GAIN for gameplay. On transient loss ducks; on full loss
	/// suspends the OpenAL device via <see cref="AndroidAudioBridge"/>.
	/// </summary>
	public sealed class AndroidAudioFocus : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
	{
		readonly AudioManager manager;
		AudioFocusRequestClass focusRequest; // API 26+
		bool hasFocus;
		bool started;

		public AndroidAudioFocus(Context context)
		{
			manager = context.GetSystemService(Context.AudioService) as AudioManager;
		}

		public void Start()
		{
			if (started || manager == null)
				return;
			started = true;
			Request();
		}

		public void Stop()
		{
			if (!started || manager == null)
				return;
			started = false;
			Abandon();
		}

		void Request()
		{
			try
			{
				if ((int)Build.VERSION.SdkInt >= 26)
				{
					var attrs = new AudioAttributes.Builder()
						.SetUsage(AudioUsageKind.Game)
						.SetContentType(AudioContentType.Music)
						.Build();
					focusRequest = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
						.SetAudioAttributes(attrs)
						.SetAcceptsDelayedFocusGain(true)
						.SetOnAudioFocusChangeListener(this)
						.SetWillPauseWhenDucked(false)
						.Build();
					var r = manager.RequestAudioFocus(focusRequest);
					hasFocus = r == AudioFocusRequest.Granted;
				}
				else
				{
#pragma warning disable CS0618
					var r = manager.RequestAudioFocus(this, global::Android.Media.Stream.Music, AudioFocus.Gain);
#pragma warning restore CS0618
					hasFocus = r == AudioFocusRequest.Granted;
				}
				AndroidFileLog.Info("OpenRA.Audio", "RequestAudioFocus granted=" + hasFocus);
				if (hasFocus)
					ApplyGained();
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Audio", "Request: " + e.Message);
			}
		}

		void Abandon()
		{
			try
			{
				if ((int)Build.VERSION.SdkInt >= 26 && focusRequest != null)
					manager.AbandonAudioFocusRequest(focusRequest);
				else
				{
#pragma warning disable CS0618
					manager.AbandonAudioFocus(this);
#pragma warning restore CS0618
				}
				hasFocus = false;
				AndroidFileLog.Info("OpenRA.Audio", "AbandonAudioFocus");
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Audio", "Abandon: " + e.Message);
			}
		}

		public void OnAudioFocusChange(AudioFocus focusChange)
		{
			try
			{
				switch (focusChange)
				{
					case AudioFocus.Gain:
					case AudioFocus.GainTransient:
					case AudioFocus.GainTransientMayDuck:
						hasFocus = true;
						ApplyGained();
						AndroidFileLog.Info("OpenRA.Audio", "Focus GAIN " + focusChange);
						break;

					case AudioFocus.Loss:
						hasFocus = false;
						AndroidAudioBridge.SetDuckGain?.Invoke(1f);
						AndroidAudioBridge.SetSuspended?.Invoke(true);
						AndroidFileLog.Info("OpenRA.Audio", "Focus LOSS — suspended");
						break;

					case AudioFocus.LossTransient:
						hasFocus = false;
						AndroidAudioBridge.SetSuspended?.Invoke(true);
						AndroidFileLog.Info("OpenRA.Audio", "Focus LOSS_TRANSIENT — suspended");
						break;

					case AudioFocus.LossTransientCanDuck:
						// Nav prompt / notification — keep playing quieter
						AndroidAudioBridge.SetDuckGain?.Invoke(0.25f);
						AndroidFileLog.Info("OpenRA.Audio", "Focus DUCK");
						break;
				}
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Audio", "OnAudioFocusChange: " + e.Message);
			}
		}

		static void ApplyGained()
		{
			AndroidAudioBridge.SetDuckGain?.Invoke(1f);
			AndroidAudioBridge.SetSuspended?.Invoke(false);
		}
	}
}
