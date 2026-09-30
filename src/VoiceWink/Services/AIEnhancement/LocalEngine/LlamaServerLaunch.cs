using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>Where the child computes: <c>Auto</c> lets llama.cpp offload to its chosen GPU (it adds
/// an integrated GPU only when no discrete one exists — <c>src/llama.cpp</c> at b11147); <c>Cpu</c>
/// forces the CPU backend.</summary>
internal enum LlamaLaunchMode
{
    Auto,
    Cpu,
}

/// <summary>
/// LAI-2: the llama-server command line and environment — pure, so every flag is pinned by a case
/// table (<c>LlamaServerLaunchTests</c>). Each fixed flag has a reason:
/// <list type="bullet">
/// <item><c>--host 127.0.0.1 --port 0</c>: loopback only; an ephemeral port read back from the
/// PID-keyed TCP table (never from the child's own output).</item>
/// <item><c>--api-key</c>: a fresh random key per child. Every route but <c>/health</c> answers 401
/// without it (measured on b11147, and asserted by the harness on every run), so another origin on
/// this machine — a web page — cannot drive the server. It rides the command line, which any
/// process running as the same user can read: inside the threat model, since such a process can
/// already do anything the app can.</item>
/// <item><c>--alias local</c>: API responses echo the alias instead of the model PATH.</item>
/// <item><c>--no-webui --offline</c>: no bundled web page, and no network fetch even if a model URL
/// arrived some other way.</item>
/// <item><c>-np 1 -c 8192</c>: one slot, an 8k context (the design plan's cleanup budget).</item>
/// <item><c>--jinja --reasoning off --chat-template-kwargs {"enable_thinking":false}</c>: thinking
/// OFF on the command line — a thinking model blows the dictation deadline and pastes its
/// reasoning (LAI-8). The server confirms it (<c>thinking = 0</c>); a 1 is refused.</item>
/// <item><c>-lv 4</c>: the verbosity at which the device and offload lines print. Nothing raw is
/// ever logged — <see cref="LlamaServerLogLine"/> is the only reader.</item>
/// <item><c>-t</c>: <see cref="Helpers.ParakeetServerPolicy.ServerDecodeThreads"/>, the one
/// child-thread rule.</item>
/// </list>
/// </summary>
internal static class LlamaServerLaunch
{
    /// <summary>The alias API responses carry in place of the model path.</summary>
    internal const string ModelAlias = "local";

    internal const int ContextSize = 8192;

    /// <summary>A fresh per-child API key: 32 random bytes, base64url. Never logged, never persisted.</summary>
    internal static string NewApiKey()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static IReadOnlyList<string> BuildArguments(string modelPath, string apiKey, LlamaLaunchMode mode, int threads)
    {
        var args = new List<string>
        {
            "-m", modelPath,
            "--host", "127.0.0.1",
            "--port", "0",
            "--api-key", apiKey,
            "--alias", ModelAlias,
            "--no-webui",
            "--offline",
            "-np", "1",
            "-c", ContextSize.ToString(CultureInfo.InvariantCulture),
            "--jinja",
            "--reasoning", "off",
            "--chat-template-kwargs", "{\"enable_thinking\":false}",
            "-lv", "4",
            "-t", threads.ToString(CultureInfo.InvariantCulture),
        };
        if (mode == LlamaLaunchMode.Cpu)
        {
            // --device none: no GPU backend at all, not merely zero layers (a Vulkan device would
            // still be initialised, and a broken driver can crash there). The n-gram draft is the
            // design bench's measured CPU arm.
            args.AddRange(["--device", "none", "-ngl", "0",
                "--spec-type", "ngram-simple", "--spec-ngram-simple-size-n", "3", "--spec-ngram-simple-size-m", "16"]);
        }
        else
        {
            // All layers; llama.cpp's own fit logic reduces the count when VRAM is short, and the
            // observed offload is logged.
            args.AddRange(["-ngl", "99"]);
        }
        return args;
    }

    /// <summary>The full command line, argv[0] included, every argument quoted by the
    /// <c>CommandLineToArgvW</c> rules the child's CRT parses with.</summary>
    internal static string BuildCommandLine(string exePath, IEnumerable<string> arguments)
        => string.Join(' ', new[] { exePath }.Concat(arguments).Select(QuoteArgument));

    /// <summary>The child's environment: ours minus every <c>LLAMA_*</c> name (llama.cpp reads
    /// <c>LLAMA_ARG_*</c> for every flag, host and model download included) and
    /// <c>GGML_BACKEND_PATH</c> (a third backend search path). Case-insensitive, like Windows.
    /// <c>GGML_VK_VISIBLE_DEVICES</c> is kept: it is the documented user lever for choosing a GPU.</summary>
    internal static string? BuildEnvironmentBlock(global::System.Collections.IDictionary parent)
        => NativeChildLauncher.BuildEnvironmentBlock(parent, DropFromEnvironment, []);

    internal static bool DropFromEnvironment(string name)
        => name.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "GGML_BACKEND_PATH", StringComparison.OrdinalIgnoreCase);

    /// <summary>Quote one argument so <c>CommandLineToArgvW</c> returns it unchanged: wrap in quotes
    /// when it is empty or holds whitespace or a quote; double the backslashes that precede a quote
    /// (and the closing quote); escape each quote.</summary>
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }
        var sb = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
