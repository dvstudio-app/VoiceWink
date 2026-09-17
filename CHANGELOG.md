# Changelog

The user-visible changes in each VoiceWink release, newest first — highlights only, in plain
language. Minor fixes are grouped into one closing line per release.

For the complete release history and earlier versions, see
https://github.com/dvstudio-app/VoiceWink/releases.

<!-- Format note (maintainers): keep the version headings in exactly their current shape, and
     keep the Unreleased heading at the top - scripts/bump-version.ps1 rolls that section on every
     bump, and the in-app What's-new dialog parses these headings. The script decides the section
     has content by looking for bullet lines, not for sub-headings. Bullets render verbatim in
     that dialog, so keep them short. Do not repeat a literal version heading anywhere in this
     preamble (this comment included): the roll finds the Unreleased section with a plain substring
     search, so an earlier copy of that heading text silently captures it and the release ships
     without a changelog entry. -->

## [Unreleased]

## [1.90.385] - 2026-09-17

- Whispered dictation is no longer rejected as "No speech detected" with Parakeet, and far less often with other models.
- A model download that fails because your disk is full now stops immediately instead of retrying, and keeps what it already downloaded.
- A model download refused for lack of disk space now says so, instead of "Download failed. Please try again."
- A recording that cannot download its model for lack of disk space now says so.
- A stuck image generation now gives up after 5 minutes instead of 10.
- An image generation that times out no longer fills the log viewer with technical detail.
- A model download that hits a flaky connection no longer fills the log viewer with technical detail.

## [1.88.383] - 2026-09-16

- AI cleanups can run longer before they reach a model's output limit.
- An AI cleanup cut off at the model's token limit now gives you the complete transcript and says so, instead of text that stops mid-sentence.
- Metrics: time saved is now based on 40 words per minute of typing, and the page says so.

## [1.87.382] - 2026-09-15

- First public release: the signed installer at voicewink.app, with the free 7-day trial on first run.

## [1.86.381] - 2026-09-14

- About → Support: a "Suggest an improvement" button opens a pre-addressed email.
- Report a problem: the email ends with the VoiceWink version.
- About: a "Third-party licenses" link opens the bundled licence notices.

## [1.85.380] - 2026-09-13

- A model name typed into the AI Enhancement model box no longer appears in VoiceWink's own log lines unless it looks like a model id; provider error responses and the opt-in full prompt log are unchanged.
- Word Replacements: a rule switched off dims immediately instead of after leaving the page.
- Problem reports that include logs now carry a redacted prompt summary (text replaced by its length); the full prompt log stays opt-in.
- The setup wizard's "Help improve VoiceWink" choice now also switches the full prompt log on or off; image prompts stay a separate opt-in in Settings → Diagnostics.
- Word Replacements: the edit row keeps its buttons on the card and the list shows the whole original before trimming.
- Word replacements can be edited in place: the pencil on a row loads it into the boxes above, then Save or Cancel.
- App Modes keep the enhancement you chose for them; it no longer resets to the default when VoiceWink starts.
- The Dictionary page's two descriptions are shorter.
- The Cerebras model list no longer offers gemma-4-31b, which Cerebras stopped serving.
- Filler-word removal no longer deletes real words from Dutch, German and French dictation ("er", "eh", "ah"), and is skipped entirely when a non-English language is selected.
- History is no longer deleted automatically after 7 days by default; the auto-delete option is still available in Settings.
- "Reset all settings" now says what it keeps, and restores Right Ctrl (not Right Alt) as the recording hotkey on keyboards that use AltGr.
- The setup wizard's summary now lists minimize-to-tray, crash reports and the update choices, and says when AI enhancement is on without an API key or model.
- The setup wizard's hotkey step explains Instant recording, and "Send Enter after paste" now says that it sends the message in chat apps.
- Rerunning the setup wizard now updates the AI Enhancement switch on its page.
- "Verbose debug logging" in the Log Viewer now writes debug-level detail to the log, and covers startup too.
- The log opens each session with the app version, Windows version, architecture and the main settings, and problem reports include the same summary.
- Deleting all history no longer deletes the log files.
- The crash-report choice takes effect immediately, in the setup wizard and in Settings.

## [1.84.379] - 2026-09-13

- The Dictionary starts with VoiceWink's own names — the product, the providers, the local models — and three spelling fixes ("voice wing" → VoiceWink, "DeepGram" → Deepgram, "parkit" → Parakeet); remove any you don't need and they stay removed.
- The AI-enhancement model list no longer offers OpenAI's gpt-live-1, a voice-session model that cannot enhance text.
- OpenAI's gpt-image-2.5 Flare and Sunburst now offer the 1K/2K/4K sizes and the 2:1, 1:2 and 9:21 ratios, like gpt-image-2.
- Image options now use each provider's own names: quality reads Low / Medium / High / XHigh / Max (XHigh and Max are new, gpt-image-2.5 only; the old Maximum tier is now High and still sends the same value) and the smallest size reads 512 instead of 0.5K.

## [1.84.378] - 2026-09-13

- Public releases now have a page on GitHub with these notes and the signed installer to download.
- Home shows how long your free trial has left; the last three days turn amber.
- After the free trial ends, the first blocked recording opens the License page for you, and clicking the recorder pill opens it.
- AI enhancement asks reasoning models to answer directly instead of thinking at length, which ends the occasional "No answer: token limit" on Cerebras.
- The re-enhance dialog suggests Qwen 3.8 for Cerebras; Gemma 4 no longer works there, so pick another model if you had it selected.

## [1.83.377] - 2026-09-11

- Send Enter after paste is on by default; switch it off in Settings if you want the caret to stay on the line.
- The License page now names this device, so support can free the right seat when a device dies.

## [1.82.375] - 2026-09-11

- Your license key is re-checked online once after this update, before recording continues.
- Send Enter after paste is now off by default; switch it on in Settings if you use it for chat apps.

## [1.80.373] - 2026-09-09

- Clearer message when an AI model stops before answering.
- License page: the key box shows your stored key (masked) instead of a sentence above it.

## [1.79.372] - 2026-09-09

- Light theme: amber warning and advisory text across the app is now readable (it was too faint).
- License page: when a license has been disabled, the page shows which (masked) key it was.

## [1.78.371] - 2026-09-08

- Privacy Policy: the third-parties section no longer reads as if license checks only happen with cloud features (you will be asked to accept it once).
- Restoring the window no longer adds a second taskbar icon beside the pinned one (if you still see two, unpin and pin it again).
- License page: the check button is a refresh icon beside the status, and the result shows on that line without moving the card.
- A disabled license's status chip reads DISABLED.
- Setup wizard: shorter free-trial text; License page: Start free trial sits between Activate and Buy, and the trial length is stated once.

## [1.78.370] - 2026-09-07

- The license is also re-checked with the server about once a day while VoiceWink stays open.
- The free trial is 7 days, and deleting the app's data no longer restarts it.
- Activating a key does not end the free trial; deactivating returns to the days that were left.
- After a check, the License page says what the license is now.
- "Check now" is the first button on every License panel, and button rows wrap instead of clipping in a narrow window.
- Starting the free trial in the setup wizard takes one click after a failed key entry.
- A recording blocked after the trial says the trial has ended.

## [1.77.369] - 2026-09-06

- The license is re-checked with the server on every start after setup is complete.
- The free trial no longer needs a key or an email.

## [1.76.368] - 2026-09-05

- A license key disabled in the Lemon Squeezy dashboard is now recognised on the next check instead of staying active.
- The License page no longer carries a refused activation's warning onto the tryout card, and a failed check now reads in red.
- The License page's key form is one row of buttons, with the free-trial-key hint always shown.
- License warnings and the status chip are readable in the light theme.

## [1.75.367] - 2026-09-05

- On a PC with two graphics cards, local models now use the dedicated one instead of whichever Windows lists first.
- The GPU acceleration row names the graphics card the selected model is running on.

## [1.74.366] - 2026-09-04

- "Check now" on the License page now tells you what happened, including when it couldn't reach the license server.
- The License page keeps itself up to date instead of showing a state the app has already moved on from.
- Trying VoiceWink without a key is no longer offered on a machine that is already doing so, and that screen now has a way back.
- An expired license key says when it expired, and the status badge agrees with it.
- Several License page messages no longer give a reason the app cannot actually know — being offline, needing to reconnect, or why a key was refused.
- Smaller License page fixes: the disabled-key screen can look up a lost key, the device count no longer reads zero on the machine you just activated, and every Buy button shows where it goes.

## [1.73.365] - 2026-09-04

- Local models switch to the processor when the GPU check finds your graphics card too slow.
- The GPU acceleration row confirms when the selected model is running on your graphics card.
- Whisper no longer transcribes on a graphics card that failed its check; the recording is kept so you can retry on another model.
- The GPU acceleration warning now shows only when it affects the model you have selected.
- The GPU acceleration row says when your graphics card is being checked, and clears its warning the moment you switch it off.

## [1.72.364] - 2026-09-04

- The GPU acceleration switch moved from Settings to the Models page, above the local models it speeds up.
- Setup wizard: the license step keeps its layout when you go back, leads with trying VoiceWink without a key, and shows a Buy button; an expired trial key now leads with Buy on the License page.
- Flipping GPU acceleration now offers to restart VoiceWink so the change applies right away; a restart is refused while a recording or transcription is running.
- If you have crash reporting switched on, a failed graphics-card check now reports your graphics adapters and their driver versions, so a faulty driver can be identified and worked around.

## [1.71.363] - 2026-09-03

- GPU acceleration is checked with a short test clip at start-up; a graphics driver that produces no text is bypassed and local models run on the processor instead.
- A local model that returns no text on the graphics card is retried on the processor, which VoiceWink then keeps using until GPU acceleration is switched off and on again.
- A short recording that captured only silence is treated as no speech instead of restarting the microphone stream; only repeated silent recordings still trigger the restart.
- After a graphics driver update, GPU acceleration is tested again at the next start on its own, instead of waiting for the next VoiceWink update.

## [1.70.362] - 2026-09-03

- Report a problem: if your email app rejects the report, you can now try again before attaching the file yourself
- The log now records which GPU each local speech model is running on
- The GPU acceleration setting shows off and greyed out, with the reason, on a PC that has no GPU driver VoiceWink can use
- Settings now says when local Whisper models are running on the CPU because GPU support could not start on this PC

## [1.69.361] - 2026-09-02

- Models page: local models now show GPU speed stars when GPU acceleration is active, CPU speed stars otherwise
- Setup wizard: the license step now separates the free 7-day trial key from trying without a key, and no longer mentions signing up

## [1.68.360] - 2026-09-02

- Local Whisper models now use your graphics card (GPU) when available — much faster on capable machines, automatic fallback to CPU otherwise
- Parakeet transcription now also uses your graphics card when available — several times faster decoding, automatic fallback to CPU otherwise
- The first dictation after installing no longer pays a one-time GPU preparation delay — it now runs in the background at startup
- New setting: GPU acceleration (on by default, under Settings → General; takes effect at next start)

## [1.67.359] - 2026-09-01

- Fixed Parakeet sometimes returning nothing for a very short dictation
- Parakeet transcribes a bit faster on PCs with more than four processor threads
- Whisper Large V3 Turbo now shows the same accuracy rating as Medium
- The larger local Whisper models transcribe noticeably faster
- Fixed local Whisper occasionally repeating a sentence
- Parakeet now shows a five-star speed rating

## [1.66.358] - 2026-08-31

- On keyboards where Right Alt types characters (AltGr), the setup wizard now suggests Right Ctrl as the recording hotkey
- Hotkey menus and warnings now use friendly key names like "Right Alt" and "Ctrl + Space", with Space, letters and digits in their own sections
- Local Whisper models now show a more realistic speed rating on the Models page
- Accuracy ratings on the Models page now come from real measurements - two models changed rating
- Speed ratings for cloud models now come from real measurements - they all rate equally fast
- Steadier recording waveform: the bars no longer go flat in the pauses between words.
- Hotkeys can now be two-key combinations such as Ctrl+Space, so AltGr stays free for typing € and @.

## [1.65.357] - 2026-08-29

- Fixed: a Bluetooth headset could stay muted for calls after dictating

## [1.64.355] - 2026-08-29

- Speech-model downloads now come from VoiceWink’s own server; the privacy policy was updated to match, so you’ll be asked to accept it once
- Fixed: very quiet recordings could come back empty from a cloud transcription provider
- Speaking faintly against loud background noise is less likely to be discarded as “No speech detected”

## [1.63.354] - 2026-08-26

- Quiet dictation is no longer thrown away — speaking softly, or with your microphone turned down, used to come back as “No speech detected”
- The recording waveform now follows your voice at any microphone level: it rests when you are not speaking, moves while you are, and stays small when the level is too low to transcribe
- Fixed: a microphone that stopped delivering sound was reported as “No speech detected”; VoiceWink now says so plainly and recovers on its own
- Fixed: the “selected mic unavailable” warning was hard to read on top of the live waveform

## [1.62.353] - 2026-08-25

- The old Parakeet engine's leftover files are now removed automatically once the new engine has delivered a transcription
- Fixed: sending a problem report could close VoiceWink instead of opening your email
- Fixed: the faster Parakeet engine never started on installed builds — dictation quietly used the older, slower one, and a brand-new install could not transcribe locally at all

## [1.61.352] - 2026-08-24

- Parakeet transcription is sharply more accurate — far fewer dropped words and sentences; the model updates itself in the background (a one-time 940 MB download), and the Models page then offers to remove the old files
- Updated privacy policy wording about local speech-model downloads; you'll be asked to accept it once
## [1.60.351] - 2026-08-23

- The recorder pill now clears after 10 seconds instead of 15; errors still stay until you dismiss them
- Fixed the taskbar icon turning into a blank Windows icon after reopening the window from the system tray
- Corrected the Lemon Squeezy name in the app and legal documents; the updated legal text asks for one re-acceptance
- Fixed dictated text being pasted twice into slow-responding apps

## [1.59.350] - 2026-08-22

- Updated license agreement and privacy policy (now lawyer-reviewed); you'll be asked to accept them once
- Starting an image generation now shows its progress right away instead of waiting behind a retry message
- History keeps your place when a new transcription arrives instead of jumping back to the top
- Report a problem now explains it when your email app can't take the attachment automatically

## [1.58.349] - 2026-08-17

- The prompt trace now shows which language was used and why

## [1.55.345] - 2026-08-17

- Redo now pastes into the text box you dictated into, instead of falling back to the clipboard
- Redo no longer presses Enter, so a corrected message is never sent without you reading it
- The microphone picker in Settings no longer squeezes its description into a narrow column
- Dictation started while a model or retry dialog is open is copied to the clipboard instead of being typed into VoiceWink

## [1.54.344] - 2026-08-15

- Parakeet no longer drops the final word of some dictations

## [1.53.342] - 2026-08-13

- The mini recorder now says "Enhancing…" for the whole enhancement, instead of swapping in a stop-button hint partway through.

## [1.53.341] - 2026-08-10

- The mini recorder now says "Done" when your text or image is pasted.

## [1.53.339] - 2026-08-09

- VoiceWink can now install updates on its own — on by default, and never while you are recording, transcribing, or generating an image.
- Onboarding now asks whether VoiceWink should check for and install updates automatically.

## [1.52.337] - 2026-08-08

- Quiet audio files now get the same volume boost as quiet recordings before transcription.
- Download and Audio Transcribe buttons now turn into Cancel while working, instead of sitting greyed out beside a separate cancel button.
- Onboarding no longer shows the Terms and Privacy step once you have already accepted them.
- Onboarding now offers local transcription first and selects it by default.
- Onboarding's startup step now also offers "Minimize to tray".
- Transcription model names appear in plain language; an unrecognised selection now says so instead of showing a file name.
- Onboarding's download step now explains its recommendation for your chosen language.
- The unsupported-language warning no longer suggests auto-detect will cope, and during onboarding it names the model to download.
- Fixed: starting a recording twice in quick succession could silently record nothing.
- The recorder now warns within seconds if the microphone stops mid-recording.
- Fixed: a recording started right after a slow one could be cut short.

## [1.51.336] - 2026-08-06

- Fixed: long recordings could stop partway through when transcribing with a local Parakeet model.
- Fixed: quiet recordings could transcribe as empty or incomplete with the Parakeet model.
- Fixed the app taking minutes to become responsive when launched right after a Windows restart.

## [1.50.335] - 2026-08-05

- Recording now starts instantly when you press the hotkey. Settings → Audio → "Instant recording" (on by default) keeps the microphone ready; turn it off to release it between recordings.

## [1.43.334] - 2026-08-05

- Local models now say which engine they run — "Parakeet" and "Whisper Tiny/Base/Small/Medium/Large V3 Turbo" — with Parakeet first and the Whisper models largest first.
- Recordings are filtered before being converted, so high-frequency sound no longer folds down into the speech range.
- Settings is reorganised: a General section at the top, Data Management split into History cleanup, Your data and Settings backup, and the prompt, recording and crash-report options moved to a new Diagnostics section — so the Log Viewer shows the log again.
- The image model list opens instantly, plus smaller fixes and polish.

## [1.42.333] - 2026-08-04

- **Parakeet (Fast Multilingual)** — a new local model, roughly 15× faster than the previous default on CPU. New installs now start on it.
- The English-only local models are gone; if you used one you now use its multilingual version. Local model files VoiceWink no longer supports are deleted, freeing the space they used.
- The Models page now tells you when the selected model cannot transcribe your chosen language, instead of silently detecting it.
- AVIF reference images, a fixed crash in the image options dialog, correct clipboard copying for non-PNG images, and smaller display fixes.

## [1.41.332] - 2026-08-03

- **OpenAI GPT Transcribe** — more accurate than GPT-4o Transcribe and cheaper per minute. Your dictionary and trigger words can be sent to it for better recognition.
- Compact versions of the Small, Medium, Large V3 and Large V3 Turbo local models — same models, roughly a third of the download.
- Choose which microphone VoiceWink records from (Settings → Audio); if it is unavailable, the system default is used for that recording.
- Very soft and whispered recordings are boosted automatically so speech recognition can hear them.
- Cleanup prompts are much better: misheard words are corrected, spoken punctuation becomes real marks, numbers and dates are written normally, long dictation comes back in paragraphs, and nothing is filled in with placeholders like "[Name]". The E-mail prompt adds a greeting and closing only when you actually wanted one.
- A dictation that fails while connecting on a weak network now retries once automatically instead of asking you to tap Retry.
- The recorder shows "Starting…" while the microphone opens, and recording begins a little sooner.
- Every cloud provider now has a "Get API key" link, model lists carry short guidance, and the mini recorder's colours match its state — plus smaller fixes and polish.

## [1.34.331] - 2026-07-31

- The recorder now tells you when enhancement could not run because no model or an unsupported model was selected, instead of quietly pasting the raw transcription.
- Fixed a crash after saving an API key or switching AI providers quickly.
- Tidier Gemini, OpenRouter and Cerebras model lists, with Gemma models now offered.

## [1.34.330] - 2026-07-30

- **Paste reliability**: dictated text now lands in Claude and WhatsApp instead of silently going nowhere, VoiceWink checks that the text actually arrived and retries when it did not, and a paste that fails shows a warning instead of reporting success.
- Switching to another text field at the last moment no longer pastes into the wrong one, and an unresponsive app can no longer grab focus seconds after a paste.
- Right-click the mini recorder to hide it while a long job finishes; restore it from the tray.
- The mini recorder is kept out of screenshots and screen recordings by default.
- A rejected license check now stops recording instead of continuing on the last successful check.
- Clearer license wording, refreshed default models, and smaller fixes.

## [1.34.329] - 2026-07-27

- Prompt and output logging is now available in every build (off by default), and problem reports can include prompt logs and kept audio recordings.
- New option to keep audio recordings for 7 days for debugging.
- Clearer provider error messages in the mini recorder.

## [1.33.328] - 2026-07-26

- Set the speech recognition language per AI enhancement — a translation prompt can carry its own source language.
- Mini-recorder messages now say what happened to your text ("Text pasted", "Enhancement timed out — pasted transcription").
- The Models page notices Whisper files deleted from disk instead of pretending they are still installed.
- The delete-history dialog explains exactly what is removed and what is kept.

## [1.33.327] - 2026-07-25

- Transcription errors now name the real cause instead of blaming your connection or your API key.
- Shorter, plainer status messages in the mini recorder.

## [1.33.326] - 2026-07-24

- Smaller fixes: pages no longer jump back to the top when a button refreshes them, the amber Retry pill no longer reappears endlessly, and no-speech detection is hardened against a rare crash.

## [1.33.325] - 2026-07-24

- Near-silent recordings no longer paste hallucinated text ("Thank you.", etc.) — real voice-activity detection screens recordings, and a blocked recording offers Retry instead of being discarded.
- Export and import your settings to a file (Settings → Settings backup), excluding API keys, license and device-specific state.
- The default local transcription model is now Small (was Base) for better accuracy.
- A new Enable Dictionary switch, a choice of whether minimizing hides to the tray, and smaller fixes.
- Updated the bundled SQLite database engine to fix a security vulnerability (CVE-2025-6965).

## [1.33.324] - 2026-07-19

- Deleting an enhancement that an App Mode uses now warns you first and names the affected App Modes.
- Adding a prompt or an App Mode opens its editor immediately instead of appending a blank one to the list.
- Updated builds refresh the built-in prompts and App-Mode presets when they have been improved; your own custom prompts are kept.
- Smaller fixes and polish.

## [1.33.323] - 2026-07-18

- Prompt trigger words are recognised far more reliably, including when the transcription adds punctuation ("Hey, assistant.").
- Many more Dictionary words now reach the recognizer in each dictation.
- New application icon, plus smaller fixes.

## [1.32.322] - 2026-07-18

- Dictionary words now improve Deepgram transcription automatically, and ElevenLabs transcription via an opt-in toggle (ElevenLabs bills keyterms at +20%).
- "No speech detected" now keeps the recording and offers Retry — switch the transcription model and try again instead of losing the dictation.
- Dictations keep their language through enhancement unless the prompt itself translates, and cleanup prompts no longer answer questions you dictate.
- Improved built-in prompt templates, and ElevenLabs Scribe v1 selections now use Scribe v2.

## [1.31.321] - 2026-07-17

- AI enhancement no longer pastes a reasoning model's internal thought process.
- Push-to-talk no longer stops working after a recording is stopped from the pill, tray, or main window.

## [1.31.320] - 2026-07-17

- Various improvements and bug fixes.

## [1.23.284] - 2026-06-13

- Export my data, Delete transcription history, and Delete all my data (Settings → Data Management).
- In-app About card and a "What's new" dialog after updates.

## [1.23.283] - 2026-06-12

- Starting a free trial no longer leaves the License page stuck on the key-entry form.

## [1.23.281] - 2026-06-03

- VoiceWink checks for updates after launch and daily, with a dot on the Updates sidebar entry.

## [1.23.280] - 2026-05-30

- Internal release-pipeline, installer, and packaging improvements.
