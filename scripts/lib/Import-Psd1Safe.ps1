# Import-Psd1Safe - read a .psd1 manifest as DATA, executing nothing. (ASCII-only source, like its
# consumer check-vcredist-payload.ps1: this file is part of that gate's parse surface.)
#
# Shared by the two release gates that read vendored-payload manifests - the pre-pack vendored-payload
# gate (check-vcredist-payload: the VC++ DLLs, the parakeet-server pin, the Vulkan loader) and the
# post-pack signing gate (check-pack-signatures: the loader's per-entry signer + hash expectation, since
# 2026-09-02). One reader, dot-sourced by both, so the two gates cannot disagree about what a manifest
# says or how strictly it is parsed. (The gates are named without a path on purpose: the public-snapshot
# closure check reads script references out of comment lines too, and the post-pack gate is not in the
# snapshot allowlist - a path here would be a reference it must resolve.)
#
# Why an AST walk and not Import-PowerShellDataFile: SafeGetValue() guarantees no code executes, and the
# shape checks below additionally reject a tampered manifest that appends statements after the hashtable
# (a param/begin/process block, a second statement, trailing script text). A manifest is trusted repo
# content, but a gate that would RUN it if someone made it runnable is a gate with a hole in it.
#
# Dot-source it from a gate in scripts/ by joining that gate's own $PSScriptRoot with this file's
# lib-relative name (the pre-pack gate's dot-source line is the model), then call
# `Import-Psd1Safe <path>`; a parse or shape failure THROWS, and callers decide whether that is a
# refusal (it always is, in both gates). No literal path is spelled out here because, read from
# THIS file's directory, it would name scripts/lib/lib/ and trip the snapshot closure check.

function Import-Psd1Safe([string]$path) {
    $tokens = $null
    $perrors = $null
    $fileAst = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$perrors)
    if ($perrors -and $perrors.Count -gt 0) {
        throw "parse error: $($perrors[0].Message)"
    }
    # Require the file to be EXACTLY one top-level hashtable expression and nothing
    # else - no param/begin/process blocks, no extra statements, no trailing script
    # text. SafeGetValue() below already guarantees no code executes, but this also
    # rejects a tampered manifest that appends statements after the hashtable.
    if ($null -ne $fileAst.ParamBlock) { throw "manifest must not contain a param block" }
    if ($null -ne $fileAst.BeginBlock -or $null -ne $fileAst.ProcessBlock) { throw "manifest must not contain begin/process blocks" }
    $end = $fileAst.EndBlock
    if ($null -eq $end -or $end.Statements.Count -ne 1) { throw "manifest must be a single top-level hashtable expression" }
    $pipe = $end.Statements[0] -as [System.Management.Automation.Language.PipelineAst]
    if ($null -eq $pipe -or $pipe.PipelineElements.Count -ne 1) { throw "manifest must be a single hashtable expression" }
    $cmdExpr = $pipe.PipelineElements[0] -as [System.Management.Automation.Language.CommandExpressionAst]
    if ($null -eq $cmdExpr) { throw "manifest top-level statement is not a value expression" }
    $htAst = $cmdExpr.Expression -as [System.Management.Automation.Language.HashtableAst]
    if ($null -eq $htAst) { throw "manifest top-level expression is not a hashtable" }
    return $htAst.SafeGetValue()
}
