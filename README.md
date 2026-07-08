# Heimdall

Discord bot for [OneObfuscator](https://obfuscator.fr/).

Generates and inspects intermediate representation (IR) output for Luau scripts, directly in Discord.

> This is a testing preview. Features are subject to change and may not currently function as expected.

## What it does

Heimdall takes a Luau script attached to a slash command, lowers it to OneObfuscator's internal IR, optionally runs it through a configurable optimization pipeline, and returns both the unoptimized and optimized IR as downloadable pseudocode files, with dynamic per-stage timing reported as the job progresses.

## Commands

### `/ir generate`

Generates the CFG (control flow graph) and IR for a given Luau file.

**Parameters**

| Name | Type | Default | Description |
|---|---|---|---|
| `file` | attachment | — | The `.luau` or `.lua` script to process |
| `block-folding-pass` | bool | `true` | Enable the block folding pass |
| `builtin-optimization-pass` | bool | `true` | Enable the `BuiltIn` call optimization pass |
| `cse-pass` | bool | `true` | Enable common subexpression elimination |
| `constant-folding-pass` | bool | `true` | Enable constant folding |
| `constant-substitution-pass` | bool | `true` | Enable constant substitution |
| `copy-propagation-pass` | bool | `true` | Enable copy propagation |
| `dead-code-elimination-pass` | bool | `true` | Enable dead code elimination |
| `dead-store-elimination-pass` | bool | `true` | Enable dead store elimination |
| `phi-elimination-pass` | bool | `true` | Enable dead phi elimination |
| `unreachable-block-pass` | bool | `true` | Enable unreachable block elimination |

Toggle any pass off to inspect IR at a specific stage of the pipeline, or set every pass to `false` to receive only the unoptimized IR.

**Output**

Two files are returned as followups:

- `unoptimized_ir.pseudo.lua` — IR immediately after lowering, before any optimization
- `optimized_ir.pseudo.lua` — IR after the enabled passes run (omitted if no passes were enabled)

Files use the `.lua` extension so Discord renders them with syntax highlighting in-client. This does not imply compatibility with [PUC-Rio (vanilla) Lua](https://www.lua.org/).

Heimdall's pseudocode output is Luau/IR-specific and there are no plans to support vanilla Lua.

**Pseudocode syntax**

Output follows AT&T syntax: source operand(s) come first, and the destination is always the last operand.

**Large outputs**

If an output file exceeds the configured safe upload size, it's automatically gzip-compressed and returned with a `.gz` suffix. If a file is still too large after compression, the command will report this instead of failing silently. Try disabling optimization passes or reducing the input size.

## Constraints

- Input files must have a `.luau` or `.lua` extension.
- Input files are capped at `MAX_INPUT_FILE_SIZE_BYTES` (see [Configuration](#configuration)).
- Output files are compressed automatically if they exceed `SAFE_UPLOAD_SIZE_LIMIT_BYTES`, staying under Discord's alleged 25MB attachment limit (caution: during testing, requests appeared to fail, even if files did not reach the 25MB ceiling).

## Architecture

Processing is offloaded to a dedicated worker pool (`IrWorkerPool`) backed by an unbounded `System.Threading.Channels.Channel<IrJob>`, so long-running compilation/optimization work never blocks the gateway thread. Each `/ir generate` invocation:

1. Validates the attachment (size, extension) and defers the interaction.
2. Enqueues an `IrJob` carrying the attachment, the configured `OptimizationPipeline`, and a progress callback.
3. A background worker downloads the script, lowers it to IR, and reports elapsed compile time via followup.
4. If any passes are enabled, the worker runs them and reports elapsed optimization time via a second followup.
5. Output is emitted, compressed if necessary, and returned as file attachments in a final followup.

Errors are surfaced as `IrProcessingException` with a message tailored to the failure point (download, parse, or optimization), so users get an accurate explanation rather than a raw stack trace. Unexpected errors fall back to a generic message and are still logged internally.

## Configuration

Set via environment variables:

| Variable | Description |
|---|---|
| `MAX_INPUT_FILE_SIZE_BYTES` | Maximum accepted size for uploaded scripts |
| `SAFE_UPLOAD_SIZE_LIMIT_BYTES` | Threshold above which output files are gzip-compressed before upload |
| `REGISTRY_SERVER_ID` | Guild ID to register the slash commands to |
| `HEIMDALL_DC_TOKEN` | Bot token |

## Requirements

- .NET (matching the version targeted by OneObfuscator.Engine)
- Discord bot token with the `applications.commands` scope authorized on the target guild(s)

## License

```
Copyright (c) 2026 OneSuite ("1suite"). All rights reserved.
No part of this repository may be used, copied, modified, or distributed without prior written permission.
```
