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
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
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
		}

		public void Clear()
		{
			TryInitVao();
			GLES20.GlClearColor(0, 0, 0, 1);
			GLES20.GlClear(GLES20.GlColorBufferBit | GLES20.GlDepthBufferBit);
		}

		public void ClearDepthBuffer()
		{
			GLES20.GlClear(GLES20.GlDepthBufferBit);
		}

		public void EnableDepthBuffer()
		{
			GLES20.GlClear(GLES20.GlDepthBufferBit);
			GLES20.GlEnable(GLES20.GlDepthTest);
			GLES20.GlDepthFunc(GLES20.GlLequal);
		}

		public void DisableDepthBuffer()
		{
			GLES20.GlDisable(GLES20.GlDepthTest);
		}

		public void EnableScissor(int x, int y, int width, int height)
		{
			if (width < 0) width = 0;
			if (height < 0) height = 0;
			GLES20.GlEnable(GLES20.GlScissorTest);
			GLES20.GlScissor(x, y, width, height);
		}

		public void DisableScissor()
		{
			GLES20.GlDisable(GLES20.GlScissorTest);
		}

		public void Present()
		{
			AndroidEgl.MakeCurrent();
			AndroidEgl.SwapBuffers();
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
				case BlendMode.Translucent:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
					break;
				default:
					GLES20.GlEnable(GLES20.GlBlend);
					GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
					break;
			}
		}

		public void SetVSyncEnabled(bool enabled)
		{
			// Window-system swap interval is controlled by EGL; no-op on ES via this path.
		}

		static int ModeFromPrimitiveType(PrimitiveType pt) => pt switch
		{
			PrimitiveType.PointList => GLES20.GlPoints,
			PrimitiveType.LineList => GLES20.GlLines,
			PrimitiveType.TriangleList => GLES20.GlTriangles,
			_ => GLES20.GlTriangles
		};

		public void DrawPrimitives(PrimitiveType pt, int firstVertex, int numVertices)
		{
			GLES20.GlDrawArrays(ModeFromPrimitiveType(pt), firstVertex, numVertices);
		}

		public void DrawElements(int numIndices, int offset)
		{
			GLES20.GlDrawElements(GLES20.GlTriangles, numIndices, GLES20.GlUnsignedInt, offset);
		}

		public IVertexBuffer<T> CreateEmptyVertexBuffer<T>(int size) where T : struct
			=> new AndroidVertexBuffer<T>(size);

		public IVertexBuffer<T> CreateVertexBuffer<T>(T[] data, bool dynamic = true) where T : struct
			=> new AndroidVertexBuffer<T>(data, dynamic);

		public T[] CreateVertices<T>(int size) where T : struct => new T[size];

		public IIndexBuffer CreateIndexBuffer(uint[] indices) => new AndroidIndexBuffer(indices);

		public ITexture CreateTexture() => new AndroidTexture();

		public IFrameBuffer CreateFrameBuffer(Size s)
			=> new AndroidFrameBuffer(s, Color.FromArgb(0));

		public IFrameBuffer CreateFrameBuffer(Size s, Color clearColor)
			=> new AndroidFrameBuffer(s, clearColor);

		public IShader CreateShader(IShaderBindings shaderBindings)
			=> new AndroidShader(shaderBindings);

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
		bool dynamic;

		public AndroidVertexBuffer(int size)
		{
			elementSize = Marshal.SizeOf<T>();
			dynamic = true;
			var ids = new int[1];
			GLES20.GlGenBuffers(1, ids, 0);
			buffer = ids[0];
			GLES20.GlBindBuffer(GLES20.GlArrayBuffer, buffer);
			GLES20.GlBufferData(GLES20.GlArrayBuffer, size * elementSize, IntPtr.Zero, GLES20.GlDynamicDraw);
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

		public void Bind()
		{
			GLES20.GlBindBuffer(GLES20.GlArrayBuffer, buffer);
		}

		public void SetData(T[] vertices, int length)
		{
			var handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
			try
			{
				var ptr = handle.AddrOfPinnedObject();
				var usage = dynamic ? GLES20.GlDynamicDraw : GLES20.GlStaticDraw;
				GLES20.GlBindBuffer(GLES20.GlArrayBuffer, buffer);
				GLES20.GlBufferData(GLES20.GlArrayBuffer, length * elementSize, ptr, usage);
			}
			finally
			{
				handle.Free();
			}
		}

		public void SetData(ref T[] vertices, int length)
		{
			SetData(vertices, length);
		}

		public void SetData(T[] vertices, int offset, int start, int length)
		{
			var handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
			try
			{
				var ptr = IntPtr.Add(handle.AddrOfPinnedObject(), start * elementSize);
				GLES20.GlBindBuffer(GLES20.GlArrayBuffer, buffer);
				GLES20.GlBufferSubData(GLES20.GlArrayBuffer, offset * elementSize, length * elementSize, ptr);
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
			var handle = GCHandle.Alloc(indices, GCHandleType.Pinned);
			try
			{
				GLES20.GlBindBuffer(GLES20.GlElementArrayBuffer, buffer);
				GLES20.GlBufferData(GLES20.GlElementArrayBuffer, indices.Length * sizeof(uint),
					handle.AddrOfPinnedObject(), GLES20.GlStaticDraw);
			}
			finally
			{
				handle.Free();
			}
		}

		public void Bind()
		{
			GLES20.GlBindBuffer(GLES20.GlElementArrayBuffer, buffer);
		}

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
		}

		public void SetData(byte[] colors, int width, int height)
		{
			EnsureTexture();
			size = new Size(width, height);
			var handle = GCHandle.Alloc(colors, GCHandleType.Pinned);
			try
			{
				GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
				GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, width, height, 0,
					GLES20.GlRgba, GLES20.GlUnsignedByte, handle.AddrOfPinnedObject());
			}
			finally
			{
				handle.Free();
			}
		}

		public void SetFloatData(float[] data, int width, int height)
		{
			EnsureTexture();
			size = new Size(width, height);
			var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
			try
			{
				GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
				// RGBA float
				GLES30.GlTexImage2D(GLES20.GlTexture2d, 0, GLES30.GlRgba16f, width, height, 0,
					GLES20.GlRgba, GLES20.GlFloat, handle.AddrOfPinnedObject());
			}
			finally
			{
				handle.Free();
			}
		}

		public void SetEmpty(int width, int height)
		{
			EnsureTexture();
			size = new Size(width, height);
			GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
			GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, width, height, 0,
				GLES20.GlRgba, GLES20.GlUnsignedByte, IntPtr.Zero);
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
			var data = new byte[w * h * 4];
			// Full FBO readback path omitted for non-FBO textures; return zeros
			return data;
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
		int[] savedViewport = new int[4];

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
				Android.Util.Log.Error("OpenRA.GL", $"Framebuffer incomplete: 0x{status:X}");

			GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
		}

		public void Bind()
		{
			GLES20.GlGetIntegerv(GLES20.GlViewport, savedViewport, 0);
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

		public void DisableScissor()
		{
			GLES20.GlDisable(GLES20.GlScissorTest);
		}

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
		readonly Dictionary<string, int> uniformCache = new();
		int textureUnit;

		public AndroidShader(IShaderBindings bindings)
		{
			var vs = Compile(GLES20.GlVertexShader, AdaptShader(bindings.VertexShaderCode, true));
			var fs = Compile(GLES20.GlFragmentShader, AdaptShader(bindings.FragmentShaderCode, false));

			program = GLES20.GlCreateProgram();
			GLES20.GlAttachShader(program, vs);
			GLES20.GlAttachShader(program, fs);

			// Bind attribute locations from OpenRA shader bindings
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
				Android.Util.Log.Error("OpenRA.GL", "Shader link failed: " + log);
			}

			GLES20.GlDeleteShader(vs);
			GLES20.GlDeleteShader(fs);

			// Enable vertex attributes
			if (bindings.Attributes != null)
			{
				GLES20.GlUseProgram(program);
				foreach (var attr in bindings.Attributes)
				{
					var loc = GLES20.GlGetAttribLocation(program, attr.Name);
					if (loc < 0)
						continue;
					GLES20.GlEnableVertexAttribArray(loc);
					var type = attr.Type == ShaderVertexAttributeType.Float ? GLES20.GlFloat : GLES20.GlFloat;
					GLES20.GlVertexAttribPointer(loc, attr.Components, type, false, bindings.Stride, attr.Offset);
				}
			}
		}

		/// <summary>
		/// Desktop GLSL often uses #version 140+; ES needs #version 300 es and precision.
		/// Soft adaptation for common OpenRA shaders.
		/// </summary>
		static string AdaptShader(string code, bool vertex)
		{
			if (string.IsNullOrEmpty(code))
				return code;

			var sb = new StringBuilder();
			if (!code.Contains("#version"))
			{
				sb.AppendLine("#version 300 es");
				if (!vertex)
					sb.AppendLine("precision mediump float;");
			}
			else
			{
				// Replace desktop version directives with ES 300
				code = System.Text.RegularExpressions.Regex.Replace(
					code, @"#version\s+\d+(\s+core)?", "#version 300 es");
				if (!vertex && !code.Contains("precision "))
					sb.AppendLine("precision mediump float;");
			}

			sb.Append(code);
			return sb.ToString();
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
				Android.Util.Log.Error("OpenRA.GL", "Shader compile failed: " + log);
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
			GLES20.GlUseProgram(program);
			textureUnit = 0;
		}

		public void PrepareRender()
		{
			Bind();
		}

		public void SetBool(string name, bool value)
		{
			var loc = Uniform(name);
			if (loc >= 0)
				GLES20.GlUniform1i(loc, value ? 1 : 0);
		}

		public void SetVec(string name, float x)
		{
			var loc = Uniform(name);
			if (loc >= 0)
				GLES20.GlUniform1f(loc, x);
		}

		public void SetVec(string name, float x, float y)
		{
			var loc = Uniform(name);
			if (loc >= 0)
				GLES20.GlUniform2f(loc, x, y);
		}

		public void SetVec(string name, float x, float y, float z)
		{
			var loc = Uniform(name);
			if (loc >= 0)
				GLES20.GlUniform3f(loc, x, y, z);
		}

		public void SetVec(string name, ReadOnlyMemory<float> vec, int length)
		{
			var loc = Uniform(name);
			if (loc < 0)
				return;
			var span = vec.Span;
			var arr = span.Length == length ? span.ToArray() : span[..Math.Min(length, span.Length)].ToArray();
			switch (length)
			{
				case 1: GLES20.GlUniform1fv(loc, 1, arr, 0); break;
				case 2: GLES20.GlUniform2fv(loc, 1, arr, 0); break;
				case 3: GLES20.GlUniform3fv(loc, 1, arr, 0); break;
				default: GLES20.GlUniform4fv(loc, Math.Max(1, length / 4), arr, 0); break;
			}
		}

		public void SetTexture(string param, ITexture t)
		{
			var loc = Uniform(param);
			if (loc < 0)
				return;
			var unit = textureUnit++;
			GLES20.GlActiveTexture(GLES20.GlTexture0 + unit);
			var id = (t as AndroidTexture)?.TextureId ?? 0;
			GLES20.GlBindTexture(GLES20.GlTexture2d, id);
			GLES20.GlUniform1i(loc, unit);
		}

		public void SetMatrix(string param, float[] mtx)
		{
			var loc = Uniform(param);
			if (loc >= 0)
				GLES20.GlUniformMatrix4fv(loc, 1, false, mtx, 0);
		}
	}

	/// <summary>Minimal IFont until FreeType is wired.</summary>
	sealed class AndroidStubFont : IFont
	{
		public FontGlyph CreateGlyph(char c, int size, float deviceScale)
		{
			return new FontGlyph
			{
				Offset = int2.Zero,
				Size = new Size(Math.Max(1, size / 2), Math.Max(1, size)),
				Advance = size * 0.5f,
				Data = Array.Empty<byte>()
			};
		}

		public void Dispose() { }
	}
}
