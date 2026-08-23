#nullable enable

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ProGPU.Backend;
using SW = Silk.NET.WebGPU;
using N = Uno.WebGpu.Native;
using SilkBuffer = Silk.NET.WebGPU.Buffer;

namespace Uno.UI.Composition.ProGpu;

/// <summary>
/// Exact descriptor translator between ProGPU's stable Silk WebGPU contract and
/// the wgpu-native ABI owned by Uno. Opaque handles are borrowed unchanged;
/// descriptor structs are never reinterpreted across ABI versions.
/// </summary>
internal sealed unsafe class UnoModernWebGpuApi : IWebGpuApi
{
	private const int MaxItems = 256;

	private sealed class MapCompletion
	{
		internal TaskCompletionSource<SW.BufferMapAsyncStatus>? Source;
		internal nint Callback;
		internal nint UserData;
		internal GCHandle Handle;
	}

	public SW.BindGroup* DeviceCreateBindGroup(SW.Device* device, SW.BindGroupDescriptor* descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		var count = Count(descriptor->EntryCount);
		var entries = stackalloc N.WGPUBindGroupEntry[count];
		for (var i = 0; i < count; i++)
		{
			var source = descriptor->Entries[i];
			entries[i] = new N.WGPUBindGroupEntry
			{
				Binding = source.Binding,
				Buffer = H(source.Buffer),
				Offset = source.Offset,
				Size = source.Size,
				Sampler = H(source.Sampler),
				TextureView = H(source.TextureView),
			};
		}
		var native = new N.WGPUBindGroupDescriptor
		{
			Label = StringView(descriptor->Label),
			Layout = H(descriptor->Layout),
			EntryCount = descriptor->EntryCount,
			Entries = entries,
		};
		return P<SW.BindGroup>(N.WGPU.wgpuDeviceCreateBindGroup(H(device), &native));
	}

	public SW.BindGroupLayout* DeviceCreateBindGroupLayout(SW.Device* device, SW.BindGroupLayoutDescriptor* descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		var count = Count(descriptor->EntryCount);
		var entries = stackalloc N.WGPUBindGroupLayoutEntry[count];
		for (var i = 0; i < count; i++)
		{
			var source = descriptor->Entries[i];
			entries[i] = new N.WGPUBindGroupLayoutEntry
			{
				Binding = source.Binding,
				Visibility = (N.WGPUShaderStage)(uint)source.Visibility,
				Buffer = new N.WGPUBufferBindingLayout
				{
					Type = BindingE<SW.BufferBindingType, N.WGPUBufferBindingType>(source.Buffer.Type),
					HasDynamicOffset = source.Buffer.HasDynamicOffset,
					MinBindingSize = source.Buffer.MinBindingSize,
				},
				Sampler = new N.WGPUSamplerBindingLayout { Type = BindingE<SW.SamplerBindingType, N.WGPUSamplerBindingType>(source.Sampler.Type) },
				Texture = new N.WGPUTextureBindingLayout
				{
					SampleType = BindingE<SW.TextureSampleType, N.WGPUTextureSampleType>(source.Texture.SampleType),
					ViewDimension = BindingE<SW.TextureViewDimension, N.WGPUTextureViewDimension>(source.Texture.ViewDimension),
					Multisampled = source.Texture.Multisampled,
				},
				StorageTexture = new N.WGPUStorageTextureBindingLayout
				{
					Access = BindingE<SW.StorageTextureAccess, N.WGPUStorageTextureAccess>(source.StorageTexture.Access),
					Format = TextureFormat(source.StorageTexture.Format),
					ViewDimension = BindingE<SW.TextureViewDimension, N.WGPUTextureViewDimension>(source.StorageTexture.ViewDimension),
				},
			};
		}
		var native = new N.WGPUBindGroupLayoutDescriptor
		{
			Label = StringView(descriptor->Label),
			EntryCount = descriptor->EntryCount,
			Entries = entries,
		};
		return P<SW.BindGroupLayout>(N.WGPU.wgpuDeviceCreateBindGroupLayout(H(device), &native));
	}

	public SilkBuffer* DeviceCreateBuffer(SW.Device* device, SW.BufferDescriptor* descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		var native = new N.WGPUBufferDescriptor
		{
			Label = StringView(descriptor->Label),
			Usage = (N.WGPUBufferUsage)(ulong)descriptor->Usage,
			Size = descriptor->Size,
			MappedAtCreation = descriptor->MappedAtCreation,
		};
		return P<SilkBuffer>(N.WGPU.wgpuDeviceCreateBuffer(H(device), &native));
	}

	public SW.CommandEncoder* DeviceCreateCommandEncoder(SW.Device* device, SW.CommandEncoderDescriptor* descriptor)
	{
		var native = new N.WGPUCommandEncoderDescriptor { Label = descriptor is null ? default : StringView(descriptor->Label) };
		return P<SW.CommandEncoder>(N.WGPU.wgpuDeviceCreateCommandEncoder(H(device), &native));
	}

	public SW.ComputePipeline* DeviceCreateComputePipeline(SW.Device* device, SW.ComputePipelineDescriptor* descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		var count = Count(descriptor->Compute.ConstantCount);
		var constants = stackalloc N.WGPUConstantEntry[count];
		TranslateConstants(descriptor->Compute.Constants, constants, count);
		var native = new N.WGPUComputePipelineDescriptor
		{
			Label = StringView(descriptor->Label),
			Layout = H(descriptor->Layout),
			Compute = new N.WGPUComputeState
			{
				Module = H(descriptor->Compute.Module),
				EntryPoint = StringView(descriptor->Compute.EntryPoint),
				ConstantCount = descriptor->Compute.ConstantCount,
				Constants = constants,
			},
		};
		return P<SW.ComputePipeline>(N.WGPU.wgpuDeviceCreateComputePipeline(H(device), &native));
	}

	public SW.PipelineLayout* DeviceCreatePipelineLayout(SW.Device* device, SW.PipelineLayoutDescriptor* descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		var count = Count(descriptor->BindGroupLayoutCount);
		var layouts = stackalloc nint[count];
		for (var i = 0; i < count; i++) layouts[i] = H(descriptor->BindGroupLayouts[i]);
		var native = new N.WGPUPipelineLayoutDescriptor
		{
			Label = StringView(descriptor->Label),
			BindGroupLayoutCount = descriptor->BindGroupLayoutCount,
			BindGroupLayouts = (nint)layouts,
		};
		return P<SW.PipelineLayout>(N.WGPU.wgpuDeviceCreatePipelineLayout(H(device), &native));
	}

	public SW.RenderPipeline* DeviceCreateRenderPipeline(SW.Device* device, SW.RenderPipelineDescriptor* descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		var vertex = descriptor->Vertex;
		var vertexConstantCount = Count(vertex.ConstantCount);
		var vertexBufferCount = Count(vertex.BufferCount);
		var attributeCount = 0;
		for (var i = 0; i < vertexBufferCount; i++) attributeCount = checked(attributeCount + Count(vertex.Buffers[i].AttributeCount));
		if (attributeCount > MaxItems) throw new ArgumentOutOfRangeException(nameof(descriptor));

		var vertexConstants = stackalloc N.WGPUConstantEntry[vertexConstantCount];
		var vertexBuffers = stackalloc N.WGPUVertexBufferLayout[vertexBufferCount];
		var attributes = stackalloc N.WGPUVertexAttribute[attributeCount];
		TranslateConstants(vertex.Constants, vertexConstants, vertexConstantCount);
		var attributeOffset = 0;
		for (var i = 0; i < vertexBufferCount; i++)
		{
			var source = vertex.Buffers[i];
			var count = Count(source.AttributeCount);
			for (var j = 0; j < count; j++)
			{
				var attribute = source.Attributes[j];
				attributes[attributeOffset + j] = new N.WGPUVertexAttribute
				{
					Format = E<SW.VertexFormat, N.WGPUVertexFormat>(attribute.Format),
					Offset = attribute.Offset,
					ShaderLocation = attribute.ShaderLocation,
				};
			}
			vertexBuffers[i] = new N.WGPUVertexBufferLayout
			{
				ArrayStride = source.ArrayStride,
				StepMode = E<SW.VertexStepMode, N.WGPUVertexStepMode>(source.StepMode),
				AttributeCount = source.AttributeCount,
				Attributes = attributes + attributeOffset,
			};
			attributeOffset += count;
		}

		var fragmentConstantCount = descriptor->Fragment is null ? 0 : Count(descriptor->Fragment->ConstantCount);
		var targetCount = descriptor->Fragment is null ? 0 : Count(descriptor->Fragment->TargetCount);
		var fragmentConstants = stackalloc N.WGPUConstantEntry[fragmentConstantCount];
		var targets = stackalloc N.WGPUColorTargetState[targetCount];
		var blends = stackalloc N.WGPUBlendState[targetCount];
		N.WGPUFragmentState fragment = default;
		N.WGPUFragmentState* fragmentPtr = null;
		if (descriptor->Fragment is not null)
		{
			var source = *descriptor->Fragment;
			TranslateConstants(source.Constants, fragmentConstants, fragmentConstantCount);
			for (var i = 0; i < targetCount; i++)
			{
				var target = source.Targets[i];
				N.WGPUBlendState* blend = null;
				if (target.Blend is not null)
				{
					blends[i] = Blend(*target.Blend);
					blend = &blends[i];
				}
				targets[i] = new N.WGPUColorTargetState
				{
					Format = TextureFormat(target.Format),
					Blend = blend,
					WriteMask = (N.WGPUColorWriteMask)(uint)target.WriteMask,
				};
			}
			fragment = new N.WGPUFragmentState
			{
				Module = H(source.Module),
				EntryPoint = StringView(source.EntryPoint),
				ConstantCount = source.ConstantCount,
				Constants = fragmentConstants,
				TargetCount = source.TargetCount,
				Targets = targets,
			};
			fragmentPtr = &fragment;
		}

		N.WGPUDepthStencilState depth = default;
		N.WGPUDepthStencilState* depthPtr = null;
		if (descriptor->DepthStencil is not null)
		{
			depth = DepthStencil(*descriptor->DepthStencil);
			depthPtr = &depth;
		}
		var native = new N.WGPURenderPipelineDescriptor
		{
			Label = StringView(descriptor->Label),
			Layout = H(descriptor->Layout),
			Vertex = new N.WGPUVertexState
			{
				Module = H(vertex.Module),
				EntryPoint = StringView(vertex.EntryPoint),
				ConstantCount = vertex.ConstantCount,
				Constants = vertexConstants,
				BufferCount = vertex.BufferCount,
				Buffers = vertexBuffers,
			},
			Primitive = Primitive(descriptor->Primitive),
			DepthStencil = depthPtr,
			Multisample = new N.WGPUMultisampleState
			{
				Count = descriptor->Multisample.Count,
				Mask = descriptor->Multisample.Mask,
				AlphaToCoverageEnabled = descriptor->Multisample.AlphaToCoverageEnabled,
			},
			Fragment = fragmentPtr,
		};
		return P<SW.RenderPipeline>(N.WGPU.wgpuDeviceCreateRenderPipeline(H(device), &native));
	}

	public SW.Sampler* DeviceCreateSampler(SW.Device* device, SW.SamplerDescriptor* descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		var native = new N.WGPUSamplerDescriptor
		{
			Label = StringView(descriptor->Label),
			AddressModeU = E<SW.AddressMode, N.WGPUAddressMode>(descriptor->AddressModeU),
			AddressModeV = E<SW.AddressMode, N.WGPUAddressMode>(descriptor->AddressModeV),
			AddressModeW = E<SW.AddressMode, N.WGPUAddressMode>(descriptor->AddressModeW),
			MagFilter = E<SW.FilterMode, N.WGPUFilterMode>(descriptor->MagFilter),
			MinFilter = E<SW.FilterMode, N.WGPUFilterMode>(descriptor->MinFilter),
			MipmapFilter = E<SW.MipmapFilterMode, N.WGPUMipmapFilterMode>(descriptor->MipmapFilter),
			LodMinClamp = descriptor->LodMinClamp,
			LodMaxClamp = descriptor->LodMaxClamp,
			Compare = E<SW.CompareFunction, N.WGPUCompareFunction>(descriptor->Compare),
			MaxAnisotropy = descriptor->MaxAnisotropy,
		};
		return P<SW.Sampler>(N.WGPU.wgpuDeviceCreateSampler(H(device), &native));
	}

	public SW.ShaderModule* DeviceCreateShaderModule(SW.Device* device, SW.ShaderModuleDescriptor* descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		if (descriptor->NextInChain is null || descriptor->NextInChain->SType != SW.SType.ShaderModuleWgslDescriptor)
		{
			throw new NotSupportedException("Only WGSL shader modules are supported.");
		}
		var source = (SW.ShaderModuleWGSLDescriptor*)descriptor->NextInChain;
		var wgsl = new N.WGPUShaderSourceWGSL
		{
			Chain = new N.WGPUChainedStruct { SType = N.WGPUSType.ShaderSourceWGSL },
			Code = StringView(source->Code),
		};
		var native = new N.WGPUShaderModuleDescriptor { NextInChain = &wgsl.Chain, Label = StringView(descriptor->Label) };
		return P<SW.ShaderModule>(N.WGPU.wgpuDeviceCreateShaderModule(H(device), &native));
	}

	public SW.Texture* DeviceCreateTexture(SW.Device* device, SW.TextureDescriptor* descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		var count = Count(descriptor->ViewFormatCount);
		var formats = stackalloc N.WGPUTextureFormat[count];
		for (var i = 0; i < count; i++) formats[i] = TextureFormat(descriptor->ViewFormats[i]);
		var native = new N.WGPUTextureDescriptor
		{
			Label = StringView(descriptor->Label),
			Usage = (N.WGPUTextureUsage)(ulong)descriptor->Usage,
			Dimension = E<SW.TextureDimension, N.WGPUTextureDimension>(descriptor->Dimension),
			Size = Extent(descriptor->Size),
			Format = TextureFormat(descriptor->Format),
			MipLevelCount = descriptor->MipLevelCount,
			SampleCount = descriptor->SampleCount,
			ViewFormatCount = descriptor->ViewFormatCount,
			ViewFormats = formats,
		};
		return P<SW.Texture>(N.WGPU.wgpuDeviceCreateTexture(H(device), &native));
	}

	public SW.TextureView* TextureCreateView(SW.Texture* texture, SW.TextureViewDescriptor* descriptor)
	{
		if (descriptor is null) return P<SW.TextureView>(N.WGPU.wgpuTextureCreateView(H(texture), null));
		var native = new N.WGPUTextureViewDescriptor
		{
			Label = StringView(descriptor->Label),
			Format = TextureFormat(descriptor->Format),
			Dimension = E<SW.TextureViewDimension, N.WGPUTextureViewDimension>(descriptor->Dimension),
			BaseMipLevel = descriptor->BaseMipLevel,
			MipLevelCount = descriptor->MipLevelCount,
			BaseArrayLayer = descriptor->BaseArrayLayer,
			ArrayLayerCount = descriptor->ArrayLayerCount,
			Aspect = E<SW.TextureAspect, N.WGPUTextureAspect>(descriptor->Aspect),
			Usage = N.WGPUTextureUsage.None,
		};
		return P<SW.TextureView>(N.WGPU.wgpuTextureCreateView(H(texture), &native));
	}

	public SW.BindGroupLayout* ComputePipelineGetBindGroupLayout(SW.ComputePipeline* pipeline, uint index) => P<SW.BindGroupLayout>(N.WGPU.wgpuComputePipelineGetBindGroupLayout(H(pipeline), index));
	public SW.BindGroupLayout* RenderPipelineGetBindGroupLayout(SW.RenderPipeline* pipeline, uint index) => P<SW.BindGroupLayout>(N.WGPU.wgpuRenderPipelineGetBindGroupLayout(H(pipeline), index));

	public SW.ComputePassEncoder* CommandEncoderBeginComputePass(SW.CommandEncoder* encoder, SW.ComputePassDescriptor* descriptor)
	{
		var native = new N.WGPUComputePassDescriptor { Label = descriptor is null ? default : StringView(descriptor->Label) };
		return P<SW.ComputePassEncoder>(N.WGPU.wgpuCommandEncoderBeginComputePass(H(encoder), &native));
	}

	public SW.RenderPassEncoder* CommandEncoderBeginRenderPass(SW.CommandEncoder* encoder, SW.RenderPassDescriptor* descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		var count = Count(descriptor->ColorAttachmentCount);
		var colors = stackalloc N.WGPURenderPassColorAttachment[count];
		for (var i = 0; i < count; i++)
		{
			var source = descriptor->ColorAttachments[i];
			colors[i] = new N.WGPURenderPassColorAttachment
			{
				View = H(source.View),
				DepthSlice = uint.MaxValue,
				ResolveTarget = H(source.ResolveTarget),
				LoadOp = E<SW.LoadOp, N.WGPULoadOp>(source.LoadOp),
				StoreOp = E<SW.StoreOp, N.WGPUStoreOp>(source.StoreOp),
				ClearValue = new N.WGPUColor { R = source.ClearValue.R, G = source.ClearValue.G, B = source.ClearValue.B, A = source.ClearValue.A },
			};
		}
		N.WGPURenderPassDepthStencilAttachment depth = default;
		N.WGPURenderPassDepthStencilAttachment* depthPtr = null;
		if (descriptor->DepthStencilAttachment is not null)
		{
			var source = *descriptor->DepthStencilAttachment;
			depth = new N.WGPURenderPassDepthStencilAttachment
			{
				View = H(source.View),
				DepthLoadOp = E<SW.LoadOp, N.WGPULoadOp>(source.DepthLoadOp),
				DepthStoreOp = E<SW.StoreOp, N.WGPUStoreOp>(source.DepthStoreOp),
				DepthClearValue = source.DepthClearValue,
				DepthReadOnly = source.DepthReadOnly,
				StencilLoadOp = E<SW.LoadOp, N.WGPULoadOp>(source.StencilLoadOp),
				StencilStoreOp = E<SW.StoreOp, N.WGPUStoreOp>(source.StencilStoreOp),
				StencilClearValue = source.StencilClearValue,
				StencilReadOnly = source.StencilReadOnly,
			};
			depthPtr = &depth;
		}
		var native = new N.WGPURenderPassDescriptor
		{
			Label = StringView(descriptor->Label),
			ColorAttachmentCount = descriptor->ColorAttachmentCount,
			ColorAttachments = colors,
			DepthStencilAttachment = depthPtr,
		};
		return P<SW.RenderPassEncoder>(N.WGPU.wgpuCommandEncoderBeginRenderPass(H(encoder), &native));
	}

	public void CommandEncoderCopyBufferToBuffer(SW.CommandEncoder* e, SilkBuffer* s, ulong so, SilkBuffer* d, ulong @do, ulong size) => N.WGPU.wgpuCommandEncoderCopyBufferToBuffer(H(e), H(s), so, H(d), @do, size);
	public void CommandEncoderCopyBufferToTexture(SW.CommandEncoder* e, SW.ImageCopyBuffer* s, SW.ImageCopyTexture* d, SW.Extent3D* z) { var ns = CopyBuffer(*s); var nd = CopyTexture(*d); var nz = Extent(*z); N.WGPU.wgpuCommandEncoderCopyBufferToTexture(H(e), &ns, &nd, &nz); }
	public void CommandEncoderCopyTextureToBuffer(SW.CommandEncoder* e, SW.ImageCopyTexture* s, SW.ImageCopyBuffer* d, SW.Extent3D* z) { var ns = CopyTexture(*s); var nd = CopyBuffer(*d); var nz = Extent(*z); N.WGPU.wgpuCommandEncoderCopyTextureToBuffer(H(e), &ns, &nd, &nz); }
	public void CommandEncoderCopyTextureToTexture(SW.CommandEncoder* e, SW.ImageCopyTexture* s, SW.ImageCopyTexture* d, SW.Extent3D* z) { var ns = CopyTexture(*s); var nd = CopyTexture(*d); var nz = Extent(*z); N.WGPU.wgpuCommandEncoderCopyTextureToTexture(H(e), &ns, &nd, &nz); }
	public SW.CommandBuffer* CommandEncoderFinish(SW.CommandEncoder* e, SW.CommandBufferDescriptor* d) { var native = new N.WGPUCommandBufferDescriptor { Label = d is null ? default : StringView(d->Label) }; return P<SW.CommandBuffer>(N.WGPU.wgpuCommandEncoderFinish(H(e), &native)); }

	public void ComputePassEncoderSetPipeline(SW.ComputePassEncoder* p, SW.ComputePipeline* v) => N.WGPU.wgpuComputePassEncoderSetPipeline(H(p), H(v));
	public void ComputePassEncoderSetBindGroup(SW.ComputePassEncoder* p, uint i, SW.BindGroup* g, nuint c, uint* o) => N.WGPU.wgpuComputePassEncoderSetBindGroup(H(p), i, H(g), c, o);
	public void ComputePassEncoderDispatchWorkgroups(SW.ComputePassEncoder* p, uint x, uint y, uint z) => N.WGPU.wgpuComputePassEncoderDispatchWorkgroups(H(p), x, y, z);
	public void ComputePassEncoderEnd(SW.ComputePassEncoder* p) => N.WGPU.wgpuComputePassEncoderEnd(H(p));
	public void RenderPassEncoderSetPipeline(SW.RenderPassEncoder* p, SW.RenderPipeline* v) => N.WGPU.wgpuRenderPassEncoderSetPipeline(H(p), H(v));
	public void RenderPassEncoderSetBindGroup(SW.RenderPassEncoder* p, uint i, SW.BindGroup* g, nuint c, uint* o) => N.WGPU.wgpuRenderPassEncoderSetBindGroup(H(p), i, H(g), c, o);
	public void RenderPassEncoderSetVertexBuffer(SW.RenderPassEncoder* p, uint i, SilkBuffer* b, ulong o, ulong s) => N.WGPU.wgpuRenderPassEncoderSetVertexBuffer(H(p), i, H(b), o, s);
	public void RenderPassEncoderSetIndexBuffer(SW.RenderPassEncoder* p, SilkBuffer* b, SW.IndexFormat f, ulong o, ulong s) => N.WGPU.wgpuRenderPassEncoderSetIndexBuffer(H(p), H(b), E<SW.IndexFormat, N.WGPUIndexFormat>(f), o, s);
	public void RenderPassEncoderSetScissorRect(SW.RenderPassEncoder* p, uint x, uint y, uint w, uint h) => N.WGPU.wgpuRenderPassEncoderSetScissorRect(H(p), x, y, w, h);
	public void RenderPassEncoderSetStencilReference(SW.RenderPassEncoder* p, uint r) => N.WGPU.wgpuRenderPassEncoderSetStencilReference(H(p), r);
	public void RenderPassEncoderSetViewport(SW.RenderPassEncoder* p, float x, float y, float w, float h, float n, float f) => N.WGPU.wgpuRenderPassEncoderSetViewport(H(p), x, y, w, h, n, f);
	public void RenderPassEncoderDraw(SW.RenderPassEncoder* p, uint v, uint i, uint fv, uint fi) => N.WGPU.wgpuRenderPassEncoderDraw(H(p), v, i, fv, fi);
	public void RenderPassEncoderDrawIndexed(SW.RenderPassEncoder* p, uint i, uint c, uint f, int b, uint fi) => N.WGPU.wgpuRenderPassEncoderDrawIndexed(H(p), i, c, f, b, fi);
	public void RenderPassEncoderEnd(SW.RenderPassEncoder* p) => N.WGPU.wgpuRenderPassEncoderEnd(H(p));

	public void QueueWriteBuffer(SW.Queue* q, SilkBuffer* b, ulong o, void* d, nuint s) => N.WGPU.wgpuQueueWriteBuffer(H(q), H(b), o, (nint)d, s);
	public void QueueWriteTexture(SW.Queue* q, SW.ImageCopyTexture* d, void* p, nuint z, SW.TextureDataLayout* l, SW.Extent3D* s) { var nd = CopyTexture(*d); var nl = CopyLayout(*l); var ns = Extent(*s); N.WGPU.wgpuQueueWriteTexture(H(q), &nd, (nint)p, z, &nl, &ns); }
	public void QueueSubmit(SW.Queue* q, nuint count, SW.CommandBuffer** commands) => N.WGPU.wgpuQueueSubmit(H(q), count, (nint)commands);

	public void BufferMapAsync(SilkBuffer* b, SW.MapMode m, nuint o, nuint s, SW.PfnBufferMapCallback callback, void* userData) => StartMap(b, m, o, s, new MapCompletion { Callback = (nint)(void*)callback.Handle, UserData = (nint)userData });
	public Task<SW.BufferMapAsyncStatus> BufferMapAsyncTask(SilkBuffer* b, SW.MapMode m, nuint o, nuint s)
	{
		var completion = new MapCompletion { Source = new(TaskCreationOptions.RunContinuationsAsynchronously) };
		StartMap(b, m, o, s, completion);
		return completion.Source.Task;
	}
	public void* BufferGetMappedRange(SilkBuffer* b, nuint o, nuint s) => (void*)N.WGPU.wgpuBufferGetMappedRange(H(b), o, s);
	public void* BufferGetConstMappedRange(SilkBuffer* b, nuint o, nuint s) => (void*)N.WGPU.wgpuBufferGetConstMappedRange(H(b), o, s);
	public void BufferUnmap(SilkBuffer* b) => N.WGPU.wgpuBufferUnmap(H(b));
	public void BufferDestroy(SilkBuffer* b) => N.WGPU.wgpuBufferDestroy(H(b));

	public void SurfaceGetCurrentTexture(SW.Surface* surface, SW.SurfaceTexture* target) => throw new NotSupportedException("Presentation is owned by the host render target.");
	public void SurfacePresent(SW.Surface* surface) => throw new NotSupportedException("Presentation is owned by the host render target.");
	public void SurfaceRelease(SW.Surface* surface) { }
	public void BindGroupRelease(SW.BindGroup* v) => Release(H(v), N.WGPU.wgpuBindGroupRelease);
	public void BindGroupLayoutRelease(SW.BindGroupLayout* v) => Release(H(v), N.WGPU.wgpuBindGroupLayoutRelease);
	public void BufferRelease(SilkBuffer* v) => Release(H(v), N.WGPU.wgpuBufferRelease);
	public void CommandBufferRelease(SW.CommandBuffer* v) => Release(H(v), N.WGPU.wgpuCommandBufferRelease);
	public void CommandEncoderRelease(SW.CommandEncoder* v) => Release(H(v), N.WGPU.wgpuCommandEncoderRelease);
	public void ComputePassEncoderRelease(SW.ComputePassEncoder* v) => Release(H(v), N.WGPU.wgpuComputePassEncoderRelease);
	public void ComputePipelineRelease(SW.ComputePipeline* v) => Release(H(v), N.WGPU.wgpuComputePipelineRelease);
	public void PipelineLayoutRelease(SW.PipelineLayout* v) => Release(H(v), N.WGPU.wgpuPipelineLayoutRelease);
	public void RenderPassEncoderRelease(SW.RenderPassEncoder* v) => Release(H(v), N.WGPU.wgpuRenderPassEncoderRelease);
	public void RenderPipelineRelease(SW.RenderPipeline* v) => Release(H(v), N.WGPU.wgpuRenderPipelineRelease);
	public void SamplerRelease(SW.Sampler* v) => Release(H(v), N.WGPU.wgpuSamplerRelease);
	public void ShaderModuleRelease(SW.ShaderModule* v) => Release(H(v), N.WGPU.wgpuShaderModuleRelease);
	public void TextureDestroy(SW.Texture* v) { if (v is not null) N.WGPU.wgpuTextureDestroy(H(v)); }
	public void TextureRelease(SW.Texture* v) => Release(H(v), N.WGPU.wgpuTextureRelease);
	public void TextureViewRelease(SW.TextureView* v) => Release(H(v), N.WGPU.wgpuTextureViewRelease);

	private static void StartMap(SilkBuffer* buffer, SW.MapMode mode, nuint offset, nuint size, MapCompletion completion)
	{
		completion.Handle = GCHandle.Alloc(completion);
		try
		{
			var info = new N.WGPUBufferMapCallbackInfo
			{
				Mode = N.WGPUCallbackMode.AllowSpontaneous,
				Callback = (nint)(delegate* unmanaged[Cdecl]<N.WGPUMapAsyncStatus, N.WGPUStringView, nint, nint, void>)&CompleteMap,
				Userdata1 = GCHandle.ToIntPtr(completion.Handle),
			};
			_ = N.WGPU.wgpuBufferMapAsync(H(buffer), (N.WGPUMapMode)(ulong)mode, offset, size, info);
		}
		catch
		{
			if (completion.Handle.IsAllocated) completion.Handle.Free();
			throw;
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void CompleteMap(N.WGPUMapAsyncStatus status, N.WGPUStringView message, nint userData1, nint userData2)
	{
		var handle = GCHandle.FromIntPtr(userData1);
		if (handle.Target is not MapCompletion completion) return;
		var translated = MapStatus(status);
		try
		{
			completion.Source?.TrySetResult(translated);
			if (completion.Callback != 0)
			{
				var callback = (delegate* unmanaged[Cdecl]<SW.BufferMapAsyncStatus, void*, void>)completion.Callback;
				callback(translated, (void*)completion.UserData);
			}
		}
		finally { if (handle.IsAllocated) handle.Free(); }
	}

	private static N.WGPUPrimitiveState Primitive(SW.PrimitiveState source) => new()
	{
		Topology = E<SW.PrimitiveTopology, N.WGPUPrimitiveTopology>(source.Topology),
		StripIndexFormat = E<SW.IndexFormat, N.WGPUIndexFormat>(source.StripIndexFormat),
		FrontFace = E<SW.FrontFace, N.WGPUFrontFace>(source.FrontFace),
		CullMode = E<SW.CullMode, N.WGPUCullMode>(source.CullMode),
		UnclippedDepth = 0,
	};

	private static N.WGPUDepthStencilState DepthStencil(SW.DepthStencilState source) => new()
	{
		Format = TextureFormat(source.Format),
		DepthWriteEnabled = source.DepthWriteEnabled ? N.WGPUOptionalBool.True : N.WGPUOptionalBool.False,
		DepthCompare = E<SW.CompareFunction, N.WGPUCompareFunction>(source.DepthCompare),
		StencilFront = Stencil(source.StencilFront),
		StencilBack = Stencil(source.StencilBack),
		StencilReadMask = source.StencilReadMask,
		StencilWriteMask = source.StencilWriteMask,
		DepthBias = source.DepthBias,
		DepthBiasSlopeScale = source.DepthBiasSlopeScale,
		DepthBiasClamp = source.DepthBiasClamp,
	};

	private static N.WGPUStencilFaceState Stencil(SW.StencilFaceState source) => new()
	{
		Compare = E<SW.CompareFunction, N.WGPUCompareFunction>(source.Compare),
		FailOp = E<SW.StencilOperation, N.WGPUStencilOperation>(source.FailOp),
		DepthFailOp = E<SW.StencilOperation, N.WGPUStencilOperation>(source.DepthFailOp),
		PassOp = E<SW.StencilOperation, N.WGPUStencilOperation>(source.PassOp),
	};

	private static N.WGPUBlendState Blend(SW.BlendState source) => new() { Color = Blend(source.Color), Alpha = Blend(source.Alpha) };
	private static N.WGPUBlendComponent Blend(SW.BlendComponent source) => new()
	{
		Operation = E<SW.BlendOperation, N.WGPUBlendOperation>(source.Operation),
		SrcFactor = E<SW.BlendFactor, N.WGPUBlendFactor>(source.SrcFactor),
		DstFactor = E<SW.BlendFactor, N.WGPUBlendFactor>(source.DstFactor),
	};

	private static N.WGPUTexelCopyBufferInfo CopyBuffer(SW.ImageCopyBuffer source) => new() { Buffer = H(source.Buffer), Layout = CopyLayout(source.Layout) };
	private static N.WGPUTexelCopyTextureInfo CopyTexture(SW.ImageCopyTexture source) => new()
	{
		Texture = H(source.Texture),
		MipLevel = source.MipLevel,
		Origin = new N.WGPUOrigin3D { X = source.Origin.X, Y = source.Origin.Y, Z = source.Origin.Z },
		Aspect = E<SW.TextureAspect, N.WGPUTextureAspect>(source.Aspect),
	};
	private static N.WGPUTexelCopyBufferLayout CopyLayout(SW.TextureDataLayout source) => new() { Offset = source.Offset, BytesPerRow = source.BytesPerRow, RowsPerImage = source.RowsPerImage };
	private static N.WGPUExtent3D Extent(SW.Extent3D source) => new() { Width = source.Width, Height = source.Height, DepthOrArrayLayers = source.DepthOrArrayLayers };

	private static void TranslateConstants(SW.ConstantEntry* source, N.WGPUConstantEntry* destination, int count)
	{
		for (var i = 0; i < count; i++) destination[i] = new N.WGPUConstantEntry { Key = StringView(source[i].Key), Value = source[i].Value };
	}

	private static N.WGPUTextureFormat TextureFormat(SW.TextureFormat value) => E<SW.TextureFormat, N.WGPUTextureFormat>(value);
	private static SW.BufferMapAsyncStatus MapStatus(N.WGPUMapAsyncStatus status) =>
		status == N.WGPUMapAsyncStatus.Success ? SW.BufferMapAsyncStatus.Success : SW.BufferMapAsyncStatus.Unknown;

	private static N.WGPUStringView StringView(byte* value)
	{
		if (value is null) return default;
		var length = 0;
		while (value[length] != 0) length++;
		return new N.WGPUStringView { Data = (nint)value, Length = (nuint)length };
	}

	private static TTarget E<TSource, TTarget>(TSource value)
		where TSource : struct, Enum where TTarget : struct, Enum
	{
		var name = Enum.GetName(value);
		if (name is not null && Enum.TryParse<TTarget>(name, true, out var mapped)) return mapped;
		if (name is not null)
		{
			var normalized = NormalizeEnumName(typeof(TSource).Name, name);
			foreach (var targetName in Enum.GetNames<TTarget>())
			{
				if (string.Equals(normalized, NormalizeEnumName(typeof(TTarget).Name, targetName), StringComparison.OrdinalIgnoreCase))
				{
					return Enum.Parse<TTarget>(targetName);
				}
			}
		}
		if (Convert.ToUInt64(value, CultureInfo.InvariantCulture) == 0) return default;
		throw new NotSupportedException($"WebGPU enum value {typeof(TSource).Name}.{value} is not supported by {typeof(TTarget).Name}.");
	}

	private static TTarget BindingE<TSource, TTarget>(TSource value)
		where TSource : struct, Enum where TTarget : struct, Enum =>
		Convert.ToUInt64(value, CultureInfo.InvariantCulture) == 0 ? default : E<TSource, TTarget>(value);

	private static string NormalizeEnumName(string typeName, string valueName)
	{
		var coreType = typeName.StartsWith("WGPU", StringComparison.Ordinal) ? typeName[4..] : typeName;
		var coreValue = valueName.StartsWith(coreType, StringComparison.OrdinalIgnoreCase) ? valueName[coreType.Length..] : valueName;
		return coreValue.Replace("_", string.Empty, StringComparison.Ordinal);
	}

	private static int Count(nuint value)
	{
		if (value > MaxItems) throw new ArgumentOutOfRangeException(nameof(value));
		return checked((int)value);
	}

	private static nint H(void* value) => (nint)value;
	private static T* P<T>(nint value) where T : unmanaged => (T*)value;
	private static void Release(nint value, Action<nint> release) { if (value != 0) release(value); }
}

internal sealed class UnoBorrowedWebGpuLifetime(nint device) : IWebGpuExternalDeviceLifetime
{
	public unsafe void Poll(bool wait) => _ = N.WGPU.wgpuDevicePoll(device, wait ? 1u : 0u, null);
	public void Dispose() { }
}
