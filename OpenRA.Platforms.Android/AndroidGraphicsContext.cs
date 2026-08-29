#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port — OpenGL ES 3.0 graphics backend.
 */
#endregion

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Android.Opengl;
using Java.Nio;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	static class GlesBuffers
	{
		public static ByteBuffer ToByteBuffer(byte[] data)
		{
			var bb = ByteBuffer.AllocateDirect(data.Length);
			bb.Order(ByteOrder.NativeOrder());
			bb.Put(data);
			bb.Position(0);
			return bb;
		}

		public static ByteBuffer ToByteBuffer(float[] data)
		{
			var bb = ByteBuffer.AllocateDirect(data.Length * 4);
			bb.Order(ByteOrder.NativeOrder());
			var fb = bb.AsFloatBuffer();
			fb.Put(data);
			bb.Position(0);
			return bb;
		}

		public static ByteBuffer ToByteBuffer<T>(T[] data, int length, int elementSize) where T : struct
		{
			var bytes = length * elementSize;
			var bb = ByteBuffer.AllocateDirect(bytes);
			bb.Order(ByteOrder.NativeOrder());
			var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
			try
			{
				var tmp = ArrayPool<byte>.Shared.Rent(bytes);
				try
				{
					Marshal.Copy(handle.AddrOfPinnedObject(), tmp, 0, bytes);
					bb.Put(tmp, 0, bytes);
					bb.Position(0);
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(tmp, clearArray: false);
				}
			}
			finally
			{
				handle.Free();
			}

			return bb;
		}

		public static ByteBuffer SliceElements(ByteBuffer source, int startElements, int elementSize, int count)
		{
			var start = startElements * elementSize;
			var len = count * elementSize;
			var bb = ByteBuffer.AllocateDirect(len);
			bb.Order(ByteOrder.NativeOrder());
			var tmp = new byte[len];
			var dup = (ByteBuffer)source.Duplicate();
			dup.Position(start);
			dup.Get(tmp, 0, len);
			bb.Put(tmp);
			bb.Position(0);
			return bb;
		}
	}

	/// <summary>
	/// GLES validation helpers. Desktop OpenRA calls OpenGL.CheckGLError() after nearly every
	/// GL call; we batch a drain of the error flag at strategic points and rate-limit logs.
	/// </summary>
	static class GlDiagnostics
	{
		public static bool Verbose;

		static readonly HashSet<string> LoggedContexts = new();
		static readonly object Gate = new();
		static int totalErrors;
		static bool capsLogged;

		/// <summary>Drain glGetError until NO_ERROR; log each new context+code once.
		/// Conditional(DEBUG): call sites compile away in Release — avoids string allocs in the render loop.</summary>
		[Conditional("DEBUG")]
		public static void Check(string context)
		{
			// Drain the full error queue (desktop CheckGLError only reads one, but drivers
			// can stack multiple flags; leaving them poisons the next Check).
			for (var i = 0; i < 8; i++)
			{
				var err = GLES20.GlGetError();
				if (err == GLES20.GlNoError)
					return;

				totalErrors++;
				var key = context + ":0x" + err.ToString("X");
				var first = false;
				lock (Gate)
				first = LoggedContexts.Add(key);

				if (first)
				{
					AndroidPlatformLog.Error("OpenRA.GL.Error",
											 context + ": glGetError=0x" + err.ToString("X") + " (" + GlErrorName(err) + ")"
											 + " totalErrors=" + totalErrors);
				}
			}
		}

		/// <summary>One-shot dump of renderer caps (compare with desktop GL for limits).</summary>
		public static void LogCapsOnce()
		{
			if (capsLogged || !AndroidEgl.IsReady)
				return;
			capsLogged = true;
			try
			{
				var vendor = GLES20.GlGetString(GLES20.GlVendor) ?? "?";
				var renderer = GLES20.GlGetString(GLES20.GlRenderer) ?? "?";
				var version = GLES20.GlGetString(GLES20.GlVersion) ?? "?";
				var maxTex = new int[1];
				GLES20.GlGetIntegerv(GLES20.GlMaxTextureSize, maxTex, 0);
				var maxRb = new int[1];
				GLES20.GlGetIntegerv(0x84E8 /* GL_MAX_RENDERBUFFER_SIZE */, maxRb, 0);
				var maxVattribs = new int[1];
				GLES20.GlGetIntegerv(0x8869 /* GL_MAX_VERTEX_ATTRIBS */, maxVattribs, 0);
				AndroidPlatformLog.Info("OpenRA.GL.Caps",
										"vendor=" + vendor + " renderer=" + renderer + " version=" + version
										+ " MAX_TEXTURE_SIZE=" + maxTex[0]
										+ " MAX_RENDERBUFFER_SIZE=" + maxRb[0]
										+ " MAX_VERTEX_ATTRIBS=" + maxVattribs[0]
										+ " surface=" + AndroidEgl.SurfaceWidth + "x" + AndroidEgl.SurfaceHeight);
			}
			catch (Exception e)
			{
				AndroidPlatformLog.Warn("OpenRA.GL.Caps", e.Message);
			}
		}

		static int viewportLogCounter;

		/// <summary>Rate-limited dump of GL viewport + Android window sizes (corner-shift diagnosis).</summary>
		public static void LogViewportState(string context)
		{
			viewportLogCounter++;
			if (viewportLogCounter > 5 && viewportLogCounter % 300 != 0)
				return;

			var vp = new int[4];
			GLES20.GlGetIntegerv(0x0BA2 /* GL_VIEWPORT */, vp, 0);
			var sc = new int[4];
			GLES20.GlGetIntegerv(0x0C10 /* GL_SCISSOR_BOX */, sc, 0);
			var scOn = new int[1];
			GLES20.GlGetIntegerv(0x0C11 /* GL_SCISSOR_TEST */, scOn, 0);
			var fb = new int[1];
			GLES20.GlGetIntegerv(0x8CA6 /* GL_FRAMEBUFFER_BINDING */, fb, 0);

			var win = AndroidPlatformWindow.Current;
			var native = win != null ? win.NativeWindowSize.Width + "x" + win.NativeWindowSize.Height : "?";
			var effective = win != null ? win.EffectiveWindowSize.Width + "x" + win.EffectiveWindowSize.Height : "?";
			var surface = win != null ? win.SurfaceSize.Width + "x" + win.SurfaceSize.Height : "?";
			var scale = win != null ? win.EffectiveWindowScale.ToString("0.###") : "?";

			AndroidPlatformLog.Info("OpenRA.GL.View",
									context
									+ " vp=[" + vp[0] + "," + vp[1] + "," + vp[2] + "," + vp[3] + "]"
									+ " scissor=" + (scOn[0] != 0 ? "ON" : "off")
									+ " sc=[" + sc[0] + "," + sc[1] + "," + sc[2] + "," + sc[3] + "]"
									+ " fbo=" + fb[0]
									+ " native=" + native
									+ " effective=" + effective
									+ " surface=" + surface
									+ " effScale=" + scale
									+ " egl=" + AndroidEgl.SurfaceWidth + "x" + AndroidEgl.SurfaceHeight);
		}

		public static void CheckFramebuffer(string context, int status)
		{
			if (status == GLES20.GlFramebufferComplete)
				return;
			AndroidPlatformLog.Error("OpenRA.GL.FBO",
									 context + ": incomplete status=0x" + status.ToString("X")
									 + " (" + FboStatusName(status) + ")");
		}

		static string GlErrorName(int err) => err switch
		{
			0x0500 => "GL_INVALID_ENUM",
			0x0501 => "GL_INVALID_VALUE",
			0x0502 => "GL_INVALID_OPERATION",
			0x0503 => "GL_STACK_OVERFLOW",
			0x0504 => "GL_STACK_UNDERFLOW",
			0x0505 => "GL_OUT_OF_MEMORY",
			0x0506 => "GL_INVALID_FRAMEBUFFER_OPERATION",
			_ => "UNKNOWN"
		};

		static string FboStatusName(int s) => s switch
		{
			0x8CD7 => "GL_FRAMEBUFFER_INCOMPLETE_ATTACHMENT",
			0x8CD9 => "GL_FRAMEBUFFER_INCOMPLETE_DIMENSIONS",
			0x8CD6 => "GL_FRAMEBUFFER_INCOMPLETE_MISSING_ATTACHMENT",
			0x8CDD => "GL_FRAMEBUFFER_UNSUPPORTED",
			_ => "OTHER"
		};
	}

	sealed class AndroidGraphicsContext : IGraphicsContext
	{
		int vao;
		static AndroidGraphicsContext live;

		public string GLVersion
		{
			get
			{
				if (!AndroidEgl.IsReady)
					return "OpenGL ES (no surface)";
				var v = GLES20.GlGetString(GLES20.GlVersion);
				return string.IsNullOrEmpty(v) ? "OpenGL ES 3.0" : v;
			}
		}

		public AndroidGraphicsContext()
		{
			live = this;
			TryInitVao();
		}

		void TryInitVao()
		{
			if (!AndroidEgl.IsReady || vao != 0)
				return;

			AndroidEgl.MakeCurrent();
			var ids = new int[1];
			GLES30.GlGenVertexArrays(1, ids, 0);
			vao = ids[0];
			GLES30.GlBindVertexArray(vao);
			GlDiagnostics.Check("TryInitVao (GenVertexArrays/BindVertexArray)");
			GlDiagnostics.LogCapsOnce();
			if (vao == 0)
				AndroidPlatformLog.Error("OpenRA.GL", "TryInitVao: glGenVertexArrays returned 0 — no VAO bound");
		}

		public void Clear()
		{
			TryInitVao();
			GLES20.GlClearColor(0, 0, 0, 1);
			GLES20.GlClear(GLES20.GlColorBufferBit | GLES20.GlDepthBufferBit);
			GlDiagnostics.Check("Clear");
		}

		public void ClearDepthBuffer() => GLES20.GlClear(GLES20.GlDepthBufferBit);

		public void EnableDepthBuffer()
		{
			GLES20.GlClear(GLES20.GlDepthBufferBit);
			GLES20.GlEnable(GLES20.GlDepthTest);
			GLES20.GlDepthFunc(GLES20.GlLequal);
			GLES20.GlDepthMask(true);
		}

		public void DisableDepthBuffer() => GLES20.GlDisable(GLES20.GlDepthTest);

		static int scissorLogCount;
		public void EnableScissor(int x, int y, int width, int height)
		{
			if (width < 0) width = 0;
			if (height < 0) height = 0;

			// Match Sdl2GraphicsContext: layout coords are EffectiveWindowSize;
			// GL scissor is surface pixels. Scale by EffectiveWindowScale when they differ.
			var window = AndroidPlatformWindow.Current;
			if (window != null)
			{
				var effective = window.EffectiveWindowSize;
				var surface = window.SurfaceSize;
				if (effective.Width != surface.Width || effective.Height != surface.Height)
				{
					var scale = window.EffectiveWindowScale;
					if (scale <= 0f)
						scale = 1f;
					x = (int)Math.Round(scale * x);
					y = (int)Math.Round(scale * y);
					width = (int)Math.Round(scale * width);
					height = (int)Math.Round(scale * height);
				}
			}
			if (width < 1) width = 1;
			if (height < 1) height = 1;

			GLES20.GlEnable(GLES20.GlScissorTest);
			GLES20.GlScissor(x, y, width, height);
			scissorLogCount++;
			if (scissorLogCount <= 8 || scissorLogCount % 500 == 0)
				AndroidPlatformLog.Info("OpenRA.GL.Scissor",
										"#" + scissorLogCount + " xywh=[" + x + "," + y + "," + width + "," + height + "]"
										+ " egl=" + AndroidEgl.SurfaceWidth + "x" + AndroidEgl.SurfaceHeight);
		}

		public void DisableScissor() => GLES20.GlDisable(GLES20.GlScissorTest);

		static int presentCount;

		public void Present()
		{
			if (!AndroidEgl.MakeCurrent())
			{
				if (presentCount < 5)
					AndroidPlatformLog.Warn("OpenRA.GL", "Present: MakeCurrent failed: " + AndroidEgl.LastError);
				return;
			}

			// Ensure we present the default framebuffer at full surface size.
			GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
			GLES20.GlDisable(GLES20.GlScissorTest);
			var w = Math.Max(1, AndroidEgl.SurfaceWidth);
			var h = Math.Max(1, AndroidEgl.SurfaceHeight);
			GLES20.GlViewport(0, 0, w, h);
			GlDiagnostics.Check("Present (BindFramebuffer/Viewport)");

			AndroidEgl.SwapBuffers();
			presentCount++;
			if (presentCount <= 5 || presentCount % 300 == 0)
			{
				AndroidPlatformLog.Info("OpenRA.GL", "Present #" + presentCount + " surface=" + w + "x" + h);
				GlDiagnostics.LogViewportState("Present#" + presentCount);
			}
		}

		public void SetBlendMode(BlendMode mode)
		{
			// Every case here must match OpenRA.Platforms.Default/Sdl2GraphicsContext.cs
			// exactly — this was previously grouping several distinct blend modes together
			// under one mode's blend func (Translucent used Alpha's func, Multiplicative and
			// DoubleMultiplicative both used Multiply's func, LowAdditive used Additive's
			// func, Screen had the wrong source factor), and — most impactfully — Alpha
			// itself used GL_SRC_ALPHA as the source factor instead of GL_ONE. OpenRA sprite
			// textures are premultiplied-alpha (see Util.PremultiplyAlpha, used throughout
			// the sprite pipeline), and premultiplied-alpha blending requires GL_ONE as the
			// source factor — GL_SRC_ALPHA double-applies the alpha multiplication (once
			// already baked into the premultiplied source color, once again via the blend
			// factor), systematically darkening every pixel with alpha < 1. Since Alpha is
			// the default blend mode for nearly all sprite rendering — terrain, units,
			// buildings, UI — this affected virtually everything on screen, not just one
			// effect.
			GLES20.GlBlendEquation(GLES20.GlFuncAdd);
			switch (mode)
			{
				case BlendMode.None:
					GLES20.GlDisable(GLES20.GlBlend);
					break;
				case BlendMode.Alpha:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlOne, GLES20.GlOneMinusSrcAlpha);
					break;
				case BlendMode.Additive:
				case BlendMode.Subtractive:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlOne, GLES20.GlOne);
					if (mode == BlendMode.Subtractive)
						GLES20.GlBlendEquationSeparate(GLES20.GlFuncReverseSubtract, GLES20.GlFuncAdd);
				break;
				case BlendMode.Multiply:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlDstColor, GLES20.GlOneMinusSrcAlpha);
					break;
				case BlendMode.Multiplicative:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlZero, GLES20.GlSrcColor);
					break;
				case BlendMode.DoubleMultiplicative:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlDstColor, GLES20.GlSrcColor);
					break;
				case BlendMode.LowAdditive:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlDstColor, GLES20.GlOne);
					break;
				case BlendMode.Screen:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlSrcColor, GLES20.GlOneMinusSrcColor);
					break;
				case BlendMode.Translucent:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlDstColor, GLES20.GlOneMinusDstColor);
					break;
			}
		}

		public void SetVSyncEnabled(bool enabled) { }

		static int ModeFromPrimitiveType(PrimitiveType pt) => pt switch
		{
			PrimitiveType.PointList => GLES20.GlPoints,
			PrimitiveType.LineList => GLES20.GlLines,
			_ => GLES20.GlTriangles
		};

		void EnsureVaoBound()
		{
			TryInitVao();
			if (vao != 0)
				GLES30.GlBindVertexArray(vao);
		}

		/// <summary>Shader.Bind must record attrib pointers while the context VAO is bound.</summary>
		public static void BindVaoForAttributes()
		{
			live?.EnsureVaoBound();
		}

		static int loggedPrimitiveViewport;

		public void DrawPrimitives(PrimitiveType pt, int firstVertex, int numVertices)
		{
			EnsureVaoBound();

			// Targeted diagnostic: query the ACTUAL GL_VIEWPORT/GL_SCISSOR_BOX/framebuffer
			// binding at the exact moment a small (likely full-screen-effect) quad draws.
			// DrawBatch's own logging (Renderer.cs) confirmed the vertex count/offset
			// reaching this call are correct (first=0, n=6) for the post-process tint quad,
			// which rules out a missing-triangle/vertex-count bug — so if the quad still
			// doesn't cover the full screen, the remaining explanation is that GL_VIEWPORT
			// itself is smaller than the actual render target at draw time (leftover from
			// an earlier, unrelated glViewport/scissor call), which would make the NDC
			// -1..1 quad only fill that smaller region rather than the whole target.
			if (numVertices <= 6 && loggedPrimitiveViewport < 12)
			{
				loggedPrimitiveViewport++;
				var vp = new int[4];
				GLES20.GlGetIntegerv(0x0BA2 /* GL_VIEWPORT */, vp, 0);
				var scOn = new int[1];
				GLES20.GlGetIntegerv(0x0C11 /* GL_SCISSOR_TEST */, scOn, 0);
				var sc = new int[4];
				GLES20.GlGetIntegerv(0x0C10 /* GL_SCISSOR_BOX */, sc, 0);
				var fb = new int[1];
				GLES20.GlGetIntegerv(0x8CA6 /* GL_FRAMEBUFFER_BINDING */, fb, 0);
				AndroidPlatformLog.Info("OpenRA.GL.PrimVp",
					"#" + loggedPrimitiveViewport + " " + pt + " first=" + firstVertex + " n=" + numVertices +
					" viewport=[" + vp[0] + "," + vp[1] + "," + vp[2] + "," + vp[3] + "]" +
					" scissor=" + (scOn[0] != 0 ? "on" : "off") +
					" scissorBox=[" + sc[0] + "," + sc[1] + "," + sc[2] + "," + sc[3] + "]" +
					" fbo=" + fb[0]);
			}

			GLES20.GlDrawArrays(ModeFromPrimitiveType(pt), firstVertex, numVertices);
			GlDiagnostics.Check("DrawPrimitives(" + pt + ", first=" + firstVertex + ", n=" + numVertices + ")");
		}

		public void DrawElements(int numIndices, int offset)
		{
			EnsureVaoBound();
			// offset is byte offset into bound ELEMENT_ARRAY_BUFFER (desktop: new IntPtr(offset)).
			GLES20.GlDrawElements(GLES20.GlTriangles, numIndices, GLES20.GlUnsignedInt, offset);
			GlDiagnostics.Check("DrawElements(n=" + numIndices + ", offset=" + offset + ")");
		}

		public IVertexBuffer<T> CreateEmptyVertexBuffer<T>(int size) where T : struct
			=> new AndroidVertexBuffer<T>(size);

			public IVertexBuffer<T> CreateVertexBuffer<T>(T[] data, bool dynamic = true) where T : struct
				=> new AndroidVertexBuffer<T>(data, dynamic);

				public T[] CreateVertices<T>(int size) where T : struct => new T[size];

				public IIndexBuffer CreateIndexBuffer(uint[] indices) => new AndroidIndexBuffer(indices);
				public ITexture CreateTexture() => new AndroidTexture();
				public IFrameBuffer CreateFrameBuffer(Size s) => new AndroidFrameBuffer(s, Color.FromArgb(0));
				public IFrameBuffer CreateFrameBuffer(Size s, Color clearColor) => new AndroidFrameBuffer(s, clearColor);
				public IShader CreateShader(IShaderBindings shaderBindings) => new AndroidShader(shaderBindings);

				public void Dispose()
				{
					if (vao != 0)
					{
						GLES30.GlDeleteVertexArrays(1, new[] { vao }, 0);
						vao = 0;
					}
				}
	}

	sealed class AndroidVertexBuffer<T> : IVertexBuffer<T> where T : struct
	{
		readonly int buffer;
		readonly int elementSize;
		readonly bool dynamic;
		ByteBuffer uploadBuffer; // grown once, reused every SetData

		public AndroidVertexBuffer(int size)
		{
			elementSize = Marshal.SizeOf<T>();
			dynamic = true;
			var ids = new int[1];
			GLES20.GlGenBuffers(1, ids, 0);
			buffer = ids[0];
			GLES20.GlBindBuffer(GLES20.GlArrayBuffer, buffer);
			GLES20.GlBufferData(GLES20.GlArrayBuffer, size * elementSize, null, GLES20.GlDynamicDraw);
			GlDiagnostics.Check("VertexBuffer(empty, size=" + size + ")");
		}

		public AndroidVertexBuffer(T[] data, bool dynamic)
		{
			elementSize = Marshal.SizeOf<T>();
			this.dynamic = dynamic;
			var ids = new int[1];
			GLES20.GlGenBuffers(1, ids, 0);
			buffer = ids[0];
			SetData(data, data.Length);
		}

		public void Bind() => GLES20.GlBindBuffer(GLES20.GlArrayBuffer, buffer);

		public void SetData(T[] vertices, int length)
		{
			SetData(vertices, 0, 0, length);
		}

		public void SetData(ref T[] vertices, int length) => SetData(vertices, 0, 0, length);

		/// <summary>
		/// Desktop semantics: offset = source array index, start = GPU buffer index.
		/// Pools the direct ByteBuffer + scratch byte[] to avoid per-frame Gen0 pressure.
		/// </summary>
		public void SetData(T[] vertices, int offset, int start, int length)
		{
			if (vertices == null || length <= 0)
				return;

			GLES20.GlBindBuffer(GLES20.GlArrayBuffer, buffer);

			var byteLen = length * elementSize;
			if (uploadBuffer == null || uploadBuffer.Capacity() < byteLen)
			{
				uploadBuffer = ByteBuffer.AllocateDirect(byteLen);
				uploadBuffer.Order(ByteOrder.NativeOrder());
			}
			else
			{
				uploadBuffer.Clear();
			}

			var handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
			try
			{
				var src = handle.AddrOfPinnedObject() + offset * elementSize;
				var tmp = ArrayPool<byte>.Shared.Rent(byteLen);
				try
				{
					Marshal.Copy(src, tmp, 0, byteLen);
					uploadBuffer.Put(tmp, 0, byteLen);
					uploadBuffer.Position(0);

					if (start == 0 && offset == 0 && length == vertices.Length)
					{
						var usage = dynamic ? GLES20.GlDynamicDraw : GLES20.GlStaticDraw;
						GLES20.GlBufferData(GLES20.GlArrayBuffer, byteLen, uploadBuffer, usage);
					}
					else
					{
						GLES20.GlBufferSubData(GLES20.GlArrayBuffer, start * elementSize, byteLen, uploadBuffer);
					}
					GlDiagnostics.Check("VertexBuffer.SetData(off=" + offset + ",start=" + start + ",len=" + length + ")");
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(tmp, clearArray: false);
				}
			}
			finally
			{
				handle.Free();
			}
		}

		public void Dispose()
		{
			if (buffer != 0)
				GLES20.GlDeleteBuffers(1, new[] { buffer }, 0);
			uploadBuffer = null;
		}
	}

	sealed class AndroidIndexBuffer : IIndexBuffer
	{
		readonly int buffer;

		public AndroidIndexBuffer(uint[] indices)
		{
			var ids = new int[1];
			GLES20.GlGenBuffers(1, ids, 0);
			buffer = ids[0];
			var byteLen = indices.Length * 4;
			var bytes = ArrayPool<byte>.Shared.Rent(byteLen);
			try
			{
				System.Buffer.BlockCopy(indices, 0, bytes, 0, byteLen);
				var bb = ByteBuffer.AllocateDirect(byteLen);
				bb.Order(ByteOrder.NativeOrder());
				bb.Put(bytes, 0, byteLen);
				bb.Position(0);
				GLES20.GlBindBuffer(GLES20.GlElementArrayBuffer, buffer);
				GLES20.GlBufferData(GLES20.GlElementArrayBuffer, byteLen, bb, GLES20.GlStaticDraw);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(bytes, clearArray: false);
			}
		}

		public void Bind() => GLES20.GlBindBuffer(GLES20.GlElementArrayBuffer, buffer);

		public void Dispose()
		{
			if (buffer != 0)
				GLES20.GlDeleteBuffers(1, new[] { buffer }, 0);
		}
	}

	sealed class AndroidTexture : ITexture
	{
		// ES3 sized internal format (valid for color-renderable FBO attachments).
		// Default/Texture.cs wrongly used GL_BGRA (0x80E1) as *internal* format on Embedded —
		// that is only valid as the *format* param with EXT_texture_format_BGRA8888.
		const int GL_RGBA8 = 0x8058;       // FBO color-renderable (SetEmpty / attachments)
		const int GL_BGRA_EXT = 0x80E1;    // pixel format (EXT_texture_format_BGRA8888)
		const int GL_BGRA8_EXT = 0x93A1;   // sized internal format for Embedded sprite uploads

		static bool loggedBgraOk;

		// Whether GL_BGRA8_EXT (EXT_texture_format_BGRA8888) uploads actually work on this
		// device's driver. Null = not yet determined. This is NOT universal across Android
		// GPUs: it works on Mali (which is why the direct-upload path above exists at all —
		// it avoids a CPU-side channel swap on devices that support it), but Adreno GPUs
		// (confirmed on an Adreno 610) reject the GL_BGRA8_EXT enum outright with
		// GL_INVALID_VALUE (0x501) — not the GL_INVALID_OPERATION Mali gives when it merely
		// dislikes a parameter combination, but "I don't recognize this enum" — meaning the
		// extension isn't supported at all on that hardware. Previously there was no
		// fallback at all: every texture upload failed silently, leaving every sprite,
		// palette, and UI texture completely empty forever — exactly a black screen
		// followed eventually by a crash once enough downstream code hit unusable texture
		// state. Determined once rather than retried on every single texture upload.
		static bool? bgra8ExtSupported;

		int texture;
		Size size;
		// Match desktop Texture default (Linear). Palette sheets forced Nearest in SetData.
		// Matches desktop's OpenRA.Platforms.Default/Texture.cs, where this field has no
		// initializer and so implicitly defaults to TextureScaleFilter.Nearest (value 0).
		// This was previously explicitly defaulted to Linear here — for any texture the
		// shared/cross-platform code doesn't explicitly set a filter on (relying on the
		// same implicit-Nearest default desktop has), Android would sample with bilinear
		// filtering instead. Most OpenRA texture data is palette-indexed: blending two
		// different palette *indices* together produces a garbage intermediate index,
		// which samples the palette at an unintended row — exactly the pink/magenta
		// fringing visible on detailed sprites (small, palette-dense sprites like infantry
		// are hit hardest, since adjacent pixels are more likely to differ in index).
		TextureScaleFilter scaleFilter = TextureScaleFilter.Nearest;
		ByteBuffer uploadBuffer; // per-texture reusable direct buffer

		ByteBuffer GetUploadBuffer(int minSize)
		{
			if (uploadBuffer != null && uploadBuffer.Capacity() >= minSize)
			{
				uploadBuffer.Clear();
				return uploadBuffer;
			}

			uploadBuffer = ByteBuffer.AllocateDirect(minSize);
			uploadBuffer.Order(ByteOrder.NativeOrder());
			return uploadBuffer;
		}

		public Size Size => size;

		public TextureScaleFilter ScaleFilter
		{
			get => scaleFilter;
			set
			{
				scaleFilter = value;
				ApplyScaleFilter();
			}
		}

		void ApplyScaleFilter()
		{
			if (texture == 0)
				return;
			var filter = scaleFilter == TextureScaleFilter.Linear ? GLES20.GlLinear : GLES20.GlNearest;
			GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
			GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, filter);
			GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, filter);
		}

		public int TextureId
		{
			get
			{
				EnsureTexture();
				return texture;
			}
		}

		void EnsureTexture()
		{
			if (texture != 0)
				return;
			var ids = new int[1];
			GLES20.GlGenTextures(1, ids, 0);
			texture = ids[0];
			GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
			var filt = scaleFilter == TextureScaleFilter.Linear ? GLES20.GlLinear : GLES20.GlNearest;
			GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, filt);
			GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, filt);
			GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
			GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
			// Match desktop PrepareTexture — required for complete texture state on ES3
			GLES20.GlTexParameteri(GLES20.GlTexture2d, 0x813C /* GL_TEXTURE_BASE_LEVEL */, 0);
			GLES20.GlTexParameteri(GLES20.GlTexture2d, 0x813D /* GL_TEXTURE_MAX_LEVEL */, 0);
			GLES20.GlPixelStorei(0x0CF5 /* GL_UNPACK_ALIGNMENT */, 1);
			GlDiagnostics.Check("Texture.EnsureTexture (GenTextures)");
			if (texture == 0)
				AndroidPlatformLog.Error("OpenRA.GL", "EnsureTexture: glGenTextures returned 0");
		}

		public void SetData(byte[] colors, int width, int height)
		{
			EnsureTexture();
			size = new Size(width, height);
			GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
			ApplyScaleFilter();

			// Preferred path: match OpenRA.Platforms.Default.Texture for Embedded —
			//   internal = GL_BGRA8_EXT (0x93A1), format = GL_BGRA (0x80E1)
			// OpenRA sprite bytes are BGRA; this avoids any CPU-side reordering when the
			// driver supports the extension (confirmed working on Mali). Falls back to
			// RGBA8 internal format with an explicit R/B channel swap when it doesn't
			// (confirmed necessary on Adreno) — see bgra8ExtSupported above for why this
			// is only probed once rather than on every call.
			if (bgra8ExtSupported != false)
			{
				var bb = GetUploadBuffer(colors.Length);
				bb.Put(colors);
				bb.Position(0);
				while (GLES20.GlGetError() != GLES20.GlNoError) { }
				GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GL_BGRA8_EXT, width, height, 0,
									GL_BGRA_EXT, GLES20.GlUnsignedByte, bb);
				var err = GLES20.GlGetError();
				if (err == GLES20.GlNoError)
				{
					bgra8ExtSupported = true;
					if (!loggedBgraOk)
					{
						loggedBgraOk = true;
						AndroidPlatformLog.Info("OpenRA.GL",
												"SetData BGRA8_EXT OK (first) " + width + "x" + height);
					}

					ApplyPaletteFilterIfNeeded(width);
					return;
				}

				bgra8ExtSupported = false;
				AndroidPlatformLog.Warn("OpenRA.GL",
										"GL_BGRA8_EXT upload failed 0x" + err.ToString("X") +
										" — driver does not support EXT_texture_format_BGRA8888;" +
										" using RGBA8 with CPU-side R/B swap for all textures from now on");
			}

			// CRITICAL: colors[] is BGRA. Must R/B swap on CPU for RGBA upload (Adreno path).
			// Use ArrayPool to avoid allocating a full-frame buffer every upload.
			var swapped = ArrayPool<byte>.Shared.Rent(colors.Length);
			try
			{
				for (var i = 0; i + 3 < colors.Length; i += 4)
				{
					swapped[i] = colors[i + 2];     // R <- B
					swapped[i + 1] = colors[i + 1]; // G
					swapped[i + 2] = colors[i];     // B <- R
					swapped[i + 3] = colors[i + 3]; // A
				}

				var swappedBb = GetUploadBuffer(colors.Length);
				swappedBb.Put(swapped, 0, colors.Length);
				swappedBb.Position(0);
				GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, width, height, 0,
									GLES20.GlRgba, GLES20.GlUnsignedByte, swappedBb);
				GlDiagnostics.Check("Texture.SetData RGBA fallback (R/B swapped) " + width + "x" + height);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(swapped, clearArray: false);
			}

			ApplyPaletteFilterIfNeeded(width);
		}

		void ApplyPaletteFilterIfNeeded(int width)
		{
			// Palette sheets are 256×N; linear filtering interpolates indices → noisy unit sprites.
			if (width == 256)
			{
				scaleFilter = TextureScaleFilter.Nearest;
				ApplyScaleFilter();
			}
			else
				ApplyScaleFilter();
		}

		public void SetFloatData(float[] data, int width, int height)
		{
			EnsureTexture();
			size = new Size(width, height);
			var bb = GetUploadBuffer(data.Length * 4);
			var fb = bb.AsFloatBuffer();
			fb.Put(data);
			bb.Position(0);
			GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
			GLES30.GlTexImage2D(GLES20.GlTexture2d, 0, GLES30.GlRgba16f, width, height, 0,
								GLES20.GlRgba, GLES20.GlFloat, bb);
		}

		public void SetEmpty(int width, int height)
		{
			EnsureTexture();
			size = new Size(width, height);
			GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
			ApplyScaleFilter();
			while (GLES20.GlGetError() != GLES20.GlNoError) { }

			// FBO color attachments must be color-renderable. On Mali, GL_BGRA8_EXT often
			// accepts TexImage2D with null data but the FBO is then INCOMPLETE → black world
			// with only some UI/sprites surviving. Always use RGBA8 for empty FBO textures.
			// Sprite uploads still use BGRA8_EXT in SetData().
			GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GL_RGBA8, width, height, 0,
								GLES20.GlRgba, GLES20.GlUnsignedByte, null);
			var err = GLES20.GlGetError();
			if (err != GLES20.GlNoError)
				AndroidPlatformLog.Error("OpenRA.GL",
										 "SetEmpty RGBA8 " + width + "x" + height + " glError=0x" + err.ToString("X")
										 + (err == 0x505 ? " GL_OUT_OF_MEMORY — world FBO/sheets may be incomplete" : ""));
			else
				GlDiagnostics.Check("Texture.SetEmpty RGBA8 " + width + "x" + height);
		}

		public void SetDataFromReadBuffer(Rectangle rect)
		{
			EnsureTexture();
			size = new Size(rect.Width, rect.Height);
			GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
			// CopyTexImage2D internal format must be sized RGBA8 on ES3 — not GL_BGRA.
			GLES20.GlCopyTexImage2D(GLES20.GlTexture2d, 0, GL_RGBA8, rect.Left, rect.Top, rect.Width, rect.Height, 0);
			GlDiagnostics.Check("Texture.SetDataFromReadBuffer RGBA8");
		}

		public byte[] GetData()
		{
			var w = Math.Max(1, size.Width);
			var h = Math.Max(1, size.Height);
			return new byte[w * h * 4];
		}

		public void Dispose()
		{
			if (texture != 0)
			{
				GLES20.GlDeleteTextures(1, new[] { texture }, 0);
				texture = 0;
			}
			uploadBuffer = null;
		}
	}

	sealed class AndroidFrameBuffer : IFrameBuffer
	{
		readonly Size size;
		readonly Color clearColor;
		readonly AndroidTexture texture = new();
		int framebuffer;
		int depth;
		readonly int[] savedViewport = new int[4];

		public ITexture Texture => texture;

		public AndroidFrameBuffer(Size size, Color clearColor)
		{
			this.size = size;
			this.clearColor = clearColor;

			var fb = new int[1];
			GLES20.GlGenFramebuffers(1, fb, 0);
			framebuffer = fb[0];
			GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, framebuffer);

			texture.SetEmpty(size.Width, size.Height);
			GLES20.GlFramebufferTexture2D(GLES20.GlFramebuffer, GLES20.GlColorAttachment0,
										  GLES20.GlTexture2d, texture.TextureId, 0);

			var rb = new int[1];
			GLES20.GlGenRenderbuffers(1, rb, 0);
			depth = rb[0];
			GLES20.GlBindRenderbuffer(GLES20.GlRenderbuffer, depth);
			GLES20.GlRenderbufferStorage(GLES20.GlRenderbuffer, GLES20.GlDepthComponent16, size.Width, size.Height);
			GLES20.GlFramebufferRenderbuffer(GLES20.GlFramebuffer, GLES20.GlDepthAttachment,
											 GLES20.GlRenderbuffer, depth);

			var status = GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer);
			GlDiagnostics.CheckFramebuffer("Create " + size.Width + "x" + size.Height, status);
			AndroidPlatformLog.Info("OpenRA.GL",
									"FrameBuffer.Create " + size.Width + "x" + size.Height
									+ " status=0x" + status.ToString("X")
									+ (status == GLES20.GlFramebufferComplete ? " COMPLETE" : " INCOMPLETE — world will be black"));
			if (status != GLES20.GlFramebufferComplete)
				AndroidPlatformLog.Error("OpenRA.GL",
										 "Framebuffer incomplete 0x" + status.ToString("X")
										 + " size=" + size.Width + "x" + size.Height
										 + " — terrain/world buffer will not render");
				GlDiagnostics.Check("FrameBuffer.Create after status check");

			GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
		}

		static int bindLogCount;
		public void Bind()
		{
			// Desktop FrameBuffer.Bind: glFlush before switch; restore viewport on Unbind.
			GLES20.GlFlush();
			GLES20.GlGetIntegerv(0x0BA2 /* GL_VIEWPORT */, savedViewport, 0);

			// CRITICAL: screen-space scissor left enabled while binding a 2k/4k sheet FBO
			// clips world rendering into a tiny region → "stuck in corner" / scrap terrain.
			GLES20.GlDisable(GLES20.GlScissorTest);
			GLES20.GlDisable(GLES20.GlDepthTest);

			GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, framebuffer);
			GLES20.GlViewport(0, 0, size.Width, size.Height);
			// Always clear opaque — A=0 leaves the blit transparent → black screen with only
			// later opaque sprites (units) visible. Shellmap survived; skirmish did not.
			var ca = clearColor.A / 255f;
			if (ca < 1f) ca = 1f;
			GLES20.GlClearColor(clearColor.R / 255f, clearColor.G / 255f, clearColor.B / 255f, ca);
			GLES20.GlClear(GLES20.GlColorBufferBit | GLES20.GlDepthBufferBit);

			bindLogCount++;
			if (bindLogCount <= 12 || bindLogCount % 300 == 0)
			{
				var status = GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer);
				AndroidPlatformLog.Info("OpenRA.GL.View",
										"FBO.Bind #" + bindLogCount + " " + size.Width + "x" + size.Height
										+ " status=0x" + status.ToString("X")
										+ (status == GLES20.GlFramebufferComplete ? " OK" : " INCOMPLETE")
										+ " clearA=" + ca.ToString("0.##"));
			}
		}

		public void Unbind()
		{
			GLES20.GlFlush();
			// Leave scissor disabled; caller re-enables if needed for screen passes.
			GLES20.GlDisable(GLES20.GlScissorTest);
			GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
			GLES20.GlViewport(savedViewport[0], savedViewport[1], savedViewport[2], savedViewport[3]);
		}

		public void EnableScissor(Rectangle rect)
		{
			GLES20.GlEnable(GLES20.GlScissorTest);
			GLES20.GlScissor(rect.Left, rect.Top, rect.Width, rect.Height);
		}

		public void DisableScissor() => GLES20.GlDisable(GLES20.GlScissorTest);

		public void Dispose()
		{
			if (framebuffer != 0)
			{
				GLES20.GlDeleteFramebuffers(1, new[] { framebuffer }, 0);
				framebuffer = 0;
			}

			if (depth != 0)
			{
				GLES20.GlDeleteRenderbuffers(1, new[] { depth }, 0);
				depth = 0;
			}

			texture.Dispose();
		}
	}

	sealed class AndroidShader : IShader
	{
		int program;
		readonly IShaderBindings bindings;
		readonly Dictionary<string, int> uniformCache = new();
		/// <summary>Sampler name → fixed texture unit (assigned at link, matches desktop Shader).</summary>
		readonly Dictionary<string, int> samplerUnits = new();
		readonly Dictionary<int, ITexture> boundTextures = new();

		public AndroidShader(IShaderBindings bindings)
		{
			this.bindings = bindings;
			var vs = Compile(GLES20.GlVertexShader, AdaptShader(bindings.VertexShaderCode, true));
			var fs = Compile(GLES20.GlFragmentShader, AdaptShader(bindings.FragmentShaderCode, false));

			program = GLES20.GlCreateProgram();
			GLES20.GlAttachShader(program, vs);
			GLES20.GlAttachShader(program, fs);

			if (bindings.Attributes != null)
			{
				for (var i = 0; i < bindings.Attributes.Length; i++)
					GLES20.GlBindAttribLocation(program, i, bindings.Attributes[i].Name);
			}

			GLES20.GlLinkProgram(program);
			var linkStatus = new int[1];
			GLES20.GlGetProgramiv(program, GLES20.GlLinkStatus, linkStatus, 0);
			if (linkStatus[0] == 0)
			{
				var log = GLES20.GlGetProgramInfoLog(program);
				AndroidPlatformLog.Error("OpenRA.GL", "Shader link failed: " + log);
				throw new InvalidProgramException("Shader link failed: " + log);
			}

			GLES20.GlDeleteShader(vs);
			GLES20.GlDeleteShader(fs);

			// Fixed sampler units (desktop Shader.cs parity). ES requires sampler uniforms
			// set while the program is current; values persist for the program lifetime.
			GLES20.GlUseProgram(program);
			var unit = 0;
			string[] samplerNames =
			{
				"Texture0", "Texture1", "Texture2", "Texture3",
				"Texture4", "Texture5", "Texture6", "Texture7",
				"Palette", "ColorShifts"
			};
			foreach (var name in samplerNames)
			{
				var loc = GLES20.GlGetUniformLocation(program, name);
				if (loc < 0)
					continue;
				uniformCache[name] = loc;
				samplerUnits[name] = unit;
				GLES20.GlUniform1i(loc, unit);
				unit++;
			}

			AndroidPlatformLog.Info("OpenRA.GL", "Shader linked: " + bindings.VertexShaderName
			+ " samplers=" + samplerUnits.Count);
		}


		static string AdaptShader(string code, bool vertex)
		{
			if (string.IsNullOrEmpty(code))
				return code;

			// OpenRA glsl uses "#version {VERSION}" (or a literal "#version <n>[ core]") —
			// GLES needs "#version 300 es". Do this in a single pass: running the
			// placeholder Replace and the numeric-version Regex.Replace back-to-back
			// causes the regex to re-match the digits just inserted by the first
			// replacement (e.g. "#version 300 es" -> matches "#version 300" -> becomes
			// "#version 300 es" + leftover " es" = "#version 300 es es"), which the
			// GLSL ES compiler rejects with "P0007: Unexpected text found after
			// #version directive".
			code = System.Text.RegularExpressions.Regex.Replace(
				code, @"#version\s+(\{VERSION\}|\d+(\s+core)?)", "#version 300 es");

			if (!code.Contains("#version", StringComparison.Ordinal))
				code = "#version 300 es" + Environment.NewLine + code;

			if (!vertex)
			{
				// OpenRA combined.frag ships with mediump; on Mali that quantizes palette UVs
				// and darkens indexed terrain. Force highp for all fragment shaders.
				code = code.Replace("precision mediump float;", "precision highp float;");
				if (!code.Contains("precision ", StringComparison.Ordinal))
				{
					var nl = code.IndexOf((char)10); // newline
					if (nl >= 0)
						code = code.Substring(0, nl + 1) + "precision highp float;" + Environment.NewLine + code.Substring(nl + 1);
					else
						code = code + Environment.NewLine + "precision highp float;" + Environment.NewLine;
				}
			}

			return code;
		}

		static int Compile(int type, string source)
		{
			var shader = GLES20.GlCreateShader(type);
			GLES20.GlShaderSource(shader, source);
			GLES20.GlCompileShader(shader);
			var status = new int[1];
			GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, status, 0);
			if (status[0] == 0)
			{
				var log = GLES20.GlGetShaderInfoLog(shader);
				AndroidPlatformLog.Error("OpenRA.GL", "Shader compile failed: " + log);
				var preview = source.Length > 300 ? source.Substring(0, 300) + "..." : source;
				AndroidPlatformLog.Error("OpenRA.GL", "Source preview:\n" + preview);
				throw new InvalidProgramException("Shader compile failed: " + log);
			}
			return shader;
		}

		int Uniform(string name)
		{
			if (uniformCache.TryGetValue(name, out var loc))
				return loc;
			loc = GLES20.GlGetUniformLocation(program, name);
			uniformCache[name] = loc;
			return loc;
		}

		static bool loggedAttribLayout;
		public void Bind()
		{
			// GLES3: attrib pointers are VAO state. Bind our VAO before setting pointers
			// so a later DrawElements VAO bind does not restore empty attribute state.
			AndroidGraphicsContext.BindVaoForAttributes();

			if (bindings == null || bindings.Attributes == null)
				return;

			if (!loggedAttribLayout)
			{
				loggedAttribLayout = true;
				var desc = "stride=" + bindings.Stride;
				for (var i = 0; i < bindings.Attributes.Length; i++)
				{
					var a = bindings.Attributes[i];
					desc += " | [" + i + "] " + a.Name + " type=0x" + ((int)a.Type).ToString("X")
					+ " n=" + a.Components + " off=" + a.Offset;
				}
				AndroidPlatformLog.Info("OpenRA.GL.Attrib", desc);
			}

			for (var i = 0; i < bindings.Attributes.Length; i++)
			{
				var attribute = bindings.Attributes[i];
				GLES20.GlEnableVertexAttribArray(i);
				if (attribute.Type == ShaderVertexAttributeType.Float)
				{
					// Last arg = byte offset into currently bound ARRAY_BUFFER (desktop: new IntPtr(offset)).
					GLES20.GlVertexAttribPointer(i, attribute.Components, GLES20.GlFloat, false,
												 bindings.Stride, attribute.Offset);
				}
				else
				{
					// UInt = 0x1405 GL_UNSIGNED_INT — packs palette channel flags.
					var glType = attribute.Type == ShaderVertexAttributeType.UInt
					? GLES30.GlUnsignedInt
					: GLES30.GlInt;
					GLES30.GlVertexAttribIPointer(i, attribute.Components, glType,
												  bindings.Stride, attribute.Offset);
				}
				GlDiagnostics.Check("Shader.Bind attrib[" + i + "]=" + attribute.Name);
			}
		}

		public void PrepareRender()
		{
			GLES20.GlUseProgram(program);
			foreach (var kv in boundTextures)
			{
				var at = kv.Value as AndroidTexture;
				if (at == null)
					continue;
				GLES20.GlActiveTexture(GLES20.GlTexture0 + kv.Key);
				GLES20.GlBindTexture(GLES20.GlTexture2d, at.TextureId);
			}
			GlDiagnostics.Check("Shader.PrepareRender (UseProgram program=" + program + ")");
		}

		// Set once per distinct shader "program" instance (this port has two independent
		// "combined" program objects — Renderer.SpriteRenderer for UI/chrome and
		// Renderer.WorldSpriteRenderer for the game world — so logging must be keyed by
		// program, not just by uniform name, or we'd only ever see the first instance's
		// values). Used only for the Palette/PaletteRows uniforms specifically: these are
		// what drive whether a paletted sprite (terrain, units, buildings — basically the
		// entire visible game world) samples real color data or ends up black, so if the
		// world renders black while UI/effects render fine, this is the first place to look.
		static readonly HashSet<string> LoggedPaletteUniforms = new();
		static int projLogCount;

		void LogPaletteUniformOnce(string name, string detail)
		{
			var key = program + ":" + name;
			lock (LoggedPaletteUniforms)
			{
				if (!LoggedPaletteUniforms.Add(key))
					return;
			}

			AndroidPlatformLog.Info("OpenRA.GL.Palette", "program=" + program + " " + name + ": " + detail);
		}

		public void SetBool(string name, bool value)
		{
			var loc = Uniform(name);
			if (loc >= 0)
			{
				// Mirrors the desktop reference implementation (OpenRA.Platforms.Default/
				// Shader.cs): glUniform* requires THIS shader's program to be the
				// currently-bound one, or the driver raises GL_INVALID_OPERATION and the
				// uniform silently keeps its old/default value — which for a sampler
				// uniform means every subsequent draw samples texture unit 0 regardless of
				// what texture was actually intended, i.e. exactly the "renders every
				// frame with no visible content" symptom. PrepareRender() calls
				// glUseProgram once per draw batch, but uniform setters can be called
				// interleaved with a DIFFERENT shader's setters in between (this port has
				// multiple "combined" program instances), so each setter must not assume
				// its own program is still current.
				GLES20.GlUseProgram(program);
				GLES20.GlUniform1i(loc, value ? 1 : 0);
				GlDiagnostics.Check("Shader.SetBool(" + name + ")");
			}
		}

		public void SetVec(string name, float x)
		{
			var loc = Uniform(name);
			if (name == "PaletteRows")
				LogPaletteUniformOnce(name, "loc=" + loc + " value=" + x);
			if (loc >= 0)
			{
				GLES20.GlUseProgram(program);
				GLES20.GlUniform1f(loc, x);
				GlDiagnostics.Check("Shader.SetVec(" + name + ", 1)");
			}
		}

		public void SetVec(string name, float x, float y)
		{
			var loc = Uniform(name);
			if (loc >= 0)
			{
				GLES20.GlUseProgram(program);
				GLES20.GlUniform2f(loc, x, y);
				GlDiagnostics.Check("Shader.SetVec(" + name + ", 2)");
			}
		}

		public void SetVec(string name, float x, float y, float z)
		{
			var loc = Uniform(name);
			if (loc >= 0)
			{
				GLES20.GlUseProgram(program);
				GLES20.GlUniform3f(loc, x, y, z);
				GlDiagnostics.Check("Shader.SetVec(" + name + ", 3)");
			}

			// World projection: gl_Position = (pos - Scroll) * p1 + p2
			// Wrong p1/p2 vs viewport size → map pinned in a corner.
			if (name == "Scroll" || name == "p1" || name == "p2")
			{
				projLogCount++;
				if (projLogCount <= 12 || projLogCount % 600 == 0)
					AndroidPlatformLog.Info("OpenRA.GL.Proj",
											"#" + projLogCount + " program=" + program + " " + name
											+ "=(" + x.ToString("0.####") + "," + y.ToString("0.####") + "," + z.ToString("0.####") + ")"
											+ " loc=" + loc);
			}
		}

		public void SetVec(string name, ReadOnlyMemory<float> vec, int length)
		{
			var loc = Uniform(name);
			if (loc < 0)
				return;
			GLES20.GlUseProgram(program);
			var span = vec.Span.Slice(0, Math.Min(length, vec.Length));
			switch (length)
			{
				case 1:
					GLES20.GlUniform1f(loc, span[0]);
					break;
				case 2:
					GLES20.GlUniform2f(loc, span[0], span[1]);
					break;
				case 3:
					GLES20.GlUniform3f(loc, span[0], span[1], span[2]);
					break;
				default:
				{
					// JNI GlUniform4fv needs float[]; rent only for 4+ component uniforms.
					var arr = ArrayPool<float>.Shared.Rent(length);
					try
					{
						span.Slice(0, length).CopyTo(arr);
						GLES20.GlUniform4fv(loc, Math.Max(1, length / 4), arr, 0);
					}
					finally
					{
						ArrayPool<float>.Shared.Return(arr, clearArray: false);
					}
					break;
				}
			}

			GlDiagnostics.Check("Shader.SetVec(" + name + ", n=" + length + ")");
		}

		public void SetTexture(string param, ITexture t)
		{
			if (t == null)
				return;
			if (!samplerUnits.TryGetValue(param, out var unit))
			{
				var locFallback = Uniform(param);
				if (locFallback < 0)
					return;
				unit = 0;
				foreach (var u in samplerUnits.Values)
					if (u >= unit) unit = u + 1;
					samplerUnits[param] = unit;
				GLES20.GlUseProgram(program);
				GLES20.GlUniform1i(locFallback, unit);
			}

			boundTextures[unit] = t;
			GLES20.GlUseProgram(program);
			GLES20.GlActiveTexture(GLES20.GlTexture0 + unit);
			var id = (t as AndroidTexture)?.TextureId ?? 0;
			GLES20.GlBindTexture(GLES20.GlTexture2d, id);
			GlDiagnostics.Check("Shader.SetTexture(" + param + ") unit=" + unit + " textureId=" + id);
			if (id == 0)
				AndroidPlatformLog.Warn("OpenRA.GL", "SetTexture(" + param + "): texture id 0");
			if (param == "Palette")
			{
				var texSize = (t as AndroidTexture)?.Size ?? default;
				LogPaletteUniformOnce(param, "unit=" + unit + " textureId=" + id + " size=" + texSize.Width + "x" + texSize.Height);
			}
		}

		public void SetMatrix(string param, float[] mtx)
		{
			var loc = Uniform(param);
			if (loc >= 0)
			{
				GLES20.GlUseProgram(program);
				GLES20.GlUniformMatrix4fv(loc, 1, false, mtx, 0);
				GlDiagnostics.Check("Shader.SetMatrix(" + param + ")");
			}
		}

		public void Dispose()
		{
			// Fixes a real leak: IShader previously had no Dispose at all, so every
			// world-level post-process effect trait (Tint/Flash/Chronoshift/Menu — see
			// RenderPostProcessPassBase) leaked its compiled GL program on every world
			// reload (leaving a match and starting a new one). Not visible on desktop GPUs
			// with generous VRAM, but a real accumulating cost on mobile across a long
			// session of repeated skirmishes.
			if (program != 0)
			{
				GLES20.GlDeleteProgram(program);
				program = 0;
			}
		}
	}


}