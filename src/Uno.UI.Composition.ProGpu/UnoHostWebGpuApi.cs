#nullable enable

using System;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using Uno.UI.Composition.Drawing;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;

namespace Uno.UI.Composition.ProGpu;

// All calls use the host API, including its ABI bridge and resource domain.
internal unsafe sealed class UnoHostWebGpuApi(
	WebGPU api,
	object synchronizationRoot) : IWebGpuApi, IWebGpuRenderBundleApi, IWebGpuTextureLimitsSource
{
	public bool TryGetTextureLimits(Device* device, out uint maximumTextureWidth, out uint maximumTextureHeight)
	{
		maximumTextureWidth = maximumTextureHeight = 0;
		if (device == null)
		{
			return false;
		}
		lock (synchronizationRoot)
		{
			var limits = new SupportedLimits();
			if (!api.DeviceGetLimits(device, &limits) || limits.Limits.MaxTextureDimension2D == 0)
			{
				return false;
			}
			maximumTextureWidth = maximumTextureHeight = limits.Limits.MaxTextureDimension2D;
			return true;
		}
	}

	private sealed class MapCompletion
	{
		public readonly TaskCompletionSource<BufferMapAsyncStatus> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public GCHandle Handle;
	}

	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static void CompleteBufferMap(BufferMapAsyncStatus status, void* userData)
	{
		var handle = GCHandle.FromIntPtr((nint)userData);
		if (handle.Target is MapCompletion completion)
		{
			completion.Source.TrySetResult(status);
		}
		if (handle.IsAllocated)
		{
			handle.Free();
		}
	}

	public BindGroup* DeviceCreateBindGroup(Device* d, BindGroupDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.DeviceCreateBindGroup(d, x);
		}
	}
	public BindGroupLayout* DeviceCreateBindGroupLayout(Device* d, BindGroupLayoutDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.DeviceCreateBindGroupLayout(d, x);
		}
	}
	public WgpuBuffer* DeviceCreateBuffer(Device* d, BufferDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.DeviceCreateBuffer(d, x);
		}
	}
	public CommandEncoder* DeviceCreateCommandEncoder(Device* d, CommandEncoderDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.DeviceCreateCommandEncoder(d, x);
		}
	}
	public ComputePipeline* DeviceCreateComputePipeline(Device* d, ComputePipelineDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.DeviceCreateComputePipeline(d, x);
		}
	}
	public PipelineLayout* DeviceCreatePipelineLayout(Device* d, PipelineLayoutDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.DeviceCreatePipelineLayout(d, x);
		}
	}
	public RenderPipeline* DeviceCreateRenderPipeline(Device* d, RenderPipelineDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.DeviceCreateRenderPipeline(d, x);
		}
	}
	public Sampler* DeviceCreateSampler(Device* d, SamplerDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.DeviceCreateSampler(d, x);
		}
	}
	public ShaderModule* DeviceCreateShaderModule(Device* d, ShaderModuleDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.DeviceCreateShaderModule(d, x);
		}
	}
	public Texture* DeviceCreateTexture(Device* d, TextureDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.DeviceCreateTexture(d, x);
		}
	}
	public TextureView* TextureCreateView(Texture* t, TextureViewDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.TextureCreateView(t, x);
		}
	}
	public BindGroupLayout* ComputePipelineGetBindGroupLayout(ComputePipeline* p, uint i)
	{
		lock (synchronizationRoot)
		{
			return api.ComputePipelineGetBindGroupLayout(p, i);
		}
	}
	public BindGroupLayout* RenderPipelineGetBindGroupLayout(RenderPipeline* p, uint i)
	{
		lock (synchronizationRoot)
		{
			return api.RenderPipelineGetBindGroupLayout(p, i);
		}
	}
	public ComputePassEncoder* CommandEncoderBeginComputePass(CommandEncoder* e, ComputePassDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.CommandEncoderBeginComputePass(e, x);
		}
	}
	public RenderPassEncoder* CommandEncoderBeginRenderPass(CommandEncoder* e, RenderPassDescriptor* x)
	{
		var descriptor = *x;
		Span<RenderPassColorAttachment> attachments = stackalloc RenderPassColorAttachment[checked((int)x->ColorAttachmentCount)];
		new ReadOnlySpan<RenderPassColorAttachment>(x->ColorAttachments, attachments.Length).CopyTo(attachments);
		foreach (ref var attachment in attachments)
		{
			// ProGPU render targets are 2D; the modern host ABI requires an explicit undefined slice.
			attachment.DepthSlice = uint.MaxValue;
		}
		fixed (RenderPassColorAttachment* colors = attachments)
		{
			descriptor.ColorAttachments = colors;
			lock (synchronizationRoot)
			{
				return api.CommandEncoderBeginRenderPass(e, &descriptor);
			}
		}
	}
	public RenderBundleEncoder* DeviceCreateRenderBundleEncoder(Device* d, RenderBundleEncoderDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.DeviceCreateRenderBundleEncoder(d, x);
		}
	}
	public void CommandEncoderCopyBufferToBuffer(CommandEncoder* e, WgpuBuffer* s, ulong so, WgpuBuffer* d, ulong @do, ulong z) => api.CommandEncoderCopyBufferToBuffer(e, s, so, d, @do, z);
	public void CommandEncoderCopyBufferToTexture(CommandEncoder* e, ImageCopyBuffer* s, ImageCopyTexture* d, Extent3D* z) => api.CommandEncoderCopyBufferToTexture(e, s, d, z);
	public void CommandEncoderCopyTextureToBuffer(CommandEncoder* e, ImageCopyTexture* s, ImageCopyBuffer* d, Extent3D* z) => api.CommandEncoderCopyTextureToBuffer(e, s, d, z);
	public void CommandEncoderCopyTextureToTexture(CommandEncoder* e, ImageCopyTexture* s, ImageCopyTexture* d, Extent3D* z) => api.CommandEncoderCopyTextureToTexture(e, s, d, z);
	public CommandBuffer* CommandEncoderFinish(CommandEncoder* e, CommandBufferDescriptor* x)
	{
		lock (synchronizationRoot)
		{
			return api.CommandEncoderFinish(e, x);
		}
	}
	public void ComputePassEncoderSetPipeline(ComputePassEncoder* p, ComputePipeline* x) => api.ComputePassEncoderSetPipeline(p, x);
	public void ComputePassEncoderSetBindGroup(ComputePassEncoder* p, uint i, BindGroup* g, nuint c, uint* o) => api.ComputePassEncoderSetBindGroup(p, i, g, c, o);
	public void ComputePassEncoderDispatchWorkgroups(ComputePassEncoder* p, uint x, uint y, uint z) => api.ComputePassEncoderDispatchWorkgroups(p, x, y, z);
	public void ComputePassEncoderDispatchWorkgroupsIndirect(ComputePassEncoder* p, WgpuBuffer* buffer, ulong offset) => api.ComputePassEncoderDispatchWorkgroupsIndirect(p, buffer, offset);
	public void ComputePassEncoderEnd(ComputePassEncoder* p) => api.ComputePassEncoderEnd(p);
	public void RenderPassEncoderSetPipeline(RenderPassEncoder* p, RenderPipeline* x) => api.RenderPassEncoderSetPipeline(p, x);
	public void RenderPassEncoderSetBindGroup(RenderPassEncoder* p, uint i, BindGroup* g, nuint c, uint* o) => api.RenderPassEncoderSetBindGroup(p, i, g, c, o);
	public void RenderPassEncoderSetVertexBuffer(RenderPassEncoder* p, uint s, WgpuBuffer* b, ulong o, ulong z) => api.RenderPassEncoderSetVertexBuffer(p, s, b, o, z);
	public void RenderPassEncoderSetIndexBuffer(RenderPassEncoder* p, WgpuBuffer* b, IndexFormat f, ulong o, ulong z) => api.RenderPassEncoderSetIndexBuffer(p, b, f, o, z);
	public void RenderPassEncoderSetScissorRect(RenderPassEncoder* p, uint x, uint y, uint w, uint h) => api.RenderPassEncoderSetScissorRect(p, x, y, w, h);
	public void RenderPassEncoderSetStencilReference(RenderPassEncoder* p, uint r) => api.RenderPassEncoderSetStencilReference(p, r);
	public void RenderPassEncoderSetViewport(RenderPassEncoder* p, float x, float y, float w, float h, float n, float f) => api.RenderPassEncoderSetViewport(p, x, y, w, h, n, f);
	public void RenderPassEncoderDraw(RenderPassEncoder* p, uint v, uint i, uint fv, uint fi) => api.RenderPassEncoderDraw(p, v, i, fv, fi);
	public void RenderPassEncoderDrawIndexed(RenderPassEncoder* p, uint i, uint c, uint f, int b, uint fi) => api.RenderPassEncoderDrawIndexed(p, i, c, f, b, fi);
	public void RenderPassEncoderEnd(RenderPassEncoder* p) => api.RenderPassEncoderEnd(p);
	public void RenderBundleEncoderSetPipeline(RenderBundleEncoder* e, RenderPipeline* p) => api.RenderBundleEncoderSetPipeline(e, p);
	public void RenderBundleEncoderSetBindGroup(RenderBundleEncoder* e, uint i, BindGroup* g, nuint c, uint* o) => api.RenderBundleEncoderSetBindGroup(e, i, g, c, o);
	public void RenderBundleEncoderSetVertexBuffer(RenderBundleEncoder* e, uint s, WgpuBuffer* b, ulong o, ulong z) => api.RenderBundleEncoderSetVertexBuffer(e, s, b, o, z);
	public void RenderBundleEncoderSetIndexBuffer(RenderBundleEncoder* e, WgpuBuffer* b, IndexFormat f, ulong o, ulong z) => api.RenderBundleEncoderSetIndexBuffer(e, b, f, o, z);
	public void RenderBundleEncoderDraw(RenderBundleEncoder* e, uint v, uint i, uint fv, uint fi) => api.RenderBundleEncoderDraw(e, v, i, fv, fi);
	public void RenderBundleEncoderDrawIndexed(RenderBundleEncoder* e, uint i, uint c, uint f, int b, uint fi) => api.RenderBundleEncoderDrawIndexed(e, i, c, f, b, fi);
	public RenderBundle* RenderBundleEncoderFinish(RenderBundleEncoder* e, RenderBundleDescriptor* d)
	{
		lock (synchronizationRoot)
		{
			return api.RenderBundleEncoderFinish(e, d);
		}
	}
	public void RenderPassEncoderExecuteBundles(RenderPassEncoder* p, nuint c, RenderBundle** b) => api.RenderPassEncoderExecuteBundles(p, c, b);
	public void QueueWriteBuffer(Queue* q, WgpuBuffer* b, ulong o, void* d, nuint z)
	{
		lock (synchronizationRoot)
		{
			api.QueueWriteBuffer(q, b, o, d, z);
		}
	}
	public void QueueWriteTexture(Queue* q, ImageCopyTexture* d, void* p, nuint z, TextureDataLayout* l, Extent3D* s)
	{
		lock (synchronizationRoot)
		{
			api.QueueWriteTexture(q, d, p, z, l, s);
		}
	}
	public void QueueSubmit(Queue* q, nuint c, CommandBuffer** b)
	{
		lock (synchronizationRoot)
		{
			api.QueueSubmit(q, c, b);
		}
	}
	public void BufferMapAsync(WgpuBuffer* b, MapMode m, nuint o, nuint z, PfnBufferMapCallback c, void* u)
	{
		lock (synchronizationRoot)
		{
			api.BufferMapAsync(b, m, o, z, c, u);
		}
	}
	public Task<BufferMapAsyncStatus> BufferMapAsyncTask(WgpuBuffer* b, MapMode m, nuint o, nuint z)
	{
		var completion = new MapCompletion();
		completion.Handle = GCHandle.Alloc(completion);
		try
		{
			lock (synchronizationRoot)
			{
				api.BufferMapAsync(
					b,
					m,
					o,
					z,
					new PfnBufferMapCallback(&CompleteBufferMap),
					(void*)GCHandle.ToIntPtr(completion.Handle));
			}
		}
		catch
		{
			if (completion.Handle.IsAllocated)
			{
				completion.Handle.Free();
			}
			throw;
		}
		return completion.Source.Task;
	}
	public void* BufferGetMappedRange(WgpuBuffer* b, nuint o, nuint z)
	{
		lock (synchronizationRoot)
		{
			return api.BufferGetMappedRange(b, o, z);
		}
	}
	public void* BufferGetConstMappedRange(WgpuBuffer* b, nuint o, nuint z)
	{
		lock (synchronizationRoot)
		{
			return api.BufferGetConstMappedRange(b, o, z);
		}
	}
	public void BufferUnmap(WgpuBuffer* b)
	{
		lock (synchronizationRoot)
		{
			api.BufferUnmap(b);
		}
	}
	public void BufferDestroy(WgpuBuffer* b)
	{
		lock (synchronizationRoot)
		{
			api.BufferDestroy(b);
		}
	}
	public void SurfaceGetCurrentTexture(Surface* s, SurfaceTexture* t)
	{
		lock (synchronizationRoot)
		{
			api.SurfaceGetCurrentTexture(s, t);
		}
	}
	public void SurfacePresent(Surface* s)
	{
		lock (synchronizationRoot)
		{
			api.SurfacePresent(s);
		}
	}
	public void SurfaceRelease(Surface* s)
	{
		lock (synchronizationRoot)
		{
			api.SurfaceRelease(s);
		}
	}
	public void BindGroupRelease(BindGroup* v)
	{
		lock (synchronizationRoot)
		{
			api.BindGroupRelease(v);
		}
	}
	public void BindGroupLayoutRelease(BindGroupLayout* v)
	{
		lock (synchronizationRoot)
		{
			api.BindGroupLayoutRelease(v);
		}
	}
	public void BufferRelease(WgpuBuffer* v)
	{
		lock (synchronizationRoot)
		{
			api.BufferRelease(v);
		}
	}
	public void CommandBufferRelease(CommandBuffer* v)
	{
		lock (synchronizationRoot)
		{
			api.CommandBufferRelease(v);
		}
	}
	public void CommandEncoderRelease(CommandEncoder* v)
	{
		lock (synchronizationRoot)
		{
			api.CommandEncoderRelease(v);
		}
	}
	public void ComputePassEncoderRelease(ComputePassEncoder* v)
	{
		lock (synchronizationRoot)
		{
			api.ComputePassEncoderRelease(v);
		}
	}
	public void ComputePipelineRelease(ComputePipeline* v)
	{
		lock (synchronizationRoot)
		{
			api.ComputePipelineRelease(v);
		}
	}
	public void PipelineLayoutRelease(PipelineLayout* v)
	{
		lock (synchronizationRoot)
		{
			api.PipelineLayoutRelease(v);
		}
	}
	public void RenderPassEncoderRelease(RenderPassEncoder* v)
	{
		lock (synchronizationRoot)
		{
			api.RenderPassEncoderRelease(v);
		}
	}
	public void RenderBundleEncoderRelease(RenderBundleEncoder* v)
	{
		lock (synchronizationRoot)
		{
			api.RenderBundleEncoderRelease(v);
		}
	}
	public void RenderBundleRelease(RenderBundle* v)
	{
		lock (synchronizationRoot)
		{
			api.RenderBundleRelease(v);
		}
	}
	public void RenderPipelineRelease(RenderPipeline* v)
	{
		lock (synchronizationRoot)
		{
			api.RenderPipelineRelease(v);
		}
	}
	public void SamplerRelease(Sampler* v)
	{
		lock (synchronizationRoot)
		{
			api.SamplerRelease(v);
		}
	}
	public void ShaderModuleRelease(ShaderModule* v)
	{
		lock (synchronizationRoot)
		{
			api.ShaderModuleRelease(v);
		}
	}
	public void TextureDestroy(Texture* v)
	{
		lock (synchronizationRoot)
		{
			api.TextureDestroy(v);
		}
	}
	public void TextureRelease(Texture* v)
	{
		lock (synchronizationRoot)
		{
			api.TextureRelease(v);
		}
	}
	public void TextureViewRelease(TextureView* v)
	{
		lock (synchronizationRoot)
		{
			api.TextureViewRelease(v);
		}
	}
}

internal sealed unsafe class UnoBorrowedWebGpuLifetime(IWebGpuDeviceContext device) : IWebGpuExternalDeviceLifetime
{
	private struct QueueCompletion
	{
		internal int Completed;
		internal QueueWorkDoneStatus Status;
	}

	public void Poll(bool wait)
	{
		if (!wait)
		{
			device.Api.InstanceProcessEvents(device.Instance);
			return;
		}
		QueueCompletion completion = default;
		device.Api.QueueOnSubmittedWorkDone(device.Queue, new PfnQueueWorkDoneCallback(&OnQueueWorkDone), &completion);
		while (System.Threading.Volatile.Read(ref completion.Completed) == 0)
		{
			device.Api.InstanceProcessEvents(device.Instance);
			System.Threading.Thread.SpinWait(64);
		}
		if (completion.Status != QueueWorkDoneStatus.Success)
		{
			throw new InvalidOperationException($"WebGPU queue completion failed ({completion.Status}).");
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void OnQueueWorkDone(QueueWorkDoneStatus status, void* userdata)
	{
		var completion = (QueueCompletion*)userdata;
		completion->Status = status;
		System.Threading.Volatile.Write(ref completion->Completed, 1);
	}

	public void Dispose() { }
}
