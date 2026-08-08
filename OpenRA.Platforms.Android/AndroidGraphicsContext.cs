#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port — OpenGL ES 3.0 graphics backend.
 */
#endregion

using System;
using System.Collections.Generic;
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
				var tmp = new byte[bytes];
				Marshal.Copy(handle.AddrOfPinnedObject(), tmp, 0, bytes);
				bb.Put(tmp);
				bb.Position(0);
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

	static class GlDiagnostics
	{
		static readonly HashSet<string> LoggedContexts = new();
		static readonly object Gate = new();

		/// <summary>
		/// Checks glGetError() and logs (once per distinct context+error combination, so a
		/// per-frame repeating error cannot flood the log at 60fps) any error found. This is
		/// purely additive — it never changes GL state or rendering behavior — added because
		/// nothing in this file previously checked glGetError() at all (aside from one
		/// glCheckFramebufferStatus call), so a state error anywhere upstream (bad attribute
		/// setup, an unbound/incomplete texture, a rejected uniform type, ...) could silently
		/// leave every subsequent draw call a no-op while Present() kept "succeeding" every
		/// frame — exactly the black-screen-with-no-errors-visible symptom being chased here.
		/// </summary>
		public static void Check(string context)
		{
			var err = GLES20.GlGetError();
			if (err == GLES20.GlNoError)
				return;

			var key = context + ":0x" + err.ToString("X");
			lock (Gate)
			{
				if (!LoggedContexts.Add(key))
					return;
			}

			AndroidPlatformLog.Error("OpenRA.GL.Error", $"{context}: glGetError=0x{err:X} ({GlErrorName(err)})");
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
	}

	sealed class AndroidGraphicsContext : IGraphicsContext
	{
		int vao;

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
		}

		public void DisableDepthBuffer() => GLES20.GlDisable(GLES20.GlDepthTest);

		public void EnableScissor(int x, int y, int width, int height)
		{
			if (width < 0) width = 0;
			if (height < 0) height = 0;
			GLES20.GlEnable(GLES20.GlScissorTest);
			GLES20.GlScissor(x, y, width, height);
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
			var w = Math.Max(1, AndroidEgl.SurfaceWidth);
			var h = Math.Max(1, AndroidEgl.SurfaceHeight);
			GLES20.GlViewport(0, 0, w, h);
			GlDiagnostics.Check("Present (BindFramebuffer/Viewport)");

			AndroidEgl.SwapBuffers();
			presentCount++;
			if (presentCount <= 5 || presentCount % 300 == 0)
				AndroidPlatformLog.Info("OpenRA.GL", "Present #" + presentCount + " surface=" + w + "x" + h);
		}

		public void SetBlendMode(BlendMode mode)
		{
			GLES20.GlBlendEquation(GLES20.GlFuncAdd);
			switch (mode)
			{
				case BlendMode.None:
					GLES20.GlDisable(GLES20.GlBlend);
					break;
				case BlendMode.Alpha:
				case BlendMode.Translucent:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
					break;
				case BlendMode.Additive:
				case BlendMode.LowAdditive:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlOne, GLES20.GlOne);
					break;
				case BlendMode.Subtractive:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendEquation(GLES20.GlFuncReverseSubtract);
					GLES20.GlBlendFunc(GLES20.GlOne, GLES20.GlOne);
					break;
				case BlendMode.Multiply:
				case BlendMode.Multiplicative:
				case BlendMode.DoubleMultiplicative:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlDstColor, GLES20.GlOneMinusSrcAlpha);
					break;
				case BlendMode.Screen:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlOne, GLES20.GlOneMinusSrcColor);
					break;
				default:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
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

		public void DrawPrimitives(PrimitiveType pt, int firstVertex, int numVertices)
		{
			GLES20.GlDrawArrays(ModeFromPrimitiveType(pt), firstVertex, numVertices);
			GlDiagnostics.Check("DrawPrimitives(" + pt + ", first=" + firstVertex + ", n=" + numVertices + ")");
		}

		public void DrawElements(int numIndices, int offset)
		{
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
			var usage = dynamic ? GLES20.GlDynamicDraw : GLES20.GlStaticDraw;
			var bb = GlesBuffers.ToByteBuffer(vertices, length, elementSize);
			GLES20.GlBindBuffer(GLES20.GlArrayBuffer, buffer);
			GLES20.GlBufferData(GLES20.GlArrayBuffer, length * elementSize, bb, usage);
			GlDiagnostics.Check("VertexBuffer.SetData(length=" + length + ")");
		}

		public void SetData(ref T[] vertices, int length) => SetData(vertices, length);

		public void SetData(T[] vertices, int offset, int start, int length)
		{
			var bb = GlesBuffers.ToByteBuffer(vertices, start + length, elementSize);
			// sub-data from element 'start'
			var slice = ByteBuffer.AllocateDirect(length * elementSize);
			slice.Order(ByteOrder.NativeOrder());
			var tmp = new byte[length * elementSize];
			bb.Position(start * elementSize);
			bb.Get(tmp);
			slice.Put(tmp);
			slice.Position(0);
			GLES20.GlBindBuffer(GLES20.GlArrayBuffer, buffer);
			GLES20.GlBufferSubData(GLES20.GlArrayBuffer, offset * elementSize, length * elementSize, slice);
		}

		public void Dispose()
		{
			if (buffer != 0)
				GLES20.GlDeleteBuffers(1, new[] { buffer }, 0);
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
			var bytes = new byte[indices.Length * 4];
			System.Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
			var bb = GlesBuffers.ToByteBuffer(bytes);
			GLES20.GlBindBuffer(GLES20.GlElementArrayBuffer, buffer);
			GLES20.GlBufferData(GLES20.GlElementArrayBuffer, bytes.Length, bb, GLES20.GlStaticDraw);
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
		int texture;
		Size size;
		TextureScaleFilter scaleFilter = TextureScaleFilter.Linear;

		public Size Size => size;

		public TextureScaleFilter ScaleFilter
		{
			get => scaleFilter;
			set
			{
				scaleFilter = value;
				if (texture == 0)
					return;
				var filter = scaleFilter == TextureScaleFilter.Linear ? GLES20.GlLinear : GLES20.GlNearest;
				GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
				GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, filter);
				GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, filter);
			}
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
			GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
			GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
			GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
			GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
			GlDiagnostics.Check("Texture.EnsureTexture (GenTextures)");
			if (texture == 0)
				AndroidPlatformLog.Error("OpenRA.GL", "EnsureTexture: glGenTextures returned 0");
		}

		public void SetData(byte[] colors, int width, int height)
		{
			EnsureTexture();
			size = new Size(width, height);
			var bb = GlesBuffers.ToByteBuffer(colors);
			GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
			GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, width, height, 0,
				GLES20.GlRgba, GLES20.GlUnsignedByte, bb);
			GlDiagnostics.Check("Texture.SetData " + width + "x" + height + " textureId=" + texture);
		}

		public void SetFloatData(float[] data, int width, int height)
		{
			EnsureTexture();
			size = new Size(width, height);
			var bb = GlesBuffers.ToByteBuffer(data);
			GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
			GLES30.GlTexImage2D(GLES20.GlTexture2d, 0, GLES30.GlRgba16f, width, height, 0,
				GLES20.GlRgba, GLES20.GlFloat, bb);
		}

		public void SetEmpty(int width, int height)
		{
			EnsureTexture();
			size = new Size(width, height);
			GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
			GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, width, height, 0,
				GLES20.GlRgba, GLES20.GlUnsignedByte, null);
		}

		public void SetDataFromReadBuffer(Rectangle rect)
		{
			EnsureTexture();
			size = new Size(rect.Width, rect.Height);
			GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
			GLES20.GlCopyTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, rect.Left, rect.Top, rect.Width, rect.Height, 0);
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
			if (status != GLES20.GlFramebufferComplete)
				AndroidPlatformLog.Error("OpenRA.GL", $"Framebuffer incomplete: 0x{status:X}");

			GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
		}

		public void Bind()
		{
			GLES20.GlGetIntegerv(0x0BA2 /* GL_VIEWPORT */, savedViewport, 0);
			GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, framebuffer);
			GLES20.GlViewport(0, 0, size.Width, size.Height);
			GLES20.GlClearColor(clearColor.R / 255f, clearColor.G / 255f, clearColor.B / 255f, clearColor.A / 255f);
			GLES20.GlClear(GLES20.GlColorBufferBit | GLES20.GlDepthBufferBit);
		}

		public void Unbind()
		{
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
		readonly int program;
		readonly IShaderBindings bindings;
		readonly Dictionary<string, int> uniformCache = new();
		int textureUnit;

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

			AndroidPlatformLog.Info("OpenRA.GL", "Shader linked: " + bindings.VertexShaderName);
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

			if (!vertex && !code.Contains("precision ", StringComparison.Ordinal))
			{
				var nl = code.IndexOf('\n');
				if (nl >= 0)
					code = code.Substring(0, nl + 1) + "precision mediump float;" + Environment.NewLine + code.Substring(nl + 1);
				else
					code = code + Environment.NewLine + "precision mediump float;" + Environment.NewLine;
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

		public void Bind()
		{
			// Called with the vertex buffer already bound — set attrib pointers (desktop Shader.Bind).
			if (bindings == null || bindings.Attributes == null)
				return;
			for (var i = 0; i < bindings.Attributes.Length; i++)
			{
				var attribute = bindings.Attributes[i];
				GLES20.GlEnableVertexAttribArray(i);
				if (attribute.Type == ShaderVertexAttributeType.Float)
					GLES20.GlVertexAttribPointer(i, attribute.Components, GLES20.GlFloat, false,
						bindings.Stride, attribute.Offset);
				else
					GLES30.GlVertexAttribIPointer(i, attribute.Components, (int)attribute.Type,
						bindings.Stride, attribute.Offset);
				GlDiagnostics.Check("Shader.Bind attrib[" + i + "]=" + attribute.Name);
			}
		}

		public void PrepareRender()
		{
			GLES20.GlUseProgram(program);
			GlDiagnostics.Check("Shader.PrepareRender (UseProgram program=" + program + ")");
			textureUnit = 0;
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
		}

		public void SetVec(string name, ReadOnlyMemory<float> vec, int length)
		{
			var loc = Uniform(name);
			if (loc < 0)
				return;
			GLES20.GlUseProgram(program);
			var arr = vec.Span.Slice(0, Math.Min(length, vec.Length)).ToArray();
			switch (length)
			{
				case 1: GLES20.GlUniform1fv(loc, 1, arr, 0); break;
				case 2: GLES20.GlUniform2fv(loc, 1, arr, 0); break;
				case 3: GLES20.GlUniform3fv(loc, 1, arr, 0); break;
				default: GLES20.GlUniform4fv(loc, Math.Max(1, length / 4), arr, 0); break;
			}

			GlDiagnostics.Check("Shader.SetVec(" + name + ", n=" + length + ")");
		}

		public void SetTexture(string param, ITexture t)
		{
			var loc = Uniform(param);
			if (loc < 0)
				return;

			// See SetBool above for why this is required here: unlike the desktop backend,
			// this method binds the texture and sets the sampler uniform immediately
			// rather than deferring to PrepareRender(), so it must independently guarantee
			// its own program is current rather than relying on a prior PrepareRender()
			// call still being in effect.
			GLES20.GlUseProgram(program);

			var unit = textureUnit++;
			GLES20.GlActiveTexture(GLES20.GlTexture0 + unit);
			var id = (t as AndroidTexture)?.TextureId ?? 0;
			GLES20.GlBindTexture(GLES20.GlTexture2d, id);
			GLES20.GlUniform1i(loc, unit);
			GlDiagnostics.Check("Shader.SetTexture(" + param + ") unit=" + unit + " textureId=" + id);
			if (id == 0)
				AndroidPlatformLog.Warn("OpenRA.GL", "SetTexture(" + param + "): binding texture id 0 (t=" + (t == null ? "null" : t.GetType().Name) + ")");
			if (param == "Palette")
			{
				var texSize = (t as AndroidTexture)?.Size ?? default;
				LogPaletteUniformOnce(param, "loc=" + loc + " unit=" + unit + " textureId=" + id + " size=" + texSize.Width + "x" + texSize.Height);
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
	}


}