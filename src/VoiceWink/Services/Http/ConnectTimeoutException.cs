namespace VoiceWink.Services.Http;

/// <summary>
/// A connection-establishment (DNS + TCP + TLS) timeout, raised by
/// <see cref="ConnectTimeoutTranslatingHandler"/> ONLY for the strong runtime shape that
/// <c>SocketsHttpHandler.ConnectTimeout</c> expiry actually produces.
///
/// <para><b>Why the type exists (NET-1).</b> It is the safety boundary of the one carve-out in
/// the "transcription NEVER retries" rule. A connect-phase failure is the single transcription
/// failure class that is provably safe to retry automatically: DNS/TCP/TLS never completed, so
/// no audio bytes were uploaded and no provider work started. Every other failure class may have
/// reached the provider, where a replay risks double-billing or duplicate work.</para>
///
/// <para><b>Why a type and not a shape check.</b> A consumer inferring "connect timeout" from an
/// <see cref="HttpRequestException"/> wrapping an <see cref="OperationCanceledException"/> would
/// be reading the translator's incidental output, not a contract — and would silently widen the
/// carve-out the moment any other path produced that shape. Equally, a subclass thrown for EVERY
/// non-caller cancellation would only prove which handler caught the exception, not that the
/// failure happened before upload (Codex plan review). So the type is emitted from ONE place,
/// under a deliberately narrow shape test, and everything ambiguous stays a plain
/// <see cref="HttpRequestException"/> — retry fails CLOSED.</para>
///
/// <para>It derives from <see cref="HttpRequestException"/> so existing behaviour is unchanged:
/// <c>RetryingHandler</c> still retries it on the ai/images pipelines, and every catch site and
/// user-facing message that keys on <see cref="HttpRequestException"/> keeps working. The
/// message is deliberately identical to the plain translation, so no copy moves.</para>
/// </summary>
internal sealed class ConnectTimeoutException : HttpRequestException
{
    public ConnectTimeoutException(string message, Exception? inner) : base(message, inner) { }
}
