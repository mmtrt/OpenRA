// Native Install Content UI — official-style choice + download progress, themed per mod.

using System;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;
using AColor = global::Android.Graphics.Color;

namespace OpenRA.Android
{
	public sealed class InstallContentView : FrameLayout
	{
		readonly ModInfo mod;
		readonly LinearLayout choicePanel;
		readonly LinearLayout downloadPanel;
		readonly TextView status;
		readonly TextView downloadStatus;
		readonly ProgressBar progress;
		readonly Button advanced;
		readonly Button quick;
		readonly Button quit;
		readonly Button cancel;

		public event Action QuickInstallClicked;
		public event Action AdvancedInstallClicked;
		public event Action QuitClicked;
		public event Action CancelDownloadClicked;

		public InstallContentView(Context context) : base(context)
		{
			mod = ModInfo.Current;

			var bg = new ImageView(context);
			bg.SetScaleType(ImageView.ScaleType.CenterCrop);
			try
			{
				var id = context.Resources.GetIdentifier("install_bg_" + mod.Id, "drawable", context.PackageName);
				if (id == 0)
					id = context.Resources.GetIdentifier("install_bg", "drawable", context.PackageName);
				if (id != 0)
					bg.SetImageResource(id);
			}
			catch { /* ignore */ }
			bg.SetColorFilter(new PorterDuffColorFilter(AColor.Argb(140, 0x08, 0x08, 0x0c), PorterDuff.Mode.SrcAtop));
			AddView(bg, new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

			// ----- Choice panel (Advanced / Quick / Quit) -----
			choicePanel = MakeCard(context);
			var title = MakeTitle(context, "Install Content");
			var divider = MakeDivider(context);

			var body = new TextView(context)
			{
				Text = mod.InstallBlurb,
				TextSize = 13f
			};
			body.SetTextColor(mod.BodyColor);

			status = new TextView(context)
			{
				Text = "",
				TextSize = 12f,
				Gravity = GravityFlags.CenterHorizontal
			};
			status.SetTextColor(AColor.Rgb(0xa0, 0xb0, 0x80));
			status.Visibility = ViewStates.Gone;

			var buttons = new LinearLayout(context) { Orientation = Orientation.Horizontal };
			buttons.SetGravity(GravityFlags.CenterHorizontal);
			var btnLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
			btnLp.SetMargins(Dp(4), Dp(14), Dp(4), 0);

			advanced = MakeButton(context, "Advanced Install");
			quick = MakeButton(context, "Quick Install");
			quit = MakeButton(context, "Quit");
			advanced.Click += (_, _) => AdvancedInstallClicked?.Invoke();
			quick.Click += (_, _) => QuickInstallClicked?.Invoke();
			quit.Click += (_, _) => QuitClicked?.Invoke();

			buttons.AddView(advanced, btnLp);
			buttons.AddView(quick, btnLp);
			buttons.AddView(quit, btnLp);

			choicePanel.AddView(title, MatchWrap());
			choicePanel.AddView(divider, DividerLp());
			choicePanel.AddView(body, MatchWrap());
			choicePanel.AddView(status, new LinearLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(10) });
			choicePanel.AddView(buttons, MatchWrap());

			// ----- Download progress panel -----
			downloadPanel = MakeCard(context);
			var dlTitle = MakeTitle(context, "Downloading Quick Install Package");
			var dlDivider = MakeDivider(context);

			// Horizontal determinate progress bar
			var styleId = context.Resources.GetIdentifier("Widget.Material.ProgressBar.Horizontal", "style", "android");
			if (styleId == 0)
				styleId = context.Resources.GetIdentifier("Widget.ProgressBar.Horizontal", "style", "android");
			progress = styleId != 0
				? new ProgressBar(context, null, 0, styleId)
				: new ProgressBar(context);
			progress.Indeterminate = false;
			progress.Max = 1000;
			progress.Progress = 0;
			try
			{
				progress.ProgressDrawable?.SetColorFilter(
					new PorterDuffColorFilter(mod.PrimaryColor, PorterDuff.Mode.SrcIn));
			}
			catch { /* ignore */ }

			downloadStatus = new TextView(context)
			{
				Text = "Preparing…",
				TextSize = 12f,
				Gravity = GravityFlags.CenterHorizontal
			};
			downloadStatus.SetTextColor(mod.BodyColor);

			var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
			row.SetGravity(GravityFlags.Right);
			cancel = MakeButton(context, "Cancel");
			cancel.Click += (_, _) => CancelDownloadClicked?.Invoke();
			var cancelLp = new LinearLayout.LayoutParams(
				ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
			cancelLp.TopMargin = Dp(12);
			row.AddView(cancel, cancelLp);

			downloadPanel.AddView(dlTitle, MatchWrap());
			downloadPanel.AddView(dlDivider, DividerLp());
			downloadPanel.AddView(progress, new LinearLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, Dp(12)) { TopMargin = Dp(8), BottomMargin = Dp(8) });
			downloadPanel.AddView(downloadStatus, MatchWrap());
			downloadPanel.AddView(row, MatchWrap());
			downloadPanel.Visibility = ViewStates.Gone;

			var host = new FrameLayout(context);
			host.AddView(choicePanel, MatchWrap());
			host.AddView(downloadPanel, MatchWrap());
			AddView(host, new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
			{
				Gravity = GravityFlags.Center,
				LeftMargin = Dp(28),
				RightMargin = Dp(28)
			});
		}

		LinearLayout MakeCard(Context context)
		{
			var card = new LinearLayout(context) { Orientation = Orientation.Vertical };
			var cardBg = new GradientDrawable();
			cardBg.SetColor(mod.BackgroundColor);
			cardBg.SetStroke(Dp(2), mod.PrimaryColor);
			cardBg.SetCornerRadius(Dp(4));
			card.Background = cardBg;
			card.SetPadding(Dp(22), Dp(18), Dp(22), Dp(16));
			return card;
		}

		TextView MakeTitle(Context context, string text)
		{
			var title = new TextView(context)
			{
				Text = text,
				TextSize = 17f,
				Gravity = GravityFlags.CenterHorizontal
			};
			title.SetTextColor(mod.TitleColor);
			title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
			return title;
		}

		View MakeDivider(Context context)
		{
			var divider = new View(context);
			divider.SetBackgroundColor(mod.PrimaryColor);
			return divider;
		}

		LinearLayout.LayoutParams DividerLp()
		{
			var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(2));
			lp.TopMargin = Dp(10);
			lp.BottomMargin = Dp(12);
			return lp;
		}

		static LinearLayout.LayoutParams MatchWrap() =>
			new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

		Button MakeButton(Context context, string text)
		{
			var b = new Button(context) { Text = text, TextSize = 12f };
			b.SetTextColor(mod.TitleColor);
			var bg = new GradientDrawable();
			bg.SetColor(AColor.Rgb(0x3a, 0x3a, 0x32));
			bg.SetStroke(2, mod.PrimaryColor);
			bg.SetCornerRadius(6f);
			b.Background = bg;
			b.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
			return b;
		}

		public void ShowDownloadProgress()
		{
			choicePanel.Visibility = ViewStates.Gone;
			downloadPanel.Visibility = ViewStates.Visible;
			progress.Indeterminate = true;
			progress.Progress = 0;
			downloadStatus.Text = "Fetching mirror list…";
			cancel.Enabled = true;
		}

		public void ShowChoice(string message = null)
		{
			downloadPanel.Visibility = ViewStates.Gone;
			choicePanel.Visibility = ViewStates.Visible;
			advanced.Enabled = true;
			quick.Enabled = true;
			quit.Enabled = true;
			if (!string.IsNullOrEmpty(message))
			{
				status.Visibility = ViewStates.Visible;
				status.Text = message;
			}
		}

		public void SetBusy(bool busy, string message = null)
		{
			if (busy)
			{
				ShowDownloadProgress();
				if (message != null)
					downloadStatus.Text = message;
			}
			else
			{
				ShowChoice(message);
			}
		}

		public void SetProgress(string message, double fraction)
		{
			if (downloadPanel.Visibility != ViewStates.Visible)
				ShowDownloadProgress();

			downloadStatus.Text = message ?? "";
			if (fraction <= 0)
			{
				progress.Indeterminate = true;
			}
			else
			{
				progress.Indeterminate = false;
				progress.Progress = (int)Math.Clamp(fraction * 1000, 0, 1000);
			}
		}

		int Dp(int value) =>
			(int)TypedValue.ApplyDimension(ComplexUnitType.Dip, value, Resources.DisplayMetrics);
	}
}
