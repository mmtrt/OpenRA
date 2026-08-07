// Native Install Content UI (modeled on official OpenRA dialog).

using System;
using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Widget;
using AColor = global::Android.Graphics.Color;

namespace OpenRA.Android
{
	public sealed class InstallContentView : FrameLayout
	{
		readonly TextView title;
		readonly TextView body;
		readonly TextView status;
		readonly ProgressBar progress;
		readonly Button advanced;
		readonly Button quick;
		readonly Button quit;

		public event Action QuickInstallClicked;
		public event Action AdvancedInstallClicked;
		public event Action QuitClicked;

		public InstallContentView(Context context) : base(context)
		{
			SetBackgroundColor(AColor.Rgb(0x0a, 0x0a, 0x0a));

			var card = new LinearLayout(context)
			{
				Orientation = Orientation.Vertical
			};
			card.SetBackgroundColor(AColor.Rgb(0x1a, 0x1c, 0x14));
			card.SetPadding(Dp(20), Dp(20), Dp(20), Dp(20));

			title = new TextView(context)
			{
				Text = "Install Content",
				TextSize = 20f,
				Gravity = GravityFlags.CenterHorizontal
			};
			title.SetTextColor(AColor.Rgb(0xe0, 0xd0, 0xa0));
			title.SetTypeface(Typeface.DefaultBold);

			var divider = new View(context);
			divider.SetBackgroundColor(AColor.Rgb(0x80, 0x20, 0x18));
			var divLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(2));
			divLp.TopMargin = Dp(12);
			divLp.BottomMargin = Dp(12);

			body = new TextView(context)
			{
				Text =
					"Red Alert requires artwork and audio from the original game.\n\n" +
					"Quick Install will automatically download this content (without music " +
					"or videos) from a mirror of the 2008 Red Alert freeware release.\n\n" +
					"Advanced Install can copy content you already have on the device.",
				TextSize = 14f
			};
			body.SetTextColor(AColor.Rgb(0xc8, 0xc0, 0xa8));

			status = new TextView(context)
			{
				Text = "",
				TextSize = 12f,
				Gravity = GravityFlags.CenterHorizontal
			};
			status.SetTextColor(AColor.Rgb(0xa0, 0xb0, 0x80));
			status.Visibility = ViewStates.Gone;

			progress = new ProgressBar(context, null, Android.Resource.Attribute.ProgressBarStyleHorizontal)
			{
				Indeterminate = false,
				Max = 1000
			};
			progress.Visibility = ViewStates.Gone;

			var buttons = new LinearLayout(context) { Orientation = Orientation.Horizontal };
			buttons.SetGravity(GravityFlags.CenterHorizontal);
			var btnLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
			btnLp.SetMargins(Dp(4), Dp(16), Dp(4), 0);

			advanced = MakeButton(context, "Advanced Install");
			quick = MakeButton(context, "Quick Install");
			quit = MakeButton(context, "Quit");

			advanced.Click += (_, _) => AdvancedInstallClicked?.Invoke();
			quick.Click += (_, _) => QuickInstallClicked?.Invoke();
			quit.Click += (_, _) => QuitClicked?.Invoke();

			buttons.AddView(advanced, btnLp);
			buttons.AddView(quick, btnLp);
			buttons.AddView(quit, btnLp);

			card.AddView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
			card.AddView(divider, divLp);
			card.AddView(body, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
			card.AddView(status, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(12) });
			card.AddView(progress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });
			card.AddView(buttons, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

			var cardLp = new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
			{
				Gravity = GravityFlags.Center,
				LeftMargin = Dp(24),
				RightMargin = Dp(24)
			};
			AddView(card, cardLp);
		}

		static Button MakeButton(Context context, string text)
		{
			var b = new Button(context) { Text = text, TextSize = 12f };
			b.SetTextColor(AColor.White);
			b.SetBackgroundColor(AColor.Rgb(0x3a, 0x3a, 0x32));
			return b;
		}

		public void SetBusy(bool busy, string message = null)
		{
			advanced.Enabled = !busy;
			quick.Enabled = !busy;
			quit.Enabled = !busy;
			if (message != null)
			{
				status.Visibility = ViewStates.Visible;
				status.Text = message;
			}

			progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
		}

		public void SetProgress(string message, double fraction)
		{
			status.Visibility = ViewStates.Visible;
			status.Text = message ?? "";
			progress.Visibility = ViewStates.Visible;
			progress.Indeterminate = fraction <= 0;
			if (fraction > 0)
				progress.Progress = (int)(fraction * 1000);
		}

		int Dp(int value)
		{
			return (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, value, Resources.DisplayMetrics);
		}
	}
}
