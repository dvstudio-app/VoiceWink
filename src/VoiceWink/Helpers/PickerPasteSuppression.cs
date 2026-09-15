namespace VoiceWink.Helpers;

/// <summary>
/// UI-7: should this recording's transcript be COPIED rather than pasted?
///
/// <para>Two operands, and each one is load-bearing in a different direction — which is the whole
/// reason this is a named function with a case table rather than an inline <c>&amp;&amp;</c>:</para>
///
/// <list type="bullet">
/// <item><b>Drop <paramref name="pickerDialogVisible"/></b> and this becomes "never target our own
/// process", the blanket rule the owner explicitly REJECTED — dictating into VoiceWink's own AI
/// Enhancement prompt editor is a working behaviour that must keep pasting there.</item>
/// <item><b>Drop <paramref name="foregroundIsOurOwnProcess"/></b> and a user who opened a picker,
/// then clicked back into their editor and dictated, gets their text copied instead of pasted —
/// punished for a dialog they had already left behind.</item>
/// </list>
///
/// <para>Evaluated ONCE at recording start and latched. Re-asking at paste time would answer a
/// different question, because the dialog is usually gone by then — which is exactly how the
/// defect returns.</para>
/// </summary>
internal static class PickerPasteSuppression
{
    internal static bool ShouldSuppress(bool pickerDialogVisible, bool foregroundIsOurOwnProcess)
        => pickerDialogVisible && foregroundIsOurOwnProcess;
}
