using System.Diagnostics;
using System.Threading.Channels;

using Discord;

using OneObfuscator.Emitters.LuauFrontend;
using OneObfuscator.Engine;
using OneObfuscator.Engine.Exceptions;
using OneObfuscator.Engine.IR;
using OneObfuscator.Engine.IR.Optimization;

namespace Heimdall;

public record IrProgressUpdate(string Stage, TimeSpan Elapsed);

public record IrResult(string UnoptimizedIr, string? OptimizedIr);

public class IrJob(IAttachment attachment, OptimizationPipeline optimizationPipeline, Func<IrProgressUpdate, Task> onProgress)
{
    public IAttachment Attachment { get; init; } = attachment;
    public OptimizationPipeline OptimizationPipeline { get; init; } = optimizationPipeline;
    public Func<IrProgressUpdate, Task> OnProgress { get; init; } = onProgress;
    public TaskCompletionSource<IrResult> Completion { get; } = new();
}

public class IrWorkerPool
{
    private readonly Channel<IrJob> _channel;
    private readonly List<Task> _workers;
    private readonly HttpClient _httpClient;

    public IrWorkerPool(int workerCount, HttpClient httpClient)
    {
        _channel = Channel.CreateUnbounded<IrJob>();
        _workers = [];
        _httpClient = httpClient;

        for (int i = 0; i < workerCount; i++)
        {
            _workers.Add(Task.Run(ProcessQueueAsync));
        }
    }

    private async Task ProcessQueueAsync()
    {
        var pseudoEmitter = new PseudoEmitter();
        const int maxCapacityBeforeReset = 10_000_000; // 10MB before reset. Not tunable via env vars

        await foreach (var job in _channel.Reader.ReadAllAsync())
        {
            try
            {
                await RunJobAsync(job, pseudoEmitter);

                if (pseudoEmitter.Capacity > maxCapacityBeforeReset)
                    pseudoEmitter = new PseudoEmitter();
            }
            catch (Exception ex)
            {
                job.Completion.SetException(ex);
            }
        }
    }

    private async Task RunJobAsync(IrJob job, PseudoEmitter pseudoEmitter)
    {
        var sw = Stopwatch.StartNew();

        string script;
        try
        {
            script = await _httpClient.GetStringAsync(job.Attachment.Url);
        }
        catch (HttpRequestException ex)
        {
            throw new IrProcessingException("Failed to download the attachment. It may have expired or been removed.", ex);
        }

        LuauIR ir;
        try
        {
            ir = ObfuscationPipeline.LowerSource(script);
        }
        catch (InvalidSourceException ex)
        {
            throw new IrProcessingException("Failed to parse the script. Check that it's valid Luau.", ex);
        }
        catch (Exception ex)
        {
            throw new IrProcessingException("An error occured while generating the CGF!\nThis is most likely **NOT** a problem with your script.", ex);
        }

        sw.Stop();
        await job.OnProgress(new IrProgressUpdate("Compiled to IR", sw.Elapsed));

        var emittedUnoptimizedIr = pseudoEmitter.Emit(ir.EntryBlock);

        if (job.OptimizationPipeline.Passes.Count == 0)
        {
            job.Completion.SetResult(new IrResult(emittedUnoptimizedIr, null));
            return;
        }

        sw.Restart();
        try
        {
            job.OptimizationPipeline.Run(ir);
        }
        catch (Exception ex)
        {
            throw new IrProcessingException("An optimization pass failed! This is **NOT** a problem with your script.", ex);
        }
        sw.Stop();

        var emittedOptimizedIr = pseudoEmitter.Emit(ir.EntryBlock);
        await job.OnProgress(new IrProgressUpdate("Optimization complete", sw.Elapsed));

        job.Completion.SetResult(new IrResult(emittedUnoptimizedIr, emittedOptimizedIr));

        Debug.WriteLine("Setting result...");
    }

    public async Task<IrResult> SubmitAsync(IrJob job)
    {
        await _channel.Writer.WriteAsync(job);
        return await job.Completion.Task;
    }
}

public class IrProcessingException(string message, Exception inner) : Exception(message, inner);