using System.IO.Compression;
using System.Text;

using Discord;
using Discord.Interactions;

using Hydronium.Core.Optimization;
using Hydronium.Core.Optimization.Passes;

namespace Heimdall.Modules;

[Group("ir", "IR-related commands")]
public class IrModule(IrWorkerPool pool) : InteractionModuleBase<SocketInteractionContext>
{
    private readonly long _safeUploadLimitBytes = long.Parse(
        Environment.GetEnvironmentVariable("SAFE_UPLOAD_SIZE_LIMIT_BYTES")
            ?? throw new InvalidOperationException("SAFE_UPLOAD_SIZE_LIMIT_BYTES is missing."));

    private readonly long _maxFileSizeBytes = long.Parse(
        Environment.GetEnvironmentVariable("MAX_INPUT_FILE_SIZE_BYTES")
            ?? throw new InvalidOperationException("MAX_INPUT_FILE_SIZE_BYTES is missing."));

    private readonly IrWorkerPool _pool = pool;

    [SlashCommand("generate", "Generates the CFG for a given Luau file")]
    public async Task Generate(
        [Summary("file", "The script to process")]
        IAttachment attachment,

        [Summary("block-folding-pass", "Sets the block folding pass (default: True)")]
        bool blockFoldingPass = true,

        [Summary("builtin-optimization-pass", "Sets the 'BuiltIn' call optimization pass (default: True)")]
        bool builtInOptimizationPass = true,

        [Summary("cse-pass", "Sets the CSE pass (default: True)")]
        bool commonSubexpressionEliminationPass = true,

        [Summary("constant-folding-pass", "Sets the constant folding pass (default: True)")]
        bool constantFoldingPass = true,

        [Summary("constant-substitution-pass", "Sets the constant substitution pass (default: True)")]
        bool constantSubstitutionPass = true,

        [Summary("copy-propagation-pass", "Sets the copy propagation pass (default: True)")]
        bool copyPropagationPass = true,

        [Summary("dead-code-elimination-pass", "Sets the dead code elimination pass (default: True)")]
        bool deadCodeEliminationPass = true,

        [Summary("dead-store-elimination-pass", "Sets the dead store elimination pass (default: True)")]
        bool deadStoreEliminationPass = true,

        [Summary("phi-elimination-pass", "Sets the dead phi elimination pass (default: True)")]
        bool phiEliminationPass = true,

        [Summary("unreachable-block-pass", "Sets the unreachable block elimination pass (default: True)")]
        bool unreachableBlockEliminationPass = true,

        [Summary("orphaned-closure-cleanup-pass", "Sets the orphaned closure cleanup pass (default: True)")]
        bool orphanedClosureCleanupPass = true,

        [Summary("cell-dead-store-elimination-pass", "Sets the cell dead store elimination pass (default: True)")]
        bool cellDeadStoreEliminationPass = true,

        [Summary("cell-promotion-pass", "Sets the cell promotion pass (default: True)")]
        bool cellPromotionPass = true
    )
    {
        if (attachment.Size > _maxFileSizeBytes)
        {
            await RespondAsync($"That file is too large! Max size is {_maxFileSizeBytes / 1_000_000}MB.", ephemeral: true);
            return;
        }

        if (!attachment.Filename.EndsWith(".luau", StringComparison.OrdinalIgnoreCase)
            && !attachment.Filename.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
        {
            await RespondAsync("That doesn't look like a Luau file! Attach a `.luau` or `.lua` script.", ephemeral: true);
            return;
        }

        await DeferAsync();

        var optimizationPipeline = new OptimizationPipeline();
        if (constantFoldingPass)
        {
            optimizationPipeline.Passes.Add(new ConstantFoldingPass());
        }

        if (builtInOptimizationPass)
        {
            optimizationPipeline.Passes.Add(new BuiltInOptimizationPass());
        }

        if (copyPropagationPass)
        {
            optimizationPipeline.Passes.Add(new CopyPropagationPass());
        }

        if (deadStoreEliminationPass)
        {
            optimizationPipeline.Passes.Add(new DeadStoreEliminationPass());
        }

        if (deadCodeEliminationPass)
        {
            optimizationPipeline.Passes.Add(new DeadCodeEliminationPass());
        }

        if (blockFoldingPass)
        {
            optimizationPipeline.Passes.Add(new BlockFoldingPass());
        }

        if (constantSubstitutionPass)
        {
            optimizationPipeline.Passes.Add(new ConstantSubstitutionPass());
        }

        if (unreachableBlockEliminationPass)
        {
            optimizationPipeline.Passes.Add(new UnreachableBlockEliminationPass());
        }

        if (phiEliminationPass)
        {
            optimizationPipeline.Passes.Add(new PhiEliminationPass());
        }

        if (commonSubexpressionEliminationPass)
        {
            optimizationPipeline.Passes.Add(new CommonSubexpressionEliminationPass());
        }

        if (orphanedClosureCleanupPass)
        {
            optimizationPipeline.Passes.Add(new OrphanedClosureCleanupPass());
        }

        if (cellDeadStoreEliminationPass)
        {
            optimizationPipeline.Passes.Add(new CellDeadStoreEliminationPass());
        }

        if (cellPromotionPass)
        {
            optimizationPipeline.Passes.Add(new CellPromotionPass());
        }

        var messageContents = new StringBuilder();
        messageContents.AppendLine("## :hourglass: Processing...\n\nCurrently processing your input file with the following optimization passes:\n\n```");

        foreach (var pass in optimizationPipeline.Passes)
        {
            messageContents.Append("— ").AppendLine(pass.GetType().Name);
        }

        messageContents.AppendLine("```\n# :stopwatch: Progress\n\n> :incoming_envelope: Submitting IR job to worker pool..."); // Discord code block UI glitch
        var message = await FollowupAsync(messageContents.ToString());

        async Task ReportProgress(string stage, TimeSpan? timeElapsed, bool isSuccess)
        {
            messageContents.Append($"> {(isSuccess ? ":white_check_mark:" : ":x:")} {stage}!");

            if (timeElapsed is TimeSpan elapsed)
            {
                messageContents.Append($" _Took `{elapsed.TotalMilliseconds:F0}ms`_");
            }

            messageContents.AppendLine();
            await message.ModifyAsync(msg => msg.Content = messageContents.ToString());
        }

        var job = new IrJob(attachment, optimizationPipeline, u => ReportProgress(u.Stage, u.Elapsed, true));

        IRResult result;
        try
        {
            result = await _pool.SubmitAsync(job);
        }
        catch (Exception ex)
        {
            await ReportProgress($"An error occured: `{ex.GetType().Name}: {ex.Message}`", null, false);
            await FollowupAsync($"## :x: Error\n\nSomething went wrong processing your file. Please try again, and let us know if it keeps happening.\n\n```cs\n{ex.ToString()[..500]}\n```");
            return;
        }

        var outputs = new List<(string Name, byte[] Data)>(2)
        {
            ("unoptimized_ir.pseudo.lua", Encoding.UTF8.GetBytes(result.UnoptimizedIr)),
        };

        if (result.OptimizedIR is not null)
        {
            outputs.Add(("optimized_ir.pseudo.lua", Encoding.UTF8.GetBytes(result.OptimizedIR)));
        }

        var longestLengthBytes = outputs.Count > 1
            ? Math.Max(outputs[0].Data.Length, outputs[1].Data.Length)
            : outputs[0].Data.Length;

        var streams = new List<MemoryStream>(outputs.Count);
        var files = new List<FileAttachment>(outputs.Count);
        var compressedAny = false;
        var stillTooLarge = false;

        try
        {
            foreach (var (name, data) in outputs)
            {
                if (data.LongLength <= _safeUploadLimitBytes)
                {
                    var stream = new MemoryStream(data);
                    streams.Add(stream);
                    files.Add(new FileAttachment(stream, name));
                    continue;
                }

                var compressed = GzipCompress(data);
                if (compressed.LongLength <= _safeUploadLimitBytes)
                {
                    var stream = new MemoryStream(compressed);
                    streams.Add(stream);
                    files.Add(new FileAttachment(stream, name + ".gz"));
                    compressedAny = true;
                }
                else
                {
                    stillTooLarge = true;
                }
            }

            if (stillTooLarge)
            {
                await FollowupAsync("## :x: Error\n\n The output is too large to upload even after GZIP compression. Try using a smaller input file.");
                return;
            }

            var fileList = result.OptimizedIR is not null
                ? "1. The file named `unoptimized_ir.pseudo.lua` is the unoptimized IR\n2. The file named `optimized_ir.pseudo.lua` is the Optimized IR"
                : "1. The file named `unoptimized_ir.pseudo.lua` is the unoptimized IR (no optimization passes were enabled)";

            var compressionNote = compressedAny
                ? $"\n\nOne or more output files exceeded our {_safeUploadLimitBytes / 1_000_000}MB limit (the biggest file was around {longestLengthBytes / 1_000_000}MB) and were GZIP-compressed (`.gz`)."
                : "";

            await FollowupWithFilesAsync(
                files,
                $"## :tada: Done!\n\n### Files:\n\n{fileList}{compressionNote}\n### Notes:\n\n" +
                "- The pseudocode follows AT&T syntax: source operand(s) come first, and the destination is always the last operand.\n" +
                "- The files below have the `.lua` extension to get successfully embedded in the Discord Client; " +
                "We do not plan on supporting [PUC-Rio (aka vanilla) Lua](<https://www.lua.org/>).\n\n" +
                "_This is not a final product. This is a testing preview. Features are subject to change and may not currently function properly._"
            );
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose(); // oops
            }
        }
    }

    private static byte[] GzipCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            gzip.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }
}