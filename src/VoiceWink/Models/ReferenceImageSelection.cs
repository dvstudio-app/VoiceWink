namespace VoiceWink.Models;

/// <summary>
/// Where a reference-image path was born (ENH-6). The origin is a TRUST decision made
/// only at the site that produced the path and is never upgraded downstream:
/// <see cref="AppImages"/> paths (History Iterate, "Use last image", anything derived
/// from the DB) must canonicalize inside <c>AppPaths.ImagesDir</c> via
/// <c>MediaPathPolicy</c> before any read — a corrupt/imported DB row can never cause
/// an arbitrary file to be uploaded. <see cref="UserPicked"/> paths (the Browse file
/// picker) are deliberately allowed outside the app directory but gated on the
/// extension whitelist, existence, non-zero length, and the size cap
/// (<c>ReferenceImagePolicy</c>). <see cref="AppReferences"/> paths (ENH-6b: a History
/// row's retained copy in <c>AppPaths.ReferencesDir</c> — since ENH-6g
/// content-addressed and possibly SHARED between rows) validate exactly like
/// AppImages but against the References root. Every switch over this enum FAILS
/// CLOSED on unrecognized values — an unknown origin must never fall through to the
/// permissive UserPicked handling.
/// </summary>
public enum ReferenceImageOrigin
{
    AppImages,
    UserPicked,
    AppReferences,
}

/// <summary>
/// A user-selected reference image for image generation (ENH-6): the file path plus
/// its trust origin. Carried through <c>RedoContext</c>, <c>ImageGenOutcome</c>, and
/// <c>EnhancementDialogSelection</c>; the bytes are read ONCE, late, by
/// <c>AIEnhancementService</c> at generation time.
/// </summary>
public sealed record ReferenceImageSelection(string Path, ReferenceImageOrigin Origin);
