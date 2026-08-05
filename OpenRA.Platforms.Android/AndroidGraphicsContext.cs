#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port — stub graphics context (EGL pending).
 */
#endregion

using System;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	/// <summary>
	/// Minimal IGraphicsContext so AndroidPlatformWindow.Context can return
	/// something without throwing. Real GLES calls come after EGL is live.
	/// </summary>
	sealed class AndroidGraphicsContext : IGraphicsContext
	{
		public string GLVersion => "OpenGL ES 3.0 (stub)";

		public void Clear() { }
		public void ClearDepthBuffer() { }
		public void DisableDepthBuffer() { }
		public void DisableScissor() { }
		public void EnableDepthBuffer() { }
		public void EnableScissor(int x, int y, int width, int height) { }
		public void Present() { /* GameSurfaceView.Present will own eglSwapBuffers */ }
		public void SetBlendMode(BlendMode mode) { }
		public void SetVSyncEnabled(bool enabled) { }

		public void DrawPrimitives(PrimitiveType pt, int firstVertex, int numVertices) { }
		public void DrawElements(int numIndices, int offset) { }

		public IVertexBuffer<T> CreateEmptyVertexBuffer<T>(int size) where T : struct
			=> new AndroidVertexBuffer<T>();
		public IVertexBuffer<T> CreateVertexBuffer<T>(T[] data, bool dynamic = true) where T : struct
			=> new AndroidVertexBuffer<T>();
		public T[] CreateVertices<T>(int size) where T : struct => new T[size];
		public IIndexBuffer CreateIndexBuffer(uint[] indices) => new AndroidIndexBuffer();
		public ITexture CreateTexture() => new AndroidTexture();
		public IFrameBuffer CreateFrameBuffer(Size s) => new AndroidFrameBuffer();
		public IFrameBuffer CreateFrameBuffer(Size s, Color clearColor) => new AndroidFrameBuffer();
		public IShader CreateShader(IShaderBindings shaderBindings) => new AndroidShader();

		public void Dispose() { }
	}

	// Minimal stand-ins so the context compiles against the interface surface.
	// Replaced by real GLES implementations after multi-target + EGL.
	sealed class AndroidVertexBuffer<T> : IVertexBuffer<T> where T : struct
	{
		public void Bind() { }
		public void SetData(T[] vertices, int length) { }
		public void Dispose() { }
	}

	sealed class AndroidIndexBuffer : IIndexBuffer
	{
		public void Bind() { }
		public void Dispose() { }
	}

	sealed class AndroidTexture : ITexture
	{
		public void Dispose() { }
	}

	sealed class AndroidFrameBuffer : IFrameBuffer
	{
		public void Bind() { }
		public void Unbind() { }
		public void Dispose() { }
	}

	sealed class AndroidShader : IShader
	{
		public void PrepareRender() { }
		public void SetBool(string name, bool value) { }
		public void SetVec(string name, float x) { }
		public void SetVec(string name, float x, float y) { }
		public void Bind() { }
		public void Dispose() { }
	}
}
