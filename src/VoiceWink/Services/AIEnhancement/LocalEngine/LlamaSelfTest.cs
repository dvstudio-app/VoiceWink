using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>Where a model that passed its GPU check runs: whichever of the two returned the same
/// fixed request faster on THIS PC.</summary>
internal enum LlamaRoute
{
    Gpu,
    Cpu,
}

/// <summary>One timed run of the self-test's request. <c>Completed</c> is false when no reply
/// arrived inside the deadline (or the request failed); <c>Correct</c> is the facts check.</summary>
internal readonly record struct LlamaTimedRun(bool Completed, bool Correct, TimeSpan Elapsed, bool Garbled = false);

/// <summary>The GPU self-test's outcome. <c>Unknown</c> (the request did not complete) never pins.</summary>
internal enum LlamaSelfTestVerdict
{
    Unknown,
    Pass,
    Fail,
}

/// <summary>
/// LAI-2: does this GPU produce usable text? One fixed cleanup request — a short dictation that
/// carries four checkable facts — at temperature 0, scored by whether the facts SURVIVE, never by
/// an exact string (a correct model may rephrase). <b>Pass at 3 of 4 facts.</b> The failure it
/// exists for is a driver that loads, reports healthy and then produces garbage tokens (llama.cpp
/// issue 21888, Intel Arc): such output carries none of the facts. Anything that did not complete —
/// timeout, transport error, a non-200 — is <see cref="LlamaSelfTestVerdict.Unknown"/> and never
/// pins the CPU. Re-validate the dictation and the threshold at every llama.cpp bump (the payload
/// manifest's bump procedure says so).
/// </summary>
internal static class LlamaSelfTest
{
    private static ILogger Logger => Log.ForContext(typeof(LlamaSelfTest));

    internal const string SystemInstruction =
        "Clean up this dictated text. Remove filler words and fix punctuation. Reply with the cleaned text only.";

    internal const string Dictation =
        "so um tell Marguerite that the invoice for four thousand two hundred euros is due on Thursday and uh the call moves to half past three";

    /// <summary>The TIMED run's dictation: a paragraph the size of a real one (about 80 words) that
    /// carries the same four facts, so the timed reply is scored by the same rule. Its time is the
    /// model's speed on this PC (owner, 2026-10-03): a ~25-word request could not tell the models
    /// apart, because its time was mostly fixed cost — on the Arc laptop Qwen3.5 4B and Gemma 4
    /// both took about 3.7 s, while their real paragraphs took 5.0 s and 15.4 s.</summary>
    internal const string TimedDictation =
        "so um hi team quick update on the project we finished the first round of testing yesterday and most of the results look good " +
        "there are still two open issues the export sometimes takes too long and uh the settings page does not always save " +
        "I will look into both tomorrow morning oh and tell Marguerite that the invoice for four thousand two hundred euros is due on Thursday " +
        "and the call moves to half past three let me know if that works for you thanks";

    /// <summary>Room for the timed reply: a cleaned ~80-word paragraph is ~110 tokens.</summary>
    internal const int TimedMaxTokens = 256;

    internal const int PassThreshold = 3;

    /// <summary>The four facts, each with every form a correct cleanup may write it in.</summary>
    internal static readonly IReadOnlyList<string[]> FactGroups =
    [
        ["marguerite"],
        ["thursday"],
        ["4,200", "4200", "4.200", "4 200", "four thousand two hundred"],
        ["half past three", "3:30", "3.30", "15:30", "15.30", "three thirty"],
    ];

    /// <summary>How many fact groups the output carries (case-insensitive).</summary>
    internal static int FactsPresent(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return 0;
        }
        var count = 0;
        foreach (var group in FactGroups)
        {
            if (group.Any(form => output.Contains(form, StringComparison.OrdinalIgnoreCase)))
            {
                count++;
            }
        }
        return count;
    }

    internal static LlamaSelfTestVerdict Judge(string? output)
        => FactsPresent(output) >= PassThreshold ? LlamaSelfTestVerdict.Pass : LlamaSelfTestVerdict.Fail;

    internal static string BuildRequestBody()
        => JsonSerializer.Serialize(new
        {
            model = LlamaServerLaunch.ModelAlias,
            messages = new object[]
            {
                new { role = "system", content = SystemInstruction },
                new { role = "user", content = Dictation },
            },
            temperature = 0,
            seed = 42,
            max_tokens = 128,
            stream = false,
        });

    /// <summary>The timed request: the paragraph-sized <see cref="TimedDictation"/>, with the
    /// server's prompt cache off so a run on either route pays for the whole prompt and the two are
    /// comparable.</summary>
    internal static string BuildTimedRequestBody()
        => JsonSerializer.Serialize(new
        {
            model = LlamaServerLaunch.ModelAlias,
            messages = new object[]
            {
                new { role = "system", content = SystemInstruction },
                new { role = "user", content = TimedDictation },
            },
            temperature = 0,
            seed = 42,
            max_tokens = TimedMaxTokens,
            stream = false,
            cache_prompt = false,
        });

    /// <summary>
    /// GPU or CPU for a model whose GPU already passed the check: the CPU only when its run
    /// COMPLETED, was CORRECT, and took no longer than the GPU's. No constant: the rule compares
    /// two measurements of the same request on the same PC. No CPU run at all (the child would not
    /// start), a late one, a failed one or a wrong one all leave the GPU.
    /// </summary>
    internal static LlamaRoute ChooseRoute(TimeSpan gpuElapsed, LlamaTimedRun? cpu)
        => cpu is { Completed: true, Correct: true } run && run.Elapsed <= gpuElapsed ? LlamaRoute.Cpu : LlamaRoute.Gpu;

    /// <summary>
    /// One timed run against <paramref name="lease"/>, whatever it runs on, bounded by
    /// <paramref name="deadline"/>. The clock covers the request only (the child is already
    /// healthy). Never logs the reply. Caller cancellation propagates.
    /// </summary>
    internal static async Task<LlamaTimedRun> RunTimedAsync(
        HttpClient http, LlamaServerProcess.LlamaLease lease, TimeSpan deadline, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(deadline);
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(lease.BaseUri, "v1/chat/completions"))
            {
                Content = new StringContent(BuildTimedRequestBody(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.ApiKey);
            using var response = await http.SendAsync(request, bounded.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new LlamaTimedRun(false, false, watch.Elapsed);
            }
            var content = ReadContent(await response.Content.ReadAsStringAsync(bounded.Token).ConfigureAwait(false));
            var elapsed = watch.Elapsed;
            if (content is null)
                return new LlamaTimedRun(false, false, elapsed);
            // Correct (3 of 4 facts) decides whether the CPU may win the route. Garbled (none of
            // the four) is the broken-driver shape that fails a GPU: a model that condenses the
            // longer paragraph and drops a fact is still a working GPU.
            var facts = FactsPresent(content);
            return new LlamaTimedRun(true, facts >= PassThreshold, elapsed, Garbled: facts == 0);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LlamaTimedRun(false, false, watch.Elapsed);
        }
        catch (HttpRequestException)
        {
            return new LlamaTimedRun(false, false, watch.Elapsed);
        }
    }

    /// <summary>The reply text from a chat-completions body, or null when the shape is not one.</summary>
    internal static string? ReadContent(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>Run the request against <paramref name="lease"/>. Logs the verdict, the fact count
    /// and the latency — never the reply text. A lease without GPU evidence (the child printed no
    /// GPU device) is not tested: its answer would be a CPU verdict filed as a GPU one.</summary>
    internal static async Task<(LlamaSelfTestVerdict Verdict, int Facts, TimeSpan Elapsed)> RunAsync(
        HttpClient http, LlamaServerProcess.LlamaLease lease, TimeSpan timeout, CancellationToken ct)
    {
        if (!lease.OnGpu)
        {
            Logger.Information("llama-server self-test: skipped - the child shows no GPU evidence");
            return (LlamaSelfTestVerdict.Unknown, 0, TimeSpan.Zero);
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(lease.BaseUri, "v1/chat/completions"))
            {
                Content = new StringContent(BuildRequestBody(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.ApiKey);
            using var response = await http.SendAsync(request, bounded.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning("llama-server self-test: HTTP {Status} - verdict Unknown", (int)response.StatusCode);
                return (LlamaSelfTestVerdict.Unknown, 0, watch.Elapsed);
            }
            var content = ReadContent(await response.Content.ReadAsStringAsync(bounded.Token).ConfigureAwait(false));
            if (content is null)
            {
                Logger.Warning("llama-server self-test: the reply is not a chat completion - verdict Unknown");
                return (LlamaSelfTestVerdict.Unknown, 0, watch.Elapsed);
            }
            var facts = FactsPresent(content);
            var verdict = Judge(content);
            Logger.Information("llama-server self-test: {Verdict} ({Facts}/{Groups} facts, {ElapsedMs} ms)",
                verdict, facts, FactGroups.Count, (int)watch.Elapsed.TotalMilliseconds);
            return (verdict, facts, watch.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Logger.Warning("llama-server self-test: no reply within {Timeout} - verdict Unknown", timeout);
            return (LlamaSelfTestVerdict.Unknown, 0, watch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            Logger.Warning("llama-server self-test: transport failure ({Error}) - verdict Unknown", ex.HttpRequestError);
            return (LlamaSelfTestVerdict.Unknown, 0, watch.Elapsed);
        }
    }
}
