extern alias unofoundation;
extern alias unouwp;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Uno.UI.Composition.Drawing;
using Uno.UI.Composition.ProGpu;
using N = Uno.WebGpu.Native;
using Color = unouwp::Windows.UI.Color;
using FontStretch = unouwp::Windows.UI.Text.FontStretch;
using FontStyle = unouwp::Windows.UI.Text.FontStyle;
using FontWeight = unouwp::Windows.UI.Text.FontWeight;
using Rect = unofoundation::Windows.Foundation.Rect;

var options = BenchmarkOptions.Parse(args);
using var harness = BenchmarkHarness.Create(options);
var result = harness.Run();
var json = JsonSerializer.Serialize(result, BenchmarkJsonContext.Default.BenchmarkResult);
Console.WriteLine(json);
if (options.Output is { Length: > 0 } output)
{
	var fullPath = Path.GetFullPath(output);
	Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
	File.WriteAllText(fullPath, json + Environment.NewLine, Encoding.UTF8);
}

internal sealed record BenchmarkOptions(
	string Backend,
	string Scenario,
	int Warmups,
	int Samples,
	int BatchSize,
	int Batches,
	bool ForceRedraw,
	string? Output,
	string? PixelsOutput)
{
	internal static BenchmarkOptions Parse(string[] args)
	{
		string Value(string name, string fallback)
		{
			var index = Array.IndexOf(args, name);
			return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
		}
		var backend = Value("--backend", "progpu").ToLowerInvariant();
		if (backend is not ("progpu" or "webgpu" or "skia")) throw new ArgumentException("--backend must be progpu, webgpu, or skia.");
		var scenario = Value("--scenario", "cached").ToLowerInvariant();
		if (scenario is not ("cached" or "sparse" or "text" or "paths" or "strokes" or "materials" or "layers" or "isolation-layers" or "mask-layers" or "blend-layers" or "images" or "clips" or "effects")) throw new ArgumentException("--scenario must be cached, sparse, text, paths, strokes, materials, layers, isolation-layers, mask-layers, blend-layers, images, clips, or effects.");
		var warmups = int.Parse(Value("--warmups", "4"), CultureInfo.InvariantCulture);
		var samples = int.Parse(Value("--samples", "100"), CultureInfo.InvariantCulture);
		var batchSize = int.Parse(Value("--batch-size", "60"), CultureInfo.InvariantCulture);
		var batches = int.Parse(Value("--batches", "5"), CultureInfo.InvariantCulture);
		if (warmups < 0 || samples <= 0 || batchSize <= 0 || batches <= 0)
		{
			throw new ArgumentException("Warmups must be non-negative; samples, batch-size, and batches must be positive.");
		}
		return new BenchmarkOptions(
			backend,
			scenario,
			warmups,
			samples,
			batchSize,
			batches,
			Array.IndexOf(args, "--force-redraw") >= 0,
			Value("--output", string.Empty) is { Length: > 0 } path ? path : null,
			Value("--pixels-output", string.Empty) is { Length: > 0 } pixelsPath ? pixelsPath : null);
	}
}

internal sealed class BenchmarkHarness : IDisposable
{
	private const int Width = 1280;
	private const int Height = 720;
	private readonly BenchmarkOptions _options;
	private readonly IDisposable? _deviceOwner;
	private readonly IWebGpuDeviceContext? _device;
	private readonly IDrawingFactory _factory;
	private readonly IRenderTarget _target;
	private readonly IWebGpuRenderTarget? _forcedTargetA;
	private readonly IWebGpuRenderTarget? _forcedTargetB;
	private readonly ProGpuGeometryFactory _geometryFactory = new();
	private readonly ProGpuRenderRecordScope[] _normalRows;
	private readonly ProGpuRenderRecordScope[] _changedRows;
	private readonly IGeometry _path;
	private readonly IGeometry[] _strokes;
	private readonly IShader[] _materials;
	private readonly IColorFilter? _colorMatrixLayer;
	private readonly ITexture _image;
	private readonly IReadOnlyList<GlyphRunElement> _text;
	private readonly IEffectFilter? _backdropBlur;
	private readonly IEffectFilter? _dropShadow;
	private int _forcedTargetIndex;
	private bool _disposed;

	private BenchmarkHarness(BenchmarkOptions options, IDisposable? deviceOwner, IWebGpuDeviceContext? device, IDrawingFactory factory, IRenderTarget target)
	{
		_options = options;
		_deviceOwner = deviceOwner;
		_device = device;
		_factory = factory;
		_target = target;
		if (options.ForceRedraw && target is IWebGpuRenderTarget gpuTarget)
		{
			_forcedTargetA = new RenderTargetAlias(gpuTarget);
			_forcedTargetB = new RenderTargetAlias(gpuTarget);
		}
		(_normalRows, _changedRows) = CreateGridRecords();
		_path = CreatePath();
		_strokes = CreateStrokes();
		_materials = options.Scenario == "materials" ? CreateMaterials() : [];
		_colorMatrixLayer = options.Scenario == "layers"
			? _factory.CreateColorMatrixColorFilter(
			[
				0.15f, 0.15f, 0.70f, 0, 0,
				0.20f, 0.75f, 0.05f, 0, 0,
				0.65f, 0.10f, 0.25f, 0, 0,
				0, 0, 0, 0.85f, 0,
			])
			: null;
		_image = CreateImage();
		_text = CreateText();
		if (options.Scenario == "effects")
		{
			_backdropBlur = _factory.CreateEffectFilter(
				new BlurEffectNode(new SourceInput(), 8f, true),
				new Rect(0, 0, Width, Height)) ??
				throw new InvalidOperationException($"Backend '{options.Backend}' does not support the effects benchmark graph.");
			_dropShadow = _factory.CreateDropShadowFilter(
				4,
				6,
				6,
				6,
				Color.FromArgb(150, 0, 0, 0));
		}
	}

	internal static BenchmarkHarness Create(BenchmarkOptions options)
	{
		if (options.Backend == "skia")
		{
			var context = new SoftwareContext();
			var skiaFactory = new SkiaGraphicsProvider(GraphicsContextKind.Software).CreateGraphics(context);
			return new BenchmarkHarness(options, context, null, skiaFactory, new SoftwareTarget(Width, Height));
		}

		var initType = typeof(N.WGPU).Assembly.GetType("Uno.UI.Composition.WebGpu.WebGpuInitDevice", true)!;
		var owner = (IDisposable)Activator.CreateInstance(initType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [N.WGPUTextureFormat.BGRA8Unorm], null)!;
		var device = (IWebGpuDeviceContext)owner;
		IDrawingFactory factory = options.Backend == "progpu"
			? new ProGpuGraphicsProvider(new ProGpuBackendOptions { FailOnUnsupportedOperation = true }).CreateGraphics(device)
			: new Uno.UI.Composition.WebGpu.WebGpuGraphicsProvider().CreateGraphics(device);
		return new BenchmarkHarness(options, owner, device, factory, GpuTarget.Create(device, Width, Height));
	}

	internal BenchmarkResult Run()
	{
		using var stable = _options.Scenario == "sparse" ? null : Record(0);
		for (var i = 0; i < _options.Warmups; i++)
		{
			using var changing = stable is null ? Record(i) : null;
			_ = Render(stable ?? changing!);
		}

		var totalSamples = new double[_options.Samples];
		var cpuFrameSamples = new double[_options.Samples];
		var completionWaitSamples = _device is null ? null : new double[_options.Samples];
		var proGpuSamples = _options.Backend == "progpu" ? new ProGpuMetricCollector(_options.Samples) : null;
		for (var i = 0; i < totalSamples.Length; i++)
		{
			// Mutation and recording stay outside the timed boundary.
			using var changing = stable is null ? Record(i + _options.Warmups) : null;
			var timing = Render(stable ?? changing!);
			totalSamples[i] = timing.TotalMilliseconds;
			cpuFrameSamples[i] = timing.CpuFrameMilliseconds;
			if (completionWaitSamples is not null)
			{
				completionWaitSamples[i] = timing.GpuCompletionWaitMilliseconds;
			}
			if (timing.ProGpu is { } metrics)
			{
				proGpuSamples!.Add(i, metrics);
			}
		}

		var batched = RunBatched(stable);
		var pixels = ReadPixels();
		var pixelPath = WritePixels(pixels);
		return new BenchmarkResult(
			Schema: "uno-drawing-backend-benchmark/v3",
			Backend: _options.Backend,
			Scenario: _options.Scenario,
			ForceRedraw: _options.ForceRedraw,
			Width,
			Height,
			_options.Warmups,
			Total: Distribution(totalSamples),
			CpuFrame: Distribution(cpuFrameSamples),
			GpuCompletionWait: completionWaitSamples is null ? null : Distribution(completionWaitSamples),
			Batched: batched,
			ProGpu: proGpuSamples?.Build(),
			SemanticStateSha256: SemanticHash(),
			Pixels: new PixelArtifact(
				Format: "BGRA8888Premultiplied",
				ByteLength: pixels.Length,
				Sha256: Convert.ToHexString(SHA256.HashData(pixels)),
				Path: pixelPath),
			UnsupportedOperations: _options.Backend == "progpu" ? ProGpuDiagnostics.UnsupportedOperationCount : 0,
			Environment: new BenchmarkEnvironment(
				DateTimeOffset.UtcNow,
				RuntimeInformation.OSDescription,
				RuntimeInformation.ProcessArchitecture.ToString(),
				RuntimeInformation.FrameworkDescription,
				Environment.ProcessorCount,
				Environment.GetEnvironmentVariable("UNO_WEBGPU_BACKENDS"),
				Environment.GetEnvironmentVariable("UNO_WEBGPU_MSAA")));
	}

	private ProGpuRenderRecordScope Record(int mutation)
	{
		var recorder = _factory.CreateRecording();
		recorder.Clear(Color.FromArgb(255, 8, 12, 20));
		var columns = 32;
		var rows = 24;
		var changedIndex = mutation % (columns * rows);
		var changedRow = changedIndex / columns;
		for (var y = 0; y < rows; y++)
		{
			(y == changedRow ? _changedRows[changedIndex] : _normalRows[y]).Record.Replay(recorder);
		}

		if (_options.Scenario == "paths")
		{
			for (var i = 0; i < 1000; i++)
			{
				recorder.SetMatrix(Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation((i % 40) * 32, (i / 40) * 28, 0));
				recorder.DrawPath(_path, Color.FromArgb(180, 250, 210, 30), true);
			}
			recorder.SetMatrix(Matrix4x4.Identity);
		}

		if (_options.Scenario == "strokes")
		{
			for (var i = 0; i < 1000; i++)
			{
				recorder.SetMatrix(Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation((i % 40) * 32, (i / 40) * 28, 0));
				recorder.DrawPath(_strokes[i % _strokes.Length], Color.FromArgb(220, 80, 180, 250), true);
			}
			recorder.SetMatrix(Matrix4x4.Identity);
		}

		if (_options.Scenario == "materials")
		{
			for (var y = 0; y < 24; y++)
			{
				for (var x = 0; x < 32; x++)
				{
					var restore = recorder.Save();
					recorder.Translate(x * 40, y * 30);
					recorder.DrawRect(new Rect(0, 0, 38, 28), _materials[(y * 32 + x) % _materials.Length], true);
					recorder.RestoreToCount(restore);
				}
			}
		}

		if (_options.Scenario == "layers")
		{
			recorder.SaveLayer(_colorMatrixLayer!);
			for (var y = 0; y < 24; y++)
			{
				for (var x = 0; x < 32; x++)
				{
					var left = x * 40 + 3;
					var top = y * 30 + 2;
					recorder.DrawRoundedRect(
						new Rect(left, top, 32, 24),
						new Vector4(5),
						Color.FromArgb(190, (byte)(235 - x * 4), (byte)(45 + y * 7), 150),
						true);
					recorder.DrawRect(
						new Rect(left + 12, top + 7, 22, 14),
						Color.FromArgb(145, 35, (byte)(100 + x * 4), (byte)(235 - y * 6)),
						true);
				}
			}
			recorder.Restore();
		}

		if (_options.Scenario == "blend-layers")
		{
			recorder.DrawRect(new Rect(0, 0, Width, Height), Color.FromArgb(255, 128, 128, 128));
			recorder.SaveLayer(BlendMode.Multiply);
			for (var y = 0; y < 24; y++)
			{
				for (var x = 0; x < 32; x++)
				{
					var left = x * 40 + 2;
					var top = y * 30 + 2;
					recorder.DrawRoundedRect(
						new Rect(left, top, 28, 24),
						new Vector4(5),
						Color.FromArgb(255, (byte)(235 - x * 4), (byte)(35 + y * 7), 70),
						true);
					recorder.DrawRect(
						new Rect(left + 12, top + 6, 24, 16),
						Color.FromArgb(255, 35, (byte)(105 + x * 4), (byte)(235 - y * 6)),
						true);
				}
			}
			recorder.Restore();
		}

		if (_options.Scenario == "isolation-layers")
		{
			recorder.DrawRect(new Rect(0, 0, Width, Height), Color.FromArgb(255, 128, 128, 128));
			recorder.SaveLayer();
			recorder.ClipRect(new Rect(0, 0, Width, Height), antialias: true);
			recorder.Clear(Color.FromArgb(0, 0, 0, 0));
			for (var y = 0; y < 24; y++)
			{
				for (var x = 0; x < 32; x++)
				{
					var left = x * 40 + 2;
					var top = y * 30 + 2;
					recorder.DrawRoundedRect(
						new Rect(left, top, 28, 24),
						new Vector4(5),
						Color.FromArgb(255, (byte)(235 - x * 4), (byte)(35 + y * 7), 70),
						true);
					recorder.DrawRect(
						new Rect(left + 12, top + 6, 24, 16),
						Color.FromArgb(255, 35, (byte)(105 + x * 4), (byte)(235 - y * 6)),
						true);
				}
			}
			recorder.Restore();
		}

		if (_options.Scenario == "mask-layers")
		{
			recorder.DrawRect(new Rect(0, 0, Width, Height), Color.FromArgb(255, 128, 128, 128));
			recorder.SaveLayer();
			recorder.ClipRect(new Rect(0, 0, Width, Height), antialias: true);
			recorder.Clear(Color.FromArgb(0, 0, 0, 0));
			for (var y = 0; y < 24; y++)
			{
				for (var x = 0; x < 32; x++)
				{
					var left = x * 40 + 2;
					var top = y * 30 + 2;
					recorder.DrawRect(
						new Rect(left, top, 36, 26),
						Color.FromArgb(255, (byte)(235 - x * 4), (byte)(35 + y * 7), (byte)(70 + x * 3)),
						true);
				}
			}
			recorder.SaveLayer(BlendMode.DstIn, antialias: true);
			for (var y = 0; y < 24; y++)
			{
				for (var x = 0; x < 32; x++)
				{
					var left = x * 40 + 8;
					var top = y * 30 + 7;
					recorder.DrawRoundedRect(
						new Rect(left, top, 24, 16),
						new Vector4(7),
						Color.FromArgb(255, 255, 255, 255),
						true);
				}
			}
			recorder.Restore();
			recorder.Restore();
		}

		if (_options.Scenario == "images")
		{
			for (var y = 0; y < 12; y++) for (var x = 0; x < 20; x++) recorder.DrawImage(_image, x * 64, y * 60, ImageSampling.Linear, 0.9f, true);
		}

		if (_options.Scenario == "text")
		{
			for (var y = 0; y < 8; y++) for (var x = 0; x < 16; x++)
			{
				recorder.SetMatrix(Matrix4x4.CreateTranslation(x * 80, y * 72, 0));
				foreach (var element in _text)
				{
					if (element is GlyphOutline outline) recorder.DrawPath(outline.Outline, Color.FromArgb(255, 240, 240, 245), true);
					else if (element is GlyphColorLayers layers) foreach (var layer in layers.Layers) recorder.DrawPath(layer.Geometry, layer.Color, true);
				}
			}
			recorder.SetMatrix(Matrix4x4.Identity);
		}

		if (_options.Scenario == "clips")
		{
			for (var y = 0; y < 12; y++) for (var x = 0; x < 20; x++)
			{
				var restore = recorder.Save();
				recorder.Translate(x * 64 + 4, y * 60 + 4);
				recorder.ClipRoundRect(
					new RoundRectangle
					{
						Rect = new Rect(0, 0, 56, 52),
						TopLeft = new Vector2(9),
						TopRight = new Vector2(7),
						BottomRight = new Vector2(11),
						BottomLeft = new Vector2(5),
					},
					ClipOperation.Intersect,
					true);
				recorder.ClipRect(new Rect(12, 12, 32, 28), ClipOperation.Difference, true);
				recorder.DrawRect(
					new Rect(0, 0, 56, 52),
					Color.FromArgb(220, (byte)(40 + x * 7), (byte)(60 + y * 11), 210),
					true);
				recorder.RestoreToCount(restore);
			}
		}

		if (_options.Scenario == "effects")
		{
			for (var y = 0; y < 3; y++) for (var x = 0; x < 4; x++)
			{
				var left = 36 + x * 312;
				var top = 32 + y * 224;
				var restore = recorder.Save();
				recorder.ClipRoundRect(
					new RoundRectangle
					{
						Rect = new Rect(left, top, 272, 184),
						TopLeft = new Vector2(18),
						TopRight = new Vector2(18),
						BottomRight = new Vector2(18),
						BottomLeft = new Vector2(18),
					},
					ClipOperation.Intersect,
					true);
				recorder.DrawEffectBackdrop(_backdropBlur!, 0.82f);
				recorder.DrawRoundedRect(
					new Rect(left, top, 272, 184),
					new Vector4(18),
					Color.FromArgb(70, 245, 248, 255),
					true);
				recorder.RestoreToCount(restore);

				recorder.SaveLayer(_dropShadow!);
				recorder.DrawRoundedRect(
					new Rect(left + 72, top + 56, 128, 72),
					new Vector4(14),
					Color.FromArgb(230, 32, 72, 148),
					true);
				recorder.Restore();
				// Uno's fallback applies a shadow-only layer, then replays the source.
				recorder.DrawRoundedRect(
					new Rect(left + 72, top + 56, 128, 72),
					new Vector4(14),
					Color.FromArgb(230, 32, 72, 148),
					true);
			}
		}

		return new ProGpuRenderRecordScope(recorder.Finish());
	}

	private (ProGpuRenderRecordScope[] Normal, ProGpuRenderRecordScope[] Changed) CreateGridRecords()
	{
		const int columns = 32;
		const int rows = 24;
		var normal = new ProGpuRenderRecordScope[rows];
		var changed = new ProGpuRenderRecordScope[columns * rows];
		for (var y = 0; y < rows; y++)
		{
			normal[y] = RecordRow(y, changedColumn: -1);
			for (var changedColumn = 0; changedColumn < columns; changedColumn++)
			{
				changed[y * columns + changedColumn] = RecordRow(y, changedColumn);
			}
		}
		return (normal, changed);
	}

	private ProGpuRenderRecordScope RecordRow(int row, int changedColumn)
	{
		const int columns = 32;
		var recorder = _factory.CreateRecording();
		for (var column = 0; column < columns; column++)
		{
			var color = column == changedColumn
				? Color.FromArgb(255, 245, 90, 35)
				: Color.FromArgb(255, (byte)(25 + column * 5), (byte)(30 + row * 7), 120);
			recorder.DrawRect(new Rect(column * 40, row * 30, 38, 28), color);
		}
		return new ProGpuRenderRecordScope(recorder.Finish());
	}

	private FrameTiming Render(ProGpuRenderRecordScope record)
	{
		var total = Stopwatch.StartNew();
		var cpuFrame = Stopwatch.StartNew();
		ProGpuFrameMetrics? metrics;
		if (_target is IWebGpuRenderTarget gpu)
		{
			gpu = GetPresentationTarget(gpu);
			{
				using var present = ((IDrawingFactory<IWebGpuRenderTarget>)_factory).BeginPresent(gpu);
				record.Record.Replay(present);
			}
			cpuFrame.Stop();
			metrics = _options.Backend == "progpu" ? ProGpuDiagnostics.LastFrame : null;
			var completion = Stopwatch.StartNew();
			WaitForGpu();
			completion.Stop();
			total.Stop();
			return new FrameTiming(total.Elapsed.TotalMilliseconds, cpuFrame.Elapsed.TotalMilliseconds, completion.Elapsed.TotalMilliseconds, metrics);
		}

		using (var present = ((IDrawingFactory<ISoftwareRenderTarget>)_factory).BeginPresent((ISoftwareRenderTarget)_target))
		{
			record.Record.Replay(present);
		}
		cpuFrame.Stop();
		total.Stop();
		return new FrameTiming(total.Elapsed.TotalMilliseconds, cpuFrame.Elapsed.TotalMilliseconds, 0, null);
	}

	private BatchedBenchmarkResult? RunBatched(ProGpuRenderRecordScope? stable)
	{
		if (_options.Batches == 0)
		{
			return null;
		}

		var cpuPerFrame = new double[_options.Batches];
		var completionPerBatch = _device is null ? null : new double[_options.Batches];
		var totalPerFrame = new double[_options.Batches];
		for (var batch = 0; batch < _options.Batches; batch++)
		{
			ProGpuRenderRecordScope[]? changing = null;
			if (stable is null)
			{
				changing = new ProGpuRenderRecordScope[_options.BatchSize];
				for (var frame = 0; frame < changing.Length; frame++)
				{
					changing[frame] = Record(_options.Warmups + _options.Samples + batch * _options.BatchSize + frame);
				}
			}

			try
			{
				var total = Stopwatch.StartNew();
				var cpu = Stopwatch.StartNew();
				for (var frame = 0; frame < _options.BatchSize; frame++)
				{
					Submit(stable ?? changing![frame]);
				}
				cpu.Stop();
				cpuPerFrame[batch] = cpu.Elapsed.TotalMilliseconds / _options.BatchSize;
				if (completionPerBatch is not null)
				{
					var completion = Stopwatch.StartNew();
					WaitForGpu();
					completion.Stop();
					completionPerBatch[batch] = completion.Elapsed.TotalMilliseconds;
				}
				total.Stop();
				totalPerFrame[batch] = total.Elapsed.TotalMilliseconds / _options.BatchSize;
			}
			finally
			{
				if (changing is not null)
				{
					foreach (var record in changing)
					{
						record?.Dispose();
					}
				}
			}
		}

		return new BatchedBenchmarkResult(
			_options.BatchSize,
			_options.Batches,
			Distribution(cpuPerFrame),
			completionPerBatch is null ? null : Distribution(completionPerBatch),
			Distribution(totalPerFrame));
	}

	private void Submit(ProGpuRenderRecordScope record)
	{
		if (_target is IWebGpuRenderTarget gpu)
		{
			gpu = GetPresentationTarget(gpu);
			using var present = ((IDrawingFactory<IWebGpuRenderTarget>)_factory).BeginPresent(gpu);
			record.Record.Replay(present);
			return;
		}

		using var softwarePresent = ((IDrawingFactory<ISoftwareRenderTarget>)_factory).BeginPresent((ISoftwareRenderTarget)_target);
		record.Record.Replay(softwarePresent);
	}

	private IWebGpuRenderTarget GetPresentationTarget(IWebGpuRenderTarget target)
	{
		if (_forcedTargetA is null || _forcedTargetB is null)
		{
			return target;
		}

		return (_forcedTargetIndex++ & 1) == 0
			? _forcedTargetA
			: _forcedTargetB;
	}

	private unsafe void WaitForGpu()
	{
		if (_factory is ProGpuDrawingFactory proGpu)
		{
			proGpu.WaitForGpuCompletion();
		}
		else if (_device is not null)
		{
			_ = N.WGPU.wgpuDevicePoll(_device.Device, 1, null);
		}
	}

	private byte[] ReadPixels() => _target switch
	{
		SoftwareTarget software => software.ReadPixels(),
		GpuTarget gpu => gpu.ReadPixels(_device!),
		_ => throw new InvalidOperationException($"Unsupported benchmark target '{_target.GetType().Name}'."),
	};

	private string? WritePixels(byte[] pixels)
	{
		if (_options.PixelsOutput is not { Length: > 0 } output)
		{
			return null;
		}

		var fullPath = Path.GetFullPath(output);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		File.WriteAllBytes(fullPath, pixels);
		return fullPath;
	}

	private IGeometry CreatePath()
	{
		var builder = _geometryFactory.CreatePathBuilder();
		builder.MoveTo(new Vector2(2, 24));
		builder.CubicTo(new Vector2(8, -6), new Vector2(24, 54), new Vector2(30, 4));
		builder.QuadraticTo(new Vector2(16, 22), new Vector2(2, 24));
		builder.Close();
		return builder.Build();
	}

	private IGeometry[] CreateStrokes()
	{
		var arcBuilder = _geometryFactory.CreatePathBuilder();
		arcBuilder.MoveTo(new Vector2(2, 20));
		arcBuilder.ArcTo(new Vector2(14, 11), 18, false, true, new Vector2(30, 20));
		using var arc = arcBuilder.Build();

		var curveBuilder = _geometryFactory.CreatePathBuilder();
		curveBuilder.MoveTo(new Vector2(2, 24));
		curveBuilder.CubicTo(new Vector2(5, -2), new Vector2(25, 46), new Vector2(30, 4));
		curveBuilder.QuadraticTo(new Vector2(18, 20), new Vector2(4, 8));
		using var curve = curveBuilder.Build();

		return
		[
			arc.GetStrokeFillGeometry(new StrokeStyle
			{
				Thickness = 3,
				StartCap = StrokeCap.Round,
				EndCap = StrokeCap.Round,
				DashCap = StrokeCap.Round,
				LineJoin = StrokeJoin.Round,
				MiterLimit = 10,
			}),
			arc.GetStrokeFillGeometry(new StrokeStyle
			{
				Thickness = 2,
				StartCap = StrokeCap.Square,
				EndCap = StrokeCap.Triangle,
				DashCap = StrokeCap.Round,
				LineJoin = StrokeJoin.MiterOrBevel,
				MiterLimit = 4,
				DashArray = [2, 1, 0.5f, 1],
				DashOffset = 0.25f,
			}),
			curve.GetStrokeFillGeometry(new StrokeStyle
			{
				Thickness = 2.5f,
				StartCap = StrokeCap.Triangle,
				EndCap = StrokeCap.Square,
				DashCap = StrokeCap.Butt,
				LineJoin = StrokeJoin.Bevel,
				MiterLimit = 3,
			}),
			curve.GetStrokeFillGeometry(new StrokeStyle
			{
				Thickness = 2,
				StartCap = StrokeCap.Butt,
				EndCap = StrokeCap.Round,
				DashCap = StrokeCap.Square,
				LineJoin = StrokeJoin.Miter,
				MiterLimit = 6,
				DashArray = [3, 1],
				DashOffset = 0.5f,
			}),
		];
	}

	private IShader[] CreateMaterials()
	{
		var warm = new[]
		{
			Color.FromArgb(235, 250, 70, 45),
			Color.FromArgb(220, 250, 195, 35),
			Color.FromArgb(230, 75, 40, 210),
		};
		var cool = new[]
		{
			Color.FromArgb(235, 20, 205, 225),
			Color.FromArgb(220, 35, 80, 235),
			Color.FromArgb(230, 180, 45, 225),
		};
		var hard = new[]
		{
			Color.FromArgb(235, 245, 245, 250),
			Color.FromArgb(235, 30, 40, 65),
			Color.FromArgb(235, 235, 65, 145),
			Color.FromArgb(235, 25, 190, 150),
		};
		var threeStops = new[] { 0f, 0.45f, 1f };
		var hardStops = new[] { 0f, 0.5f, 0.5f, 1f };
		var rotated = Matrix3x2.CreateRotation(0.22f, new Vector2(19, 14));

		return
		[
			_factory.CreateLinearGradientShader(new Vector2(0, 0), new Vector2(38, 28), warm, threeStops, GradientTileMode.Clamp, Matrix3x2.Identity),
			_factory.CreateLinearGradientShader(new Vector2(-8, 14), new Vector2(22, 14), cool, threeStops, GradientTileMode.Repeat, Matrix3x2.Identity),
			_factory.CreateLinearGradientShader(new Vector2(4, 2), new Vector2(34, 26), hard, hardStops, GradientTileMode.Mirror, Matrix3x2.Identity),
			_factory.CreateLinearGradientShader(new Vector2(0, 14), new Vector2(38, 14), warm, threeStops, GradientTileMode.Clamp, rotated),
			_factory.CreateRadialGradientShader(new Vector2(19, 14), new Vector2(19, 14), 20, 15, cool, threeStops, GradientTileMode.Clamp, Matrix3x2.Identity),
			_factory.CreateRadialGradientShader(new Vector2(19, 14), new Vector2(14, 10), 18, 11, warm, threeStops, GradientTileMode.Repeat, Matrix3x2.Identity),
			_factory.CreateRadialGradientShader(new Vector2(19, 14), new Vector2(24, 17), 16, 13, hard, hardStops, GradientTileMode.Mirror, Matrix3x2.Identity),
			_factory.CreateRadialGradientShader(new Vector2(19, 14), new Vector2(16, 12), 21, 9, cool, threeStops, GradientTileMode.Clamp, rotated),
		];
	}

	private ITexture CreateImage()
	{
		var pixels = new byte[64 * 64 * 4];
		for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++)
		{
			var offset = (y * 64 + x) * 4;
			var light = ((x / 8 + y / 8) & 1) == 0;
			pixels[offset] = light ? (byte)220 : (byte)30;
			pixels[offset + 1] = light ? (byte)160 : (byte)50;
			pixels[offset + 2] = light ? (byte)30 : (byte)210;
			pixels[offset + 3] = 255;
		}
		return _factory.CreateTexture(64, 64, pixels);
	}

	private IReadOnlyList<GlyphRunElement> CreateText()
	{
		var font = new ProGpuFontProvider().GetDefaultFont(new FontWeight { Weight = 400 }, FontStretch.Normal, FontStyle.Normal, 18);
		var shaped = font.Shape("ProGPU Uno".AsSpan(), TextDirection.LeftToRight);
		var positions = new Vector2[shaped.Count];
		var pen = 0f;
		for (var i = 0; i < shaped.Count; i++)
		{
			positions[i] = new Vector2(pen, 24) + shaped.Offsets[i];
			pen += shaped.Advances[i];
		}
		var elements = new List<GlyphRunElement>();
		font.BuildGlyphRun(_geometryFactory, shaped.Glyphs, positions, 0, elements);
		return elements;
	}

	private string SemanticHash()
	{
		var extension = _options.Scenario switch
		{
			"clips" => "|clips=240",
			"effects" => "|effectCards=12|sourceReplay=true",
			"strokes" => "|strokes=1000|styles=4|analyticArcs=true|dashes=true",
			"materials" => "|materials=768|linear=4|radial=4|duplicateStops=true|focal=true|anisotropic=true",
			"layers" => "|colorMatrixLayers=1|layerPrimitives=1536|overlap=true|alphaTransform=true",
			"isolation-layers" => "|isolationLayers=1|layerPrimitives=1536|transparentClear=true|opaqueOverlap=true",
			"mask-layers" => "|maskLayers=1|sourcePrimitives=768|maskPrimitives=768|blendMode=dstIn|transparentOutsideMask=true",
			"blend-layers" => "|blendLayers=1|blendMode=multiply|layerPrimitives=1536|opaqueOverlap=true",
			_ => string.Empty,
		};
		var bytes = Encoding.UTF8.GetBytes($"v2|clear=FF080C14|retainedRows=24|{_options.Scenario}|{Width}|{Height}|768|{(_options.Scenario == "text" ? 128 : 0)}|{(_options.Scenario == "paths" ? 1000 : 0)}|{(_options.Scenario == "images" ? 240 : 0)}{extension}");
		return Convert.ToHexString(SHA256.HashData(bytes));
	}

	private static MeasurementDistribution Distribution(double[] values)
	{
		var ordered = values.Order().ToArray();
		return new MeasurementDistribution(
			values,
			Percentile(ordered, 0.50),
			Percentile(ordered, 0.95),
			Percentile(ordered, 0.99),
			ordered[^1],
			MedianAbsoluteDeviation(values));
	}

	private static double Percentile(double[] values, double percentile) => values[Math.Clamp((int)Math.Ceiling(percentile * values.Length) - 1, 0, values.Length - 1)];
	private static double MedianAbsoluteDeviation(double[] values)
	{
		var ordered = values.Order().ToArray();
		var median = Percentile(ordered, 0.5);
		return Percentile(values.Select(value => Math.Abs(value - median)).Order().ToArray(), 0.5);
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_image.Dispose();
		_path.Dispose();
		foreach (var stroke in _strokes)
		{
			stroke.Dispose();
		}
		foreach (var element in _text)
		{
			if (element is GlyphOutline outline) outline.Outline.Dispose();
			else if (element is GlyphColorLayers layers) foreach (var layer in layers.Layers) layer.Geometry.Dispose();
		}
		foreach (var record in _normalRows)
		{
			record.Dispose();
		}
		foreach (var record in _changedRows)
		{
			record.Dispose();
		}
		_backdropBlur?.Dispose();
		_dropShadow?.Dispose();
		_target.Dispose();
		(_factory as IDisposable)?.Dispose();
		_deviceOwner?.Dispose();
	}

	private sealed class ProGpuRenderRecordScope(IRenderRecord record) : IDisposable
	{
		internal IRenderRecord Record { get; } = record;
		public void Dispose() => Record.Dispose();
	}

	private readonly record struct FrameTiming(
		double TotalMilliseconds,
		double CpuFrameMilliseconds,
		double GpuCompletionWaitMilliseconds,
		ProGpuFrameMetrics? ProGpu);

	private sealed class ProGpuMetricCollector
	{
		private readonly double[] _cpuRecord;
		private readonly double[] _cpuSubmit;
		private readonly double[] _compositorFrame;
		private readonly double[] _sceneCompile;
		private readonly double[] _gpuUpload;
		private readonly double[] _renderPass;
		private readonly long[] _uploadBytes;
		private readonly int[] _drawCalls;
		private readonly int[] _vectorVertices;
		private readonly int[] _vectorIndices;
		private readonly int[] _textVertices;
		private readonly int[] _sceneUploadBatches;
		private readonly int[] _sceneUploadCopies;
		private readonly int[] _maskRenderPasses;
		private readonly int[] _maskRenderDrawCalls;
		private readonly int[] _maskTexturePeakDemand;
		private readonly int[] _retainedPictureCount;
		private readonly long[] _retainedPictureHits;
		private readonly long[] _retainedPictureMisses;
		private readonly long[] _retainedPictureCompilations;
		private readonly bool[] _sceneCacheHits;
		private readonly string?[] _sceneCacheMissReasons;
		private readonly bool[] _targetContentReused;

		internal ProGpuMetricCollector(int count)
		{
			_cpuRecord = new double[count];
			_cpuSubmit = new double[count];
			_compositorFrame = new double[count];
			_sceneCompile = new double[count];
			_gpuUpload = new double[count];
			_renderPass = new double[count];
			_uploadBytes = new long[count];
			_drawCalls = new int[count];
			_vectorVertices = new int[count];
			_vectorIndices = new int[count];
			_textVertices = new int[count];
			_sceneUploadBatches = new int[count];
			_sceneUploadCopies = new int[count];
			_maskRenderPasses = new int[count];
			_maskRenderDrawCalls = new int[count];
			_maskTexturePeakDemand = new int[count];
			_retainedPictureCount = new int[count];
			_retainedPictureHits = new long[count];
			_retainedPictureMisses = new long[count];
			_retainedPictureCompilations = new long[count];
			_sceneCacheHits = new bool[count];
			_sceneCacheMissReasons = new string?[count];
			_targetContentReused = new bool[count];
		}

		internal void Add(int index, ProGpuFrameMetrics metrics)
		{
			_cpuRecord[index] = metrics.CpuRecordMilliseconds;
			_cpuSubmit[index] = metrics.CpuSubmitMilliseconds;
			_compositorFrame[index] = metrics.CompositorFrameMilliseconds;
			_sceneCompile[index] = metrics.SceneCompileMilliseconds;
			_gpuUpload[index] = metrics.GpuUploadMilliseconds;
			_renderPass[index] = metrics.RenderPassMilliseconds;
			_uploadBytes[index] = metrics.UploadBytes;
			_drawCalls[index] = metrics.DrawCallCount;
			_vectorVertices[index] = metrics.VectorVertexCount;
			_vectorIndices[index] = metrics.VectorIndexCount;
			_textVertices[index] = metrics.TextVertexCount;
			_sceneUploadBatches[index] = metrics.SceneUploadBatchCount;
			_sceneUploadCopies[index] = metrics.SceneUploadCopyCount;
			_maskRenderPasses[index] = metrics.MaskRenderPassCount;
			_maskRenderDrawCalls[index] = metrics.MaskRenderDrawCallCount;
			_maskTexturePeakDemand[index] = metrics.MaskTexturePeakDemand;
			_retainedPictureCount[index] = metrics.RetainedCompositionPictureCount;
			_retainedPictureHits[index] = metrics.RetainedCompositionPictureHits;
			_retainedPictureMisses[index] = metrics.RetainedCompositionPictureMisses;
			_retainedPictureCompilations[index] = metrics.RetainedCompositionPictureCompilations;
			_sceneCacheHits[index] = metrics.SceneCacheHit;
			_sceneCacheMissReasons[index] = metrics.SceneCacheMissReason;
			_targetContentReused[index] = metrics.TargetContentReused;
		}

		internal ProGpuBenchmarkMetrics Build() => new(
			Distribution(_cpuRecord),
			Distribution(_cpuSubmit),
			Distribution(_compositorFrame),
			Distribution(_sceneCompile),
			Distribution(_gpuUpload),
			Distribution(_renderPass),
			_uploadBytes,
			_drawCalls,
			_vectorVertices,
			_vectorIndices,
			_textVertices,
			_sceneUploadBatches,
			_sceneUploadCopies,
			_maskRenderPasses,
			_maskRenderDrawCalls,
			_maskTexturePeakDemand,
			_retainedPictureCount,
			_retainedPictureHits,
			_retainedPictureMisses,
			_retainedPictureCompilations,
			_sceneCacheHits,
			_sceneCacheMissReasons,
			_targetContentReused);
	}
}

internal sealed class SoftwareContext : IGraphicsContext
{
	public GraphicsContextKind Kind => GraphicsContextKind.Software;
	public void Dispose() { }
}

internal sealed unsafe class SoftwareTarget : ISoftwareRenderTarget
{
	private readonly byte[] _pixels;
	private GCHandle _pin;
	internal SoftwareTarget(int width, int height)
	{
		Width = width;
		Height = height;
		RowBytes = width * 4;
		_pixels = new byte[RowBytes * height];
		_pin = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
	}
	public nint Pixels => _pin.AddrOfPinnedObject();
	public int RowBytes { get; }
	public int Width { get; }
	public int Height { get; }
	public GraphicsColorFormat ColorFormat => GraphicsColorFormat.Bgra8888;
	internal byte[] ReadPixels() => (byte[])_pixels.Clone();
	public void Dispose() { if (_pin.IsAllocated) _pin.Free(); }
}

internal sealed unsafe class GpuTarget : IWebGpuRenderTarget
{
	private nint _texture;
	private nint _view;
	private GpuTarget(nint texture, nint view, int width, int height) { _texture = texture; _view = view; Width = width; Height = height; }
	internal static GpuTarget Create(IWebGpuDeviceContext device, int width, int height)
	{
		var descriptor = new N.WGPUTextureDescriptor
		{
			Size = new N.WGPUExtent3D { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = 1 },
			Format = N.WGPUTextureFormat.BGRA8Unorm,
			MipLevelCount = 1,
			SampleCount = 1,
			Dimension = N.WGPUTextureDimension._2D,
			Usage = N.WGPUTextureUsage.RenderAttachment | N.WGPUTextureUsage.CopySrc | N.WGPUTextureUsage.TextureBinding,
		};
		var texture = N.WGPU.wgpuDeviceCreateTexture(device.Device, &descriptor);
		return new GpuTarget(texture, N.WGPU.wgpuTextureCreateView(texture, null), width, height);
	}
	public nint ColorView => _view;
	public int Width { get; }
	public int Height { get; }
	public GraphicsColorFormat ColorFormat => GraphicsColorFormat.Bgra8888;
	internal byte[] ReadPixels(IWebGpuDeviceContext device)
	{
		var paddedRowBytes = ((Width * 4) + 255) & ~255;
		var totalBytes = checked(paddedRowBytes * Height);
		var bufferDescriptor = new N.WGPUBufferDescriptor
		{
			Size = (ulong)totalBytes,
			Usage = N.WGPUBufferUsage.CopyDst | N.WGPUBufferUsage.MapRead,
		};
		var buffer = N.WGPU.wgpuDeviceCreateBuffer(device.Device, &bufferDescriptor);
		if (buffer == 0)
		{
			throw new InvalidOperationException("Unable to create the benchmark readback buffer.");
		}

		try
		{
			var encoder = N.WGPU.wgpuDeviceCreateCommandEncoder(device.Device, null);
			if (encoder == 0)
			{
				throw new InvalidOperationException("Unable to create the benchmark readback encoder.");
			}
			try
			{
				var source = new N.WGPUTexelCopyTextureInfo
				{
					Texture = _texture,
					Aspect = N.WGPUTextureAspect.All,
				};
				var destination = new N.WGPUTexelCopyBufferInfo
				{
					Buffer = buffer,
					Layout = new N.WGPUTexelCopyBufferLayout
					{
						BytesPerRow = (uint)paddedRowBytes,
						RowsPerImage = (uint)Height,
					},
				};
				var extent = new N.WGPUExtent3D
				{
					Width = (uint)Width,
					Height = (uint)Height,
					DepthOrArrayLayers = 1,
				};
				N.WGPU.wgpuCommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &extent);
				var commandBuffer = N.WGPU.wgpuCommandEncoderFinish(encoder, null);
				try
				{
					N.WGPU.wgpuQueueSubmit(device.Queue, 1, (nint)(&commandBuffer));
				}
				finally
				{
					N.WGPU.wgpuCommandBufferRelease(commandBuffer);
				}
			}
			finally
			{
				N.WGPU.wgpuCommandEncoderRelease(encoder);
			}

			_ = N.WGPU.wgpuDevicePoll(device.Device, 1, null);
			var map = new MapState();
			var mapHandle = GCHandle.Alloc(map);
			try
			{
				var callback = new N.WGPUBufferMapCallbackInfo
				{
					Mode = N.WGPUCallbackMode.AllowProcessEvents,
					Callback = (nint)(delegate* unmanaged[Cdecl]<N.WGPUMapAsyncStatus, N.WGPUStringView, nint, nint, void>)&OnMap,
					Userdata1 = GCHandle.ToIntPtr(mapHandle),
				};
				_ = N.WGPU.wgpuBufferMapAsync(buffer, N.WGPUMapMode.Read, 0, (nuint)totalBytes, callback);
				while (!Volatile.Read(ref map.Completed))
				{
					_ = N.WGPU.wgpuDevicePoll(device.Device, 1, null);
				}
				if (map.Status != N.WGPUMapAsyncStatus.Success)
				{
					throw new InvalidOperationException($"Benchmark target readback failed ({map.Status}).");
				}
			}
			finally
			{
				mapHandle.Free();
			}

			var mapped = (byte*)N.WGPU.wgpuBufferGetConstMappedRange(buffer, 0, (nuint)totalBytes);
			if (mapped is null)
			{
				throw new InvalidOperationException("Benchmark target readback returned a null mapping.");
			}
			var pixels = new byte[checked(Width * Height * 4)];
			for (var row = 0; row < Height; row++)
			{
				new ReadOnlySpan<byte>(mapped + row * paddedRowBytes, Width * 4).CopyTo(pixels.AsSpan(row * Width * 4, Width * 4));
			}
			N.WGPU.wgpuBufferUnmap(buffer);
			return pixels;
		}
		finally
		{
			N.WGPU.wgpuBufferDestroy(buffer);
			N.WGPU.wgpuBufferRelease(buffer);
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void OnMap(N.WGPUMapAsyncStatus status, N.WGPUStringView message, nint userdata1, nint userdata2)
	{
		var state = (MapState)GCHandle.FromIntPtr(userdata1).Target!;
		state.Status = status;
		Volatile.Write(ref state.Completed, true);
	}

	public void Dispose()
	{
		if (_view != 0) { N.WGPU.wgpuTextureViewRelease(_view); _view = 0; }
		if (_texture != 0) { N.WGPU.wgpuTextureDestroy(_texture); N.WGPU.wgpuTextureRelease(_texture); _texture = 0; }
	}

	private sealed class MapState
	{
		internal bool Completed;
		internal N.WGPUMapAsyncStatus Status;
	}
}

internal sealed class RenderTargetAlias(IWebGpuRenderTarget target) : IWebGpuRenderTarget
{
	public nint ColorView => target.ColorView;
	public int Width => target.Width;
	public int Height => target.Height;
	public GraphicsColorFormat ColorFormat => target.ColorFormat;
	public void Dispose() { }
}

internal sealed record BenchmarkResult(
	string Schema,
	string Backend,
	string Scenario,
	bool ForceRedraw,
	int Width,
	int Height,
	int Warmups,
	MeasurementDistribution Total,
	MeasurementDistribution CpuFrame,
	MeasurementDistribution? GpuCompletionWait,
	BatchedBenchmarkResult? Batched,
	ProGpuBenchmarkMetrics? ProGpu,
	string SemanticStateSha256,
	PixelArtifact Pixels,
	long UnsupportedOperations,
	BenchmarkEnvironment Environment);

internal sealed record MeasurementDistribution(
	double[] Samples,
	double Median,
	double P95,
	double P99,
	double Maximum,
	double Mad);

internal sealed record BatchedBenchmarkResult(
	int FramesPerBatch,
	int BatchCount,
	MeasurementDistribution CpuSubmitPerFrame,
	MeasurementDistribution? GpuCompletionPerBatch,
	MeasurementDistribution TotalPerFrame);

internal sealed record ProGpuBenchmarkMetrics(
	MeasurementDistribution CpuRecord,
	MeasurementDistribution CpuSubmit,
	MeasurementDistribution CompositorFrame,
	MeasurementDistribution SceneCompile,
	MeasurementDistribution GpuUpload,
	MeasurementDistribution RenderPass,
	long[] UploadBytes,
	int[] DrawCalls,
	int[] VectorVertices,
	int[] VectorIndices,
	int[] TextVertices,
	int[] SceneUploadBatches,
	int[] SceneUploadCopies,
	int[] MaskRenderPasses,
	int[] MaskRenderDrawCalls,
	int[] MaskTexturePeakDemand,
	int[] RetainedCompositionPictureCount,
	long[] RetainedCompositionPictureHits,
	long[] RetainedCompositionPictureMisses,
	long[] RetainedCompositionPictureCompilations,
	bool[] SceneCacheHits,
	string?[] SceneCacheMissReasons,
	bool[] TargetContentReused);

internal sealed record PixelArtifact(
	string Format,
	int ByteLength,
	string Sha256,
	string? Path);

internal sealed record BenchmarkEnvironment(
	DateTimeOffset TimestampUtc,
	string OperatingSystem,
	string Architecture,
	string Framework,
	int LogicalProcessors,
	string? WebGpuBackends,
	string? WebGpuMsaa);

[System.Text.Json.Serialization.JsonSerializable(typeof(BenchmarkResult))]
internal partial class BenchmarkJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
