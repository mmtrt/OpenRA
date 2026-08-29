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

using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public abstract class RenderPostProcessPassBase : IRenderPostProcessPass, INotifyActorDisposing
	{
		readonly Renderer renderer;
		readonly IShader shader;
		readonly IVertexBuffer<RenderPostProcessPassVertex> buffer;
		readonly PostProcessPassType type;

		protected RenderPostProcessPassBase(string name, PostProcessPassType type)
		{
			this.type = type;
			renderer = Game.Renderer;
			shader = renderer.CreateShader(new RenderPostProcessPassShaderBindings(name));
			var vertices = new RenderPostProcessPassVertex[]
			{
				new(-1, -1),
				new(1, -1),
				new(1, 1),
				new(1, 1),
				new(-1, 1),
				new(-1, -1)
			};

			buffer = renderer.CreateVertexBuffer(vertices, false);
		}

		PostProcessPassType IRenderPostProcessPass.Type => type;
		bool IRenderPostProcessPass.Enabled => Enabled;
		void IRenderPostProcessPass.Draw(WorldRenderer wr)
		{
			shader.SetTexture("SourceTexture", Game.Renderer.GetRenderBufferSnapshot());
			PrepareRender(wr, shader);
			shader.PrepareRender();

			// Defensive: EnableScissor/DisableScissor (Renderer.cs) are a push/pop stack —
			// if any earlier caller in this frame (a UI widget/tooltip using scissor
			// clipping, say) pushed a scissor rect without popping it, GL_SCISSOR_TEST
			// stays enabled with that stale rect for every draw call afterward, including
			// this one. A full-screen post-process quad should always cover its entire
			// target regardless of whatever scissor state preceded it. ForceDisableScissor
			// bypasses the stack entirely rather than calling the normal, stack-based
			// DisableScissor() here — that pops unconditionally and throws if the stack is
			// actually empty (the normal, bug-free case), so it isn't safe to call
			// defensively. Confirmed via diagnostic: this quad was observed drawing with
			// scissor=on and a negative-origin rect smaller than the actual viewport,
			// which explains a full-screen tint effect only partially covering the screen.
			renderer.ForceDisableScissor();

			renderer.DrawBatch(buffer, shader, 0, 6, PrimitiveType.TriangleList);
		}

		protected abstract bool Enabled { get; }
		protected abstract void PrepareRender(WorldRenderer wr, IShader shader);

		void INotifyActorDisposing.Disposing(Actor self)
		{
			buffer.Dispose();
		}
	}
}
