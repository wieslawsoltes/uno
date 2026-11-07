#nullable enable

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.WebGPU;
using Uno.UI.Composition.Drawing;

internal sealed unsafe class GpuTestDevice : IWebGpuDeviceContext
{
	public WebGPU Api { get; }
	public Instance* Instance { get; private set; }
	public Adapter* Adapter { get; private set; }
	public Device* Device { get; private set; }
	public Queue* Queue { get; private set; }
	public TextureFormat ColorFormat => TextureFormat.Bgra8Unorm;
	public GraphicsContextKind Kind => GraphicsContextKind.WebGpu;

	private GpuTestDevice()
	{
		// Exercise Uno's host ABI bridge without requiring a native window in the pixel harness.
		var type = Assembly.Load("Uno.UI.Composition.WebGpu.Init").GetType("Uno.UI.Composition.WebGpu.SilkCompat.SilkCompatContext", throwOnError: true)!;
		Api = (WebGPU)type.GetProperty("Api", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
	}

	internal static IWebGpuDeviceContext Create()
	{
		var result = new GpuTestDevice();
		try
		{
			var init = Assembly.Load("Uno.UI.Composition.WebGpu.Init").GetType("Uno.UI.Composition.WebGpu.WebGpuInitDevice", throwOnError: true)!;
			result.Instance = (Instance*)(nint)init.GetMethod("CreateInstanceHandle", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null)!;
			var adapterOptions = new RequestAdapterOptions { PowerPreference = PowerPreference.HighPerformance };
			nint handle = 0;
			result.Api.InstanceRequestAdapter(result.Instance, &adapterOptions, new PfnRequestAdapterCallback(&OnAdapter), &handle);
			while (Volatile.Read(ref handle) == 0)
			{
				result.Api.InstanceProcessEvents(result.Instance);
			}
			if (handle == -1)
			{
				throw new InvalidOperationException("No WebGPU adapter available.");
			}
			result.Adapter = (Adapter*)handle;
			handle = 0;
			var descriptor = new DeviceDescriptor();
			result.Api.AdapterRequestDevice(result.Adapter, &descriptor, new PfnRequestDeviceCallback(&OnDevice), &handle);
			while (Volatile.Read(ref handle) == 0)
			{
				result.Api.InstanceProcessEvents(result.Instance);
			}
			if (handle == -1)
			{
				throw new InvalidOperationException("WebGPU device creation failed.");
			}
			result.Device = (Device*)handle;
			result.Queue = result.Api.DeviceGetQueue(result.Device);
			var properties = new AdapterProperties();
			result.Api.AdapterGetProperties(result.Adapter, &properties);
			Console.WriteLine($"GPU test device: {properties.BackendType}, {Marshal.PtrToStringUTF8((nint)properties.Name)}");
			return result;
		}
		catch
		{
			result.Dispose();
			throw;
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void OnAdapter(RequestAdapterStatus status, Adapter* adapter, byte* message, void* userdata)
		=> Volatile.Write(ref *(nint*)userdata, status == RequestAdapterStatus.Success ? (nint)adapter : -1);

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void OnDevice(RequestDeviceStatus status, Device* device, byte* message, void* userdata)
		=> Volatile.Write(ref *(nint*)userdata, status == RequestDeviceStatus.Success ? (nint)device : -1);

	internal static void WaitForCompletion(IWebGpuDeviceContext device)
	{
		var status = -1;
		device.Api.QueueOnSubmittedWorkDone(device.Queue, new PfnQueueWorkDoneCallback(&Complete), &status);
		while (Volatile.Read(ref status) < 0)
		{
			device.Api.InstanceProcessEvents(device.Instance);
			Thread.SpinWait(64);
		}
		if ((QueueWorkDoneStatus)status != QueueWorkDoneStatus.Success)
		{
			throw new InvalidOperationException($"Queue completion failed: {(QueueWorkDoneStatus)status}");
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void Complete(QueueWorkDoneStatus status, void* userdata) => Volatile.Write(ref *(int*)userdata, (int)status);

	public void Dispose()
	{
		if (Device != null)
		{
			WaitForCompletion(this);
		}
		if (Queue != null)
		{
			Api.QueueRelease(Queue);
			Queue = null;
		}
		if (Device != null)
		{
			Api.DeviceRelease(Device);
			Device = null;
		}
		if (Adapter != null)
		{
			Api.AdapterRelease(Adapter);
			Adapter = null;
		}
		if (Instance != null)
		{
			Api.InstanceRelease(Instance);
			Instance = null;
		}
	}
}
