// Native Install Content UI — official-style dialog + faction watermark background.

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
			// Official-style dark field with repeated faction icons (canvas-drawn)
			var watermark = new FactionWatermarkView(context);
			AddView(watermark, new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

			var card = new LinearLayout(context) { Orientation = Orientation.Vertical };
			var cardBg = new GradientDrawable();
			cardBg.SetColor(AColor.Argb(220, 0x1a, 0x1c, 0x14));
			cardBg.SetStroke(Dp(2), AColor.Rgb(0x90, 0x28, 0x18));
			cardBg.SetCornerRadius(Dp(4));
			card.Background = cardBg;
			card.SetPadding(Dp(22), Dp(18), Dp(22), Dp(16));

			var title = new TextView(context)
			{
				Text = "Install Content",
				TextSize = 18f,
				Gravity = GravityFlags.CenterHorizontal
			};
			title.SetTextColor(AColor.Rgb(0xe8, 0xd8, 0xa8));
			title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);

			var divider = new View(context);
			divider.SetBackgroundColor(AColor.Rgb(0x90, 0x28, 0x18));
			var divLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(2));
			divLp.TopMargin = Dp(10);
			divLp.BottomMargin = Dp(12);

			var body = new TextView(context)
			{
				Text =
					"Red Alert requires artwork and audio from the original game.\n\n" +
					"Quick Install will automatically download this content (without music " +
					"or videos) from a mirror of the 2008 Red Alert freeware release.\n\n" +
					"Advanced Install includes options for copying the music, videos, and " +
					"other content from an original game disc or digital installation.",
				TextSize = 13f
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

			progress = new ProgressBar(context) { Indeterminate = true, Max = 1000 };
			progress.Visibility = ViewStates.Gone;

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

			card.AddView(title, MatchWrap());
			card.AddView(divider, divLp);
			card.AddView(body, MatchWrap());
			card.AddView(status, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(10) });
			card.AddView(progress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });
			card.AddView(buttons, MatchWrap());

			AddView(card, new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
			{
				Gravity = GravityFlags.Center,
				LeftMargin = Dp(28),
				RightMargin = Dp(28)
			});
		}

		static LinearLayout.LayoutParams MatchWrap() =>
			new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

		static Button MakeButton(Context context, string text)
		{
			var b = new Button(context) { Text = text, TextSize = 12f };
			b.SetTextColor(AColor.Rgb(0xf0, 0xe8, 0xd0));
			var bg = new GradientDrawable();
			bg.SetColor(AColor.Rgb(0x3a, 0x3a, 0x32));
			bg.SetStroke(2, AColor.Rgb(0x70, 0x68, 0x50));
			bg.SetCornerRadius(6f);
			b.Background = bg;
			b.SetPadding(16, 12, 16, 12);
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

		int Dp(int value) =>
			(int)TypedValue.ApplyDimension(ComplexUnitType.Dip, value, Resources.DisplayMetrics);
	}

	/// <summary>
	/// Dark field with a grid of simplified RA faction marks (star / eagle / sickle) —
	/// approximates the official Content Install chrome background.
	/// </summary>
	sealed class FactionWatermarkView : View
	{
		readonly Paint paint = new() { AntiAlias = true };
		readonly AColor[] colors =
		{
			AColor.Rgb(0x40, 0x28, 0x18), // brown
			AColor.Rgb(0x28, 0x30, 0x50), // blue
			AColor.Rgb(0x50, 0x20, 0x20), // red
			AColor.Rgb(0x38, 0x38, 0x20), // olive
			AColor.Rgb(0x20, 0x40, 0x48), // teal
		};

		public FactionWatermarkView(Context context) : base(context)
		{
			SetBackgroundColor(AColor.Rgb(0x0e, 0x0e, 0x12));
		}

		protected override void OnDraw(Canvas canvas)
		{
			base.OnDraw(canvas);
			var w = Width;
			var h = Height;
			if (w <= 0 || h <= 0)
				return;

			var cell = Math.Max(Dp(72), Math.Min(w, h) / 6);
			var idx = 0;
			for (var y = cell / 2; y < h + cell; y += cell)
			{
				for (var x = cell / 2; x < w + cell; x += cell)
				{
					paint.Color = colors[idx++ % colors.Length];
					paint.Alpha = 90;
					paint.SetStyle(Paint.Style.Stroke);
					paint.StrokeWidth = Math.Max(2f, cell / 28f);
					var kind = idx % 5;
					var r = cell * 0.28f;
					switch (kind)
					{
						case 0: // star (allied/soviet mark)
							DrawStar(canvas, x, y, r);
							break;
						case 1: // hex
							DrawHex(canvas, x, y, r);
							break;
						case 2: // triangle
							DrawTriangle(canvas, x, y, r);
							break;
						case 3: // diamond
							DrawDiamond(canvas, x, y, r);
							break;
						default: // circle
							canvas.DrawCircle(x, y, r * 0.85f, paint);
							break;
					}
				}
			}
		}

		void DrawStar(Canvas c, float cx, float cy, float r)
		{
			var path = new Path();
			for (var i = 0; i < 5; i++)
			{
				var a = (float)(-Math.PI / 2 + i * 2 * Math.PI / 5);
				var x = cx + r * (float)Math.Cos(a);
				var y = cy + r * (float)Math.Sin(a);
				if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
				var a2 = a + (float)(Math.PI / 5);
				var x2 = cx + r * 0.4f * (float)Math.Cos(a2);
				var y2 = cy + r * 0.4f * (float)Math.Sin(a2);
				path.LineTo(x2, y2);
			}
			path.Close();
			c.DrawPath(path, paint);
		}

		void DrawHex(Canvas c, float cx, float cy, float r)
		{
			var path = new Path();
			for (var i = 0; i < 6; i++)
			{
				var a = (float)(i * Math.PI / 3);
				var x = cx + r * (float)Math.Cos(a);
				var y = cy + r * (float)Math.Sin(a);
				if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
			}
			path.Close();
			c.DrawPath(path, paint);
		}

		void DrawTriangle(Canvas c, float cx, float cy, float r)
		{
			var path = new Path();
			path.MoveTo(cx, cy - r);
			path.LineTo(cx + r * 0.9f, cy + r * 0.6f);
			path.LineTo(cx - r * 0.9f, cy + r * 0.6f);
			path.Close();
			c.DrawPath(path, paint);
		}

		void DrawDiamond(Canvas c, float cx, float cy, float r)
		{
			var path = new Path();
			path.MoveTo(cx, cy - r);
			path.LineTo(cx + r, cy);
			path.LineTo(cx, cy + r);
			path.LineTo(cx - r, cy);
			path.Close();
			c.DrawPath(path, paint);
		}

		int Dp(int v) =>
			(int)TypedValue.ApplyDimension(ComplexUnitType.Dip, v, Resources.DisplayMetrics);
	}
}
