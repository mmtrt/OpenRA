#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Support;

namespace OpenRA
{
	public sealed class Renderer : IDisposable
	{
		enum RenderType { None, World, UI }

		public SpriteRenderer WorldSpriteRenderer { get; }
		public RgbaSpriteRenderer WorldRgbaSpriteRenderer { get; }
		public RgbaColorRenderer WorldRgbaColorRenderer { get; }
		public IRenderer[] WorldRenderers = [];
		public RgbaColorRenderer RgbaColorRenderer { get; }
		public SpriteRenderer SpriteRenderer { get; }
		public RgbaSpriteRenderer RgbaSpriteRenderer { get; }

		public bool WindowHasInputFocus => Window.HasInputFocus;
		public bool WindowIsSuspended => Window.IsSuspended;

		public IReadOnlyDictionary<string, SpriteFont> Fonts;

		internal IPlatformWindow Window { get; }
		internal IGraphicsContext Context { get; }

		internal int TempVertexBufferSize { get; }
		internal int TempIndexBufferSize { get; }

		readonly IVertexBuffer<Vertex> tempVertexBuffer;
		readonly IIndexBuffer quadIndexBuffer;
		readonly Stack<Rectangle> scissorState = [];
		readonly ITexture bufferSnapshot;

		IFrameBuffer screenBuffer;
		Sprite screenSprite;

		IFrameBuffer worldBuffer;
		Sheet worldSheet;
		bool loggedWorldBlit;
		Sprite worldSprite;
		Size lastMaximumViewportSize;
		Size lastWorldViewportSize;

		public Size WorldFrameBufferSize => worldSheet.Size;
		public int WorldDownscaleFactor { get; private set; } = 1;

		/// <summary>
		/// Copies and returns the currently rendered state as a temporary texture.
		/// </summary>
		public ITexture GetRenderBufferSnapshot()
		{
			var size = renderType == RenderType.World ? worldSheet.Size : Window.SurfaceSize.NextPowerOf2();
			bufferSnapshot.SetDataFromReadBuffer(new Rectangle(int2.Zero, size));
			return bufferSnapshot;
		}

		SheetBuilder fontSheetBuilder;
		readonly IPlatform platform;

		float depthMargin;

		Size lastBufferSize = new(-1, -1);

		Rectangle lastWorldViewport;
		float2 lastViewportLocation;
		ITexture currentPaletteTexture;
		int currentPaletteHeight = 0;
		IBatchRenderer currentBatchRenderer;
		RenderType renderType = RenderType.None;

		public Renderer(IPlatform platform, GraphicSettings graphicSettings, int vertexBatchSize)
		{
			this.platform = platform;
			var resolution = GetResolution(graphicSettings);

			TempVertexBufferSize = vertexBatchSize - vertexBatchSize % 4;
			TempIndexBufferSize = TempVertexBufferSize / 4 * 6;

			Window = platform.CreateWindow(new Size(resolution.Width, resolution.Height),
				graphicSettings.Mode, graphicSettings.UIScale, TempVertexBufferSize, TempIndexBufferSize,
				graphicSettings.VideoDisplay, graphicSettings.GLProfile);

			Context = Window.Context;

			var combinedBindings = new CombinedShaderBindings();
			WorldSpriteRenderer = new SpriteRenderer(this, Context.CreateShader(combinedBindings));
			WorldRgbaSpriteRenderer = new RgbaSpriteRenderer(WorldSpriteRenderer);
			WorldRgbaColorRenderer = new RgbaColorRenderer(WorldSpriteRenderer);
			SpriteRenderer = new SpriteRenderer(this, Context.CreateShader(combinedBindings));
			RgbaSpriteRenderer = new RgbaSpriteRenderer(SpriteRenderer);
			RgbaColorRenderer = new RgbaColorRenderer(SpriteRenderer);

			tempVertexBuffer = Context.CreateEmptyVertexBuffer<Vertex>(TempVertexBufferSize);
			quadIndexBuffer = Context.CreateIndexBuffer(Util.CreateQuadIndices(TempIndexBufferSize / 6));
			bufferSnapshot = Context.CreateTexture();
		}

		static Size GetResolution(GraphicSettings graphicsSettings)
		{
			var size = (graphicsSettings.Mode == WindowMode.Windowed)
				? graphicsSettings.WindowedSize
				: graphicsSettings.FullscreenSize;
			return new Size(size.X, size.Y);
		}

		public void SetUIScale(float scale)
		{
			Window.SetScaleModifier(scale);
		}

		public void InitializeFonts(ModData modData)
		{
			if (Fonts != null)
				foreach (var font in Fonts.Values)
					font.Dispose();
			using (new PerfTimer("SpriteFonts"))
			{
				fontSheetBuilder?.Dispose();
				fontSheetBuilder = new SheetBuilder(SheetType.BGRA, modData.Manifest.RendererConstants.FontSheetSize);
				Fonts = modData.GetOrCreate<Fonts>().FontList.ToDictionary(x => x.Key,
					x => new SpriteFont(
						platform, x.Value.Font, modData.DefaultFileSystem.Open(x.Value.Font).ReadAllBytes(),
						x.Value.Size, x.Value.Ascender, Window.EffectiveWindowScale, fontSheetBuilder));
			}

			Window.OnWindowScaleChanged += (oldNative, oldEffective, newNative, newEffective) =>
			{
				Game.RunAfterTick(() =>
				{
					// Recalculate downscaling factor for the new window scale
					SetMaximumViewportSize(lastMaximumViewportSize);

					ChromeProvider.SetDPIScale(newEffective);

					foreach (var f in Fonts)
						f.Value.SetScale(newEffective);
				});
			};
		}

		public void SetDepthMargin(float depthMargin)
		{
			this.depthMargin = depthMargin;
		}

		void BeginFrame()
		{
			Context.Clear();

			var surfaceSize = Window.SurfaceSize;
			var surfaceBufferSize = surfaceSize.NextPowerOf2();

			if (screenSprite == null || screenSprite.Sheet.Size != surfaceBufferSize)
			{
				screenBuffer?.Dispose();

				// Render the screen into a frame buffer to simplify reading back screenshots
				screenBuffer = Context.CreateFrameBuffer(surfaceBufferSize, Color.FromArgb(0xFF, 0, 0, 0));
			}

			if (screenSprite == null || surfaceSize.Width != screenSprite.Bounds.Width || -surfaceSize.Height != screenSprite.Bounds.Height)
			{
				var screenSheet = new Sheet(SheetType.BGRA, screenBuffer.Texture);

				// Flip sprite in Y to match OpenGL's bottom-left origin
				var screenBounds = Rectangle.FromLTRB(0, surfaceSize.Height, surfaceSize.Width, 0);
				screenSprite = new Sprite(screenSheet, screenBounds, TextureChannel.RGBA);
			}

			// In HiDPI windows we follow Apple's convention of defining window coordinates as for standard resolution windows
			// but to have a higher resolution backing surface with more than 1 texture pixel per viewport pixel.
			// We must convert the surface buffer size to a viewport size - in general this is NOT just the window size
			// rounded to the next power of two, as the NextPowerOf2 calculation is done in the surface pixel coordinates
			var scale = Window.EffectiveWindowScale;
			var bufferSize = new Size((int)(surfaceBufferSize.Width / scale), (int)(surfaceBufferSize.Height / scale));
			if (lastBufferSize != bufferSize)
			{
				SpriteRenderer.SetViewportParams(bufferSize, 1, 0f, int2.Zero);
				lastBufferSize = bufferSize;
			}
		}

		public void SetMaximumViewportSize(Size size)
		{
			// Aim to render the world into a framebuffer at 1:1 scaling which is then up/downscaled using a custom
			// filter to provide crisp scaling and avoid rendering glitches when the depth buffer is used and samples don't match.
			// This approach does not scale well to large sizes, first saturating GPU fill rate and then crashing when
			// reaching the framebuffer size limits (typically 16k). We therefore clamp the maximum framebuffer size to
			// twice the window surface size, which strikes a reasonable balance between rendering quality and performance.
			// Mods that use the depth buffer must instead limit their artwork resolution or maximum zoom-out levels.
			Size worldBufferSize;
			if (depthMargin == 0)
			{
				var surfaceSize = Window.SurfaceSize;
				worldBufferSize = new Size(Math.Min(size.Width, 2 * surfaceSize.Width), Math.Min(size.Height, 2 * surfaceSize.Height)).NextPowerOf2();
			}
			else
				worldBufferSize = size.NextPowerOf2();

			if (worldSprite == null || worldSheet.Size != worldBufferSize)
			{
				worldBuffer?.Dispose();

				// If enableWorldFrameBufferDownscale and the world is more than twice the size of the final output size do we allow it to be downsampled!
				worldBuffer = Context.CreateFrameBuffer(worldBufferSize);

				// Pixel art scaling mode is a customized bilinear sampling
				worldBuffer.Texture.ScaleFilter = TextureScaleFilter.Linear;
				worldSheet = new Sheet(SheetType.BGRA, worldBuffer.Texture);

				// Invalidate cached state to force a shader update
				lastWorldViewport = Rectangle.Empty;
				worldSprite = null;
			}

			lastMaximumViewportSize = size;
		}

		public void BeginWorld(float2 viewportLocation, Size viewportSize)
		{
			if (renderType != RenderType.None)
				throw new InvalidOperationException($"BeginWorld called with renderType = {renderType}, expected RenderType.None.");

			BeginFrame();

			if (worldSheet == null)
				throw new InvalidOperationException("BeginWorld called before SetMaximumViewportSize has been set.");

			var centerLocation = viewportLocation.ToInt2();
			if (worldSprite == null || viewportSize != lastWorldViewportSize || viewportLocation != lastViewportLocation)
			{
				lastViewportLocation = viewportLocation;
				lastWorldViewportSize = viewportSize;

				// Downscale world rendering if needed to fit within the framebuffer
				var vw = viewportSize.Width;
				var vh = viewportSize.Height;
				var bw = worldSheet.Size.Width;
				var bh = worldSheet.Size.Height;
				WorldDownscaleFactor = 1;
				while (vw / WorldDownscaleFactor > bw || vh / WorldDownscaleFactor > bh)
					WorldDownscaleFactor++;

				// We need to add 1 to scroll in order to handle interpixel 0-0.99 fractionalOffset.
				var s = new Size(vw / WorldDownscaleFactor + 1, vh / WorldDownscaleFactor + 1);
				var fractionalOffset = centerLocation - viewportLocation;

				// If scaling by an integer factor (including 1:1) we must round the offset
				// to an integer number of screen-space pixels to preserve sharp pixel edges
				var renderScale = screenSprite.Size.X / (s.Width - 1f);
				if (float.IsInteger(renderScale))
					fractionalOffset = (fractionalOffset * renderScale).Round() / renderScale;

				worldSprite = new Sprite(worldSheet, new Rectangle(int2.Zero, s), 0, fractionalOffset, TextureChannel.RGBA);
			}

			worldBuffer.Bind();
			var rect = new Rectangle(centerLocation, viewportSize);
			if (lastWorldViewport != rect)
			{
				var topLeft = centerLocation - viewportSize.ToInt2() / 2;
				WorldSpriteRenderer.SetViewportParams(worldSheet.Size, WorldDownscaleFactor, depthMargin, topLeft);
				lastWorldViewport = rect;
			}

			renderType = RenderType.World;
		}

		public void BeginUI()
		{
			if (renderType == RenderType.World)
			{
				// Complete world rendering
				Flush();
				worldBuffer.Unbind();

				// Render the world buffer into the UI buffer
				screenBuffer.Bind();

				var scale = Window.EffectiveWindowScale;

				// We added 1 to worldSprite now we need to subtract.
				var bufferScale = new float3(
					(int)(screenSprite.Bounds.Width / scale) / (worldSprite.Size.X - 1),
					(int)(-screenSprite.Bounds.Height / scale) / (worldSprite.Size.Y - 1),
					1f);

				if (!loggedWorldBlit)
				{
					loggedWorldBlit = true;
					var b = screenSprite.Bounds;
					Log.Write("graphics", "BeginUI world blit: scale=" + scale +
						" screenSprite.Bounds=[" + b.X + "," + b.Y + "," + b.Width + "," + b.Height + "]" +
						" worldSprite.Size=" + worldSprite.Size +
						" worldSheet.Size=" + worldSheet.Size +
						" WorldDownscaleFactor=" + WorldDownscaleFactor +
						" bufferScale=" + bufferScale);
				}

				SpriteRenderer.EnablePixelArtScaling(true);
				RgbaSpriteRenderer.DrawSprite(worldSprite, float3.Zero, bufferScale);
				Flush();
				SpriteRenderer.EnablePixelArtScaling(false);
			}
			else
			{
				// World rendering was skipped
				BeginFrame();
				screenBuffer.Bind();
			}

			renderType = RenderType.UI;
		}

		public void SetPalette(HardwarePalette palette)
		{
			// Note: palette.Texture and palette.ColorShifts are updated at the same time
			// so we only need to check one of the two to know whether we must update the textures
			// also compare heights in case new palettes have been added
			if (palette.Texture == currentPaletteTexture && palette.Height == currentPaletteHeight)
				return;

			Flush();
			currentPaletteTexture = palette.Texture;
			currentPaletteHeight = palette.Height;

			SpriteRenderer.SetPalette(palette);
			WorldSpriteRenderer.SetPalette(palette);

			foreach (var r in WorldRenderers)
				r.SetPalette(palette);
		}

		public void EndFrame(IInputHandler inputHandler)
		{
			if (renderType != RenderType.UI)
				throw new InvalidOperationException($"EndFrame called with renderType = {renderType}, expected RenderType.UI.");

			Flush();

			screenBuffer.Unbind();

			// Render the compositor buffers to the screen
			// HACK / PERF: Fudge the coordinates to cover the actual window while keeping the buffer viewport parameters
			// This saves us two redundant (and expensive) SetViewportParams each frame
			RgbaSpriteRenderer.DrawSprite(screenSprite, new float3(0, lastBufferSize.Height, 0),
				new float3(lastBufferSize.Width / screenSprite.Size.X, -lastBufferSize.Height / screenSprite.Size.Y, 1f));
			Flush();

			Window.PumpInput(inputHandler);
			Context.Present();

			renderType = RenderType.None;
		}

		readonly Dictionary<string, int> loggedDrawBatchesByType = new();

		public void DrawBatch<T>(IVertexBuffer<T> vertices, IShader shader,
			int firstVertex, int numVertices, PrimitiveType type)
			where T : struct
		{
			// Track per vertex-type, not a single global cap — a single cap meant that on
			// TS, ModelRenderer's frequent ModelVertex draws (voxel units) exhausted the
			// whole budget before a single RenderPostProcessPassVertex (the post-process
			// tint quad we're actually chasing) ever got logged.
			var typeName = typeof(T).Name;
			if (!loggedDrawBatchesByType.TryGetValue(typeName, out var count))
				count = 0;

			if (count < 12)
			{
				loggedDrawBatchesByType[typeName] = count + 1;
				Log.Write("graphics", "DrawBatch [" + typeName + "] #" + (count + 1) +
					" shader=" + shader.GetType().Name +
					" first=" + firstVertex + " n=" + numVertices + " type=" + type);
			}

			vertices.Bind();
			shader.Bind();
			Context.DrawPrimitives(type, firstVertex, numVertices);
			PerfHistory.Increment("batches", 1);
		}

		int loggedDegenerateQuads;

		public void DrawQuadBatch(ref Vertex[] vertices, IShader shader, int numVertices)
		{
			// One-time-ish diagnostic: scan for any quad (4 consecutive vertices) that's
			// geometrically degenerate — the signature of a "streak" artifact (a rectangle
			// collapsed to a thin sliver at some rotation). See the size-gate/ratio-check
			// comment below for exactly what's being compared and why. Cheap early-exit
			// once we've logged a handful of occurrences. Uses OpenRA's own Log (mirrored
			// into AndroidFileLog on Android) rather than any platform-specific call, since
			// this is shared code.
			if (loggedDegenerateQuads < 8)
			{
				for (var i = 0; i + 3 < numVertices; i += 4)
				{
					var v0 = vertices[i];
					var v1 = vertices[i + 1];
					var v2 = vertices[i + 2];
					var v3 = vertices[i + 3];

					float minX = Math.Min(Math.Min(v0.X, v1.X), Math.Min(v2.X, v3.X));
					float maxX = Math.Max(Math.Max(v0.X, v1.X), Math.Max(v2.X, v3.X));
					float minY = Math.Min(Math.Min(v0.Y, v1.Y), Math.Min(v2.Y, v3.Y));
					float maxY = Math.Max(Math.Max(v0.Y, v1.Y), Math.Max(v2.Y, v3.Y));

					var w = maxX - minX;
					var h = maxY - minY;
					var bboxArea = w * h;

					// Shoelace formula, assuming the 4 vertices are wound in quad order
					// (matches how sprite quads are always built: consistent winding).
					var shoelace =
						(v0.X * v1.Y - v1.X * v0.Y) +
						(v1.X * v2.Y - v2.X * v1.Y) +
						(v2.X * v3.Y - v3.X * v2.Y) +
						(v3.X * v0.Y - v0.X * v3.Y);
					var quadArea = Math.Abs(shoelace) * 0.5f;

					// A degenerate/sliver quad: spans a substantial bounding-box DIAGONAL
					// (so it's not a small, legitimately-thin normal UI element like a
					// health-bar underline) AND its actual area is small relative to its
					// bounding-box area. The diagonal is the size GATE (catches long-thin
					// streaks at any rotation, unlike a bboxArea floor which unfairly
					// excludes thin-but-long shapes precisely because they're thin). The
					// quadArea-vs-bboxArea RATIO is the degeneracy check: for any legitimate
					// axis-aligned rectangle, shoelace area == w*h exactly regardless of how
					// thin it is (e.g. 480x2 has quadArea=960=bboxArea, ratio=1.0, never
					// flagged) — that ratio only drops for a rotated/sheared/genuinely
					// degenerate shape.
					var diag = MathF.Sqrt(w * w + h * h);
					if (diag > 300 && quadArea < bboxArea * 0.1f)
					{
						loggedDegenerateQuads++;
						Log.Write("graphics", "Degenerate quad #" + loggedDegenerateQuads +
							" shader=" + shader.GetType().Name +
							" verts=[" +
							$"({v0.X:F1},{v0.Y:F1},{v0.Z:F1}) | ({v1.X:F1},{v1.Y:F1},{v1.Z:F1}) | " +
							$"({v2.X:F1},{v2.Y:F1},{v2.Z:F1}) | ({v3.X:F1},{v3.Y:F1},{v3.Z:F1})" +
							"] bbox=" + w.ToString("F1") + "x" + h.ToString("F1") +
							" bboxArea=" + bboxArea.ToString("F0") + " quadArea=" + quadArea.ToString("F0"));

						if (loggedDegenerateQuads >= 8)
							break;
					}
				}
			}

			tempVertexBuffer.SetData(ref vertices, numVertices);
			DrawQuadBatch(tempVertexBuffer, quadIndexBuffer, shader, numVertices / 4 * 6, 0);
		}

		public void DrawQuadBatch<T>(IVertexBuffer<T> vertices, IIndexBuffer indices, IShader shader, int numIndices, int start)
			where T : struct
		{
			vertices.Bind();
			indices.Bind();
			shader.Bind();
			Context.DrawElements(numIndices, start);
			PerfHistory.Increment("batches", 1);
		}

		public void Flush()
		{
			CurrentBatchRenderer = null;
		}

		public Size Resolution => Window.EffectiveWindowSize;
		public Size NativeResolution => Window.NativeWindowSize;
		public float WindowScale => Window.EffectiveWindowScale;
		public float NativeWindowScale => Window.NativeWindowScale;
		public GLProfile GLProfile => Window.GLProfile;
		public GLProfile[] SupportedGLProfiles => Window.SupportedGLProfiles;

		public interface IBatchRenderer { void Flush(); }

		public IBatchRenderer CurrentBatchRenderer
		{
			get => currentBatchRenderer;

			set
			{
				if (currentBatchRenderer == value)
					return;
				currentBatchRenderer?.Flush();
				currentBatchRenderer = value;
			}
		}

		public IFrameBuffer CreateFrameBuffer(Size s)
		{
			return Context.CreateFrameBuffer(s);
		}

		public IShader CreateShader(IShaderBindings bindings)
		{
			return Context.CreateShader(bindings);
		}

		public IVertexBuffer<T> CreateVertexBuffer<T>(T[] data, bool dynamic) where T : struct
		{
			return Context.CreateVertexBuffer(data, dynamic);
		}

		public void EnableScissor(Rectangle rect)
		{
			// Must remain inside the current scissor rect
			if (scissorState.Count > 0)
				rect = Rectangle.Intersect(rect, scissorState.Peek());

			Flush();

			if (renderType == RenderType.World)
			{
				var r = Rectangle.FromLTRB(
					rect.Left / WorldDownscaleFactor,
					rect.Top / WorldDownscaleFactor,
					(rect.Right + WorldDownscaleFactor - 1) / WorldDownscaleFactor,
					(rect.Bottom + WorldDownscaleFactor - 1) / WorldDownscaleFactor);
				worldBuffer.EnableScissor(r);
			}
			else
				Context.EnableScissor(rect.X, rect.Y, rect.Width, rect.Height);

			scissorState.Push(rect);
		}

		public void DisableScissor()
		{
			scissorState.Pop();
			Flush();

			if (renderType == RenderType.World)
			{
				// Restore previous scissor rect
				if (scissorState.Count > 0)
				{
					var rect = scissorState.Peek();
					var r = Rectangle.FromLTRB(
						rect.Left / WorldDownscaleFactor,
						rect.Top / WorldDownscaleFactor,
						(rect.Right + WorldDownscaleFactor - 1) / WorldDownscaleFactor,
						(rect.Bottom + WorldDownscaleFactor - 1) / WorldDownscaleFactor);
					worldBuffer.EnableScissor(r);
				}
				else
					worldBuffer.DisableScissor();
			}
			else
			{
				// Restore previous scissor rect
				if (scissorState.Count > 0)
				{
					var rect = scissorState.Peek();
					Context.EnableScissor(rect.X, rect.Y, rect.Width, rect.Height);
				}
				else
					Context.DisableScissor();
			}
		}

		public void EnableDepthBuffer()
		{
			Flush();
			Context.EnableDepthBuffer();
		}

		public void DisableDepthBuffer()
		{
			Flush();
			Context.DisableDepthBuffer();
		}

		public void ClearDepthBuffer()
		{
			Flush();
			Context.ClearDepthBuffer();
		}

		public void EnableAntialiasingFilter()
		{
			if (renderType != RenderType.UI)
				throw new InvalidOperationException($"EndFrame called with renderType = {renderType}, expected RenderType.UI.");

			Flush();
			SpriteRenderer.EnablePixelArtScaling(true);
		}

		public void DisableAntialiasingFilter()
		{
			if (renderType != RenderType.UI)
				throw new InvalidOperationException($"EndFrame called with renderType = {renderType}, expected RenderType.UI.");

			Flush();
			SpriteRenderer.EnablePixelArtScaling(false);
		}

		public void GrabWindowMouseFocus()
		{
			Window.GrabWindowMouseFocus();
		}

		public void ReleaseWindowMouseFocus()
		{
			Window.ReleaseWindowMouseFocus();
		}

		public void SaveScreenshot(string path)
		{
			// Pull the data from the Texture directly to prevent the sheet from buffering it
			var src = screenBuffer.Texture.GetData();
			var srcWidth = screenSprite.Sheet.Size.Width;
			var destWidth = screenSprite.Bounds.Width;
			var destHeight = -screenSprite.Bounds.Height;

			ThreadPool.QueueUserWorkItem(_ =>
			{
				// Extract the screen rect from the (larger) backing surface
				var dest = new byte[4 * destWidth * destHeight];
				for (var y = 0; y < destHeight; y++)
					Array.Copy(src, 4 * y * srcWidth, dest, 4 * y * destWidth, 4 * destWidth);

				new Png(dest, SpriteFrameType.Bgra32, destWidth, destHeight).Save(path);
			});
		}

		public void Dispose()
		{
			worldBuffer?.Dispose();
			screenBuffer.Dispose();
			bufferSnapshot.Dispose();
			tempVertexBuffer.Dispose();
			quadIndexBuffer.Dispose();
			fontSheetBuilder?.Dispose();
			if (Fonts != null)
				foreach (var font in Fonts.Values)
					font.Dispose();
			Window.Dispose();
		}

		public void SetVSyncEnabled(bool enabled)
		{
			Window.Context.SetVSyncEnabled(enabled);
		}

		public string GetClipboardText()
		{
			return Window.GetClipboardText();
		}

		public bool SetClipboardText(string text)
		{
			return Window.SetClipboardText(text);
		}

		public bool TryOpenUrl(string url)
		{
			return Window.TryOpenUrl(url);
		}

		public string GLVersion => Context.GLVersion;

		public int DisplayCount => Window.DisplayCount;

		public int CurrentDisplay => Window.CurrentDisplay;
	}
}