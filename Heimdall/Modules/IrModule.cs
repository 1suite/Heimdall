using System.IO.Compression;
using System.Text;

using Discord;
using Discord.Interactions;

using OneObfuscator.Engine.IR.Optimization;
using OneObfuscator.Engine.IR.Optimization.Passes;

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
        bool unreachableBlockEliminationPass = true
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
        if (constantFoldingPass) optimizationPipeline.Passes.Add(new ConstantFoldingPass());
        if (builtInOptimizationPass) optimizationPipeline.Passes.Add(new BuiltInOptimizationPass());
        if (copyPropagationPass) optimizationPipeline.Passes.Add(new CopyPropagationPass());
        if (deadStoreEliminationPass) optimizationPipeline.Passes.Add(new DeadStoreEliminationPass());
        if (deadCodeEliminationPass) optimizationPipeline.Passes.Add(new DeadCodeEliminationPass());
        if (blockFoldingPass) optimizationPipeline.Passes.Add(new BlockFoldingPass());
        if (constantSubstitutionPass) optimizationPipeline.Passes.Add(new ConstantSubstitutionPass());
        if (unreachableBlockEliminationPass) optimizationPipeline.Passes.Add(new UnreachableBlockEliminationPass());
        if (phiEliminationPass) optimizationPipeline.Passes.Add(new PhiEliminationPass());
        if (commonSubexpressionEliminationPass) optimizationPipeline.Passes.Add(new CommonSubexpressionEliminationPass());

        Task ReportProgress(IrProgressUpdate update) =>
            FollowupAsync($":white_check_mark: {update.Stage}! Took `{update.Elapsed.TotalMilliseconds:F0}ms`");

        await FollowupAsync($":hourglass: Submitting job...");
        var job = new IrJob(attachment, optimizationPipeline, ReportProgress);

        IrResult result;
        try
        {
            result = await _pool.SubmitAsync(job);
        }
        catch (IrProcessingException ex)
        {
            await FollowupAsync($":x: {ex.Message}\n\n```cs\n{ex.InnerException}\n```");
            return;
        }
        catch (Exception)
        {
            await FollowupAsync(":x: Something went wrong processing your file. Please try again, and let us know if it keeps happening.");
            return;
        }

        var outputs = new List<(string Name, byte[] Data)>(2)
        {
            ("unoptimized_ir.pseudo.lua", Encoding.UTF8.GetBytes(result.UnoptimizedIr)),
        };

        if (result.OptimizedIr is not null)
        {
            outputs.Add(("optimized_ir.pseudo.lua", Encoding.UTF8.GetBytes(result.OptimizedIr)));
        }

        var longestLengthBytes = Math.Max(outputs[0].Data.Length, outputs[1].Data.Length);

        var files = new List<FileAttachment>(outputs.Count);
        var compressedAny = false;
        var stillTooLarge = false;

        foreach (var (name, data) in outputs)
        {
            if (data.LongLength <= _safeUploadLimitBytes)
            {
                files.Add(new FileAttachment(new MemoryStream(data), name));
                continue;
            }

            var compressed = GzipCompress(data);
            if (compressed.LongLength <= _safeUploadLimitBytes)
            {
                files.Add(new FileAttachment(new MemoryStream(compressed), name + ".gz"));

                compressedAny = true;
            }
            else
            {
                stillTooLarge = true;
            }
        }

        if (stillTooLarge)
        {
            await FollowupAsync(":x: The output is too large to upload even after GZIP compression. Try disabling some optimization passes, or use a smaller input file.");
            return;
        }

        var fileList = result.OptimizedIr is not null
            ? "1. Unoptimized IR\n2. Optimized IR"
            : "1. Unoptimized IR (no optimization passes were enabled)";

        var compressionNote = compressedAny
            ? $"\n\nOne or more output files exceeded our {_safeUploadLimitBytes / 1_000_000}MB limit (the biggest file was around {longestLengthBytes / 1_000_000}MB) and were GZIP-compressed (`.gz`)."
            : "";

        await FollowupWithFilesAsync(
            files,
            $":tada: Done!\n\nFiles:\n{fileList}{compressionNote}\n\n" +

            "The pseudocode follows AT&T syntax: source operand(s) come first, and the destination is always the last operand." +

            "Note: The files below have the `.lua` extension to get successfully embedded in the Discord Client. " +
            "We do not plan on supporting [PUC-Rio (aka vanilla) Lua](<https://www.lua.org/>).\n\n" +

            "_This is not a final product. This is a testing preview. Features are subject to change and may not currently function properly._"
        );
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
