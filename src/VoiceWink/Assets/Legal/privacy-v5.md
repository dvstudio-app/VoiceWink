---
version: 5
last-updated: 2026-09-07
title: VoiceWink Privacy Policy
---

# VoiceWink Privacy Policy

This policy explains what data VoiceWink processes, where it goes, and what rights you have. It is provided in compliance with the EU General Data Protection Regulation (Regulation 2016/679) and Belgian implementing law of 30 July 2018 concerning the protection of natural persons with regard to the processing of personal data.

## 1. Data controller

Dieter Verlaeckt, trading as DV Studio · Haverlaan 28, 2500 Lier · Belgium

Enterprise number (KBO/BCE): BE 1037.153.197

Contact: [support@dvstudio.app](mailto:support@dvstudio.app)

Privacy questions go to the contact address above.

## 2. What VoiceWink and third parties process — and where

**At a glance — what leaves your device.** VoiceWink is local-first: by default almost nothing leaves your device, and the app never sends your audio or transcripts to DV Studio. (What you choose to attach to a support email yourself is the one exception — see Section 7.)

Where a third party processes data for its own purposes — such as Lemon Squeezy's checkout and billing as merchant of record, or a cloud provider you enable under your own account and API key — that processing is carried out under the responsibility of that third party, not DV Studio (see Section 3). For the processing this policy attributes to DV Studio — the license-validation calls, opt-in crash reporting, the update check, the speech-model download, support correspondence, and the websites — DV Studio is the controller (see Sections 3, 5, 6 and 7).

The only data that leaves your device is:

- **License key + activation identifiers** (a random device label on activation; the instance identifier issued by Lemon Squeezy on re-validation and deactivation — no hostname or personal identifier) — to Lemon Squeezy, automatically, to keep your activation valid. See §3 (Category A).
- **IP address, app version, update channel** — to Cloudflare, on the automatic update check, which you can turn off. See §4.
- **A speech-model file request** (the requested file's name — never audio, transcripts, or keys) — to DV Studio's model-download endpoint models.voicewink.app, operated on our behalf by Cloudflare (or, only if that endpoint cannot deliver the file correctly, to Hugging Face as fallback), automatically when a local speech model you selected is not yet on your device (for example on first use). See §3 (Category A) for the Cloudflare-operated endpoint, and the model-download paragraph below.
- **Crash diagnostics** (never transcripts, keys, or your hostname) — to Sentry, only if you opt in to crash reporting. See §3 and §8.
- **Support emails and anything you attach** — to DV Studio, when you email support. See §7.
- **Audio or text for a cloud feature, plus your API key** (to authenticate), and — for some transcription providers — **your Dictionary and trigger words** — to a provider you choose at your own risk and under your own responsibility, not DV Studio, only when you enable that BYOK feature. See §3 (Category B).

Everything not in this list — your recordings, transcripts, settings, dictionary, API keys, reference images, and generated images — stays on your device under %LOCALAPPDATA%/VoiceWink.

VoiceWink runs locally on your Windows device. The categories of data it handles are:

- **Audio captured during recording.** Held transiently in RAM and written to a temporary WAV file on your local disk, which is deleted automatically once the recording has been transcribed. Three situations keep it for longer, all of them on your own device:
  - **A transcription that fails.** The recording is kept so that you can retry it, and is removed when you retry, start a new recording, or close the app. A 7-day sweep is the backstop.
  - **A recording in which VoiceWink detects no speech.** Transcription is not attempted, and the recording is kept in the same way, so that you can still retry it if the detection was wrong.
  - **The keep-recordings debugging option** (off by default). Each recording is moved to a local `Recordings\Debug` folder at the point where it would otherwise be deleted, and is removed automatically after 7 days.

  In all three cases the files stay under %LOCALAPPDATA%/VoiceWink and are never uploaded by DV Studio.
- **Microphone access while the app is running.** With the "Instant recording" setting on (the default), VoiceWink keeps a microphone capture open the whole time the app runs, so that pressing the record hotkey starts capturing immediately rather than after the device spins up. That continuous capture feeds a **memory-only buffer holding at most about the last 10 seconds of audio**, which is continuously overwritten, never written to disk, and never transmitted. Audio from the moment you press the hotkey onward is written to the recording file; the cut is approximate, so a recording can include a brief moment from just before the press (never more than the buffer holds). Everything else is discarded in memory within seconds. Windows shows its microphone-in-use indicator for as long as the app runs, and turning the setting off releases the microphone between recordings.
- **Transcribed text.** Written to your local SQLite database under %LOCALAPPDATA%/VoiceWink, only if history is enabled.
- **Settings and dictionary entries.** Stored locally in JSON and SQLite under %LOCALAPPDATA%/VoiceWink.
- **API keys and license keys.** Stored locally and encrypted via the Windows Data Protection API (DPAPI) so they can only be read by your Windows user account on this device. (When you use a BYOK cloud feature, the relevant provider key is also sent to that provider — and only that provider — to authenticate the request; see Section 3 Category B.)
- **Generated images.** If you use the image-generation feature, the images returned by the provider you selected are written to your local disk under %LOCALAPPDATA%/VoiceWink. They are not sent anywhere by DV Studio.
- **Reference images you attach to an image generation.** When History is enabled and you attach one or more reference images — your own files, or images VoiceWink generated earlier — a **local copy of each** is stored under %LOCALAPPDATA%/VoiceWink so that regenerating still works after you restart the app or move the original. A copy is **kept for as long as any History entry references it** and is erased once the last such entry is deleted; where a regeneration is still using a copy at that moment, erasure is deferred until that use finishes. With History disabled, no copy is kept. "Delete all my data" removes the whole folder immediately. The copies are local only — a reference image leaves your device solely as part of an image-generation request you explicitly trigger, which carries the image regardless of whether a copy is retained.
- **Crash reports (opt-in).** Sent to Sentry only if you have explicitly enabled crash reporting. Stack traces, app version, OS version, your graphics adapter models and display driver versions, and redacted log breadcrumbs are sent. Transcriptions, API keys, license keys, and your machine hostname are never sent.
- **Problem-report bundles and diagnostic logs.** A report you build with "Report a problem" is written to a local `Reports` folder and removed after 7 days if it is never sent; it is covered by the in-app "Delete all data" control and by the GDPR export's media option. If you switch on the optional prompt-trace logging (off by default), the dictated text and AI prompts are additionally written to local log files, which are swept after 7 days. See Section 7 for what happens when you choose to attach any of this to a support email.

Local data does not leave your device unless you enable a third-party cloud feature at your own risk and under your own responsibility — except for three automatic network calls: license validation to Lemon Squeezy (Section 3 Category A, required to keep your activation valid), the update check to Cloudflare (Section 4, which you can turn off at any time in the app), and the speech-model download from models.voicewink.app, also served by Cloudflare — or, only if that endpoint cannot deliver the file correctly, from Hugging Face (next paragraph; only when a selected model is not yet on your device).

**Local speech-model downloads.** Local transcription uses locally stored speech-model files. When the model you selected in the app is not yet on your device, VoiceWink downloads the model's files over an encrypted connection from **models.voicewink.app**, DV Studio's model-download endpoint. **Cloudflare** hosts those files on its R2 object storage and serves them via its global edge network on DV Studio's behalf (Section 3, Category A — the same processor that operates the update endpoint in Section 4). Each request discloses your IP address, standard HTTP request metadata, and the name of the requested file — never audio, transcripts, or keys. Only if models.voicewink.app cannot deliver the file correctly — it is unreachable, or what it serves fails the integrity check — does VoiceWink automatically retry the download from **Hugging Face** (huggingface.co), the public repository the files are mirrored from; such a fallback request discloses the same data categories, and Hugging Face processes it under its own privacy policy, for which DV Studio bears no responsibility whatsoever. The downloaded files are stored locally under %LOCALAPPDATA%/VoiceWink/Models and each file's integrity is verified after download.

**Special-category data.** VoiceWink does not use your voice to identify you — it transcribes speech to text and creates no voiceprint — so it does not process biometric data for the purpose of identification under Article 9 GDPR. We do not intentionally collect special-category data (such as data revealing health, religion, or political opinions). Because you control what you dictate, your audio or transcripts could incidentally contain such content; that content is processed locally on your device, or — if you enable a cloud feature — sent only to the third-party provider you selected under your own account, and DV Studio neither receives nor stores it. The selected third-party provider processes personal data under its own privacy policy for which DV Studio bears no responsibility whatsoever.

**Data from third parties.** The one third-party source we receive personal data from is **Lemon Squeezy**: when you buy or activate a key, Lemon Squeezy makes the order and license data available to DV Studio (buyer name, email address, country, order history, and issued license keys — card and payment data stay with Lemon Squeezy and Stripe; see Sections 3 and 5). Beyond that, the only data we hold is what your device sends as described in this policy, or what you send us directly (for example, by email).

## 3. Third parties that may receive data

VoiceWink integrates with three categories of third parties. The category determines who is responsible for the data and what contractual instruments apply.

### Category A — third-party providers that interact with VoiceWink

For the four processing operations in this category — license validation, opt-in crash reporting, the update check, and the speech-model download — DV Studio determines the purposes and means and is the controller; the providers process the data to deliver their service to DV Studio. The speech-model download's fallback is the one exception to that provider framing: where that download falls back to Hugging Face because DV Studio's endpoint cannot deliver the file correctly, Hugging Face acts as an independent third party under its own privacy policy — not as a Category A provider — as described in the model-download paragraph in Section 2. Where a provider additionally processes data for its **own** purposes — most notably Lemon Squeezy as merchant of record for its checkout, billing, tax, and fraud-prevention obligations — it does so under its own privacy policy and its own responsibility. DV Studio does not bear any responsibility with regard to the latter processing activities.

- **Lemon Squeezy** (Stripe group) — billing, license-key issuance, activation/refund handling.

**Automatic license-validation egress.** To enforce per-license activation limits, VoiceWink contacts Lemon Squeezy automatically in three situations, independent of the cloud-feature toggles in Categories B and C:

- **On activation** (when you first paste a license key), VoiceWink sends your license key plus an *instance name* — a random, stable label generated on your device (for example VoiceWink-3f9a2c71). The label contains no hostname, account name, or other personal identifier. Lemon Squeezy responds with an *instance identifier* that is stored locally.
- **On periodic re-validation**, VoiceWink sends your license key plus the instance identifier so Lemon Squeezy can confirm the license is still active and the device is still authorized. While a license key is stored on this device and matches it, VoiceWink re-validates that key on each application start after setup is complete, and about once a day while VoiceWink keeps running. You can also request a re-validation with "Check now"; the License page may re-validate it when its cached state is older than about 24 hours. A successful result is cached for about 24 hours. When Lemon Squeezy cannot be reached, VoiceWink uses its locally stored license state; depending on that state, a prior successful validation can keep VoiceWink working for up to 30 days.
- **On deactivation** (when you remove the license from this device), VoiceWink sends the license key plus the instance identifier so Lemon Squeezy can release the activation slot. (A refunded or disabled key is detected through the re-validation response above; no separate call is made for it.)

The instance name is a random device label, not your hostname or any other personal identifier, so these calls do not transmit information that identifies you personally beyond the license key itself. The license key and instance identifier leave your device only via these license-validation calls and are held by Lemon Squeezy under its merchant-of-record retention policy. Legal basis: **performance of a contract** (Article 6(1)(b) GDPR) — without these calls the paid license cannot be enforced. For the processing Lemon Squeezy performs for its own purposes as merchant of record — its checkout, billing, tax, fraud-prevention, and retention obligations — DV Studio does not bear any responsibility; that processing is governed by Lemon Squeezy's own privacy policy.

- **Sentry** — optional crash reporting (only if you have explicitly opted in; transcriptions, API keys, license keys, and machine hostname are never sent). Sentry ingests and stores crash reports on DV Studio's behalf in its EU (Frankfurt) region; see the transfers paragraph below. For any processing Sentry performs for its own purposes under its own privacy policy — outside DV Studio's instructions — DV Studio does not bear any responsibility.
- **Cloudflare** — operator of the updates.voicewink.app update-check endpoint described in Section 4 and of the models.voicewink.app speech-model download endpoint described in Section 2. Cloudflare hosts the static update manifest and the speech-model files on its R2 object storage and serves them via its global edge network on DV Studio's behalf. Where Cloudflare processes personal data on behalf of DV Studio, DV Studio has entered into a data-processing agreement with Cloudflare under which this processor provides adequate guarantees that the processing of your personal data complies with the requirements of the GDPR, national regulations and this Privacy Policy in order to protect your rights.

**International transfers (Category A).** Some Category A providers process personal data outside the European Economic Area (EEA), mainly in the United States. Those transfers are protected by European Commission–approved safeguards: certification under the EU–US Data Privacy Framework (DPF) where the provider holds it, and in any event the Commission's Standard Contractual Clauses (SCCs) incorporated in the provider's data-processing terms. **Sentry** ingests and stores crash reports in its EU (Frankfurt) region; to the extent Sentry, as a US-headquartered provider, accesses that data from outside the EEA, the same safeguards apply. You can request a copy of, or details about, the safeguards applying to a specific provider by writing to the contact in Section 1.

### Category B — Bring-your-own-key (BYOK) cloud providers you select per session

These providers are reachable from VoiceWink only if **you** supply your own API key and **you** choose which provider to use for a given feature. DV Studio chose the integration list and the request shape, but every actual data transfer happens under your account at the provider you have chosen, governed by your own terms with that provider. DV Studio bears no responsibility whatsoever.

- **Cloud transcription:** Groq, Deepgram, ElevenLabs, OpenAI.
- **AI enhancement (text post-processing):** Anthropic, OpenAI, Google (Gemini), Groq, Mistral, OpenRouter, Cerebras.
- **Image generation:** OpenAI, Google (Gemini), OpenRouter.

When you use a category-B feature, the audio or text needed for that feature is sent over an encrypted connection from your device directly to the provider you selected. Your API key for that provider is transmitted with the request — to that provider only, to authenticate it — and is never sent to DV Studio. Each provider operates under its own privacy policy for which DV Studio bears no responsibility whatsoever. Most operate outside the EU (United States); transfers are governed by your terms with the provider, including any standard contractual clauses or equivalent safeguards.

**Dictionary and trigger words.** Where the provider supports it, VoiceWink also sends the words from your custom Dictionary, and your prompt trigger words, with each transcription request so that the provider recognizes them more reliably. This applies to **Deepgram**, **ElevenLabs** and **OpenAI** (the latter only on models that accept the field), each behind its own toggle on the Models page. These words are your own content and often proper names; they go only to the provider you selected, under the same BYOK framing as the audio itself, and never to DV Studio.

### Category C — User-supplied endpoints

VoiceWink supports configuring a custom OpenAI-compatible base URL — for example a self-hosted Ollama server, a private gateway, or any other endpoint that speaks the OpenAI Chat Completions API. When you point VoiceWink at such an endpoint, DV Studio has neither a relationship with the operator of that endpoint nor knowledge of where the data ultimately lands. The endpoint is fully under your control and your responsibility.

### If you do not enable any cloud feature

No audio, text, or transcribed content leaves your device — with three automatic exceptions: the license-validation egress to third-party Lemon Squeezy in Section 3 Category A (cannot be disabled while a paid license is active; required for the license to remain valid), the update-check egress in Section 4 (which you can turn off at any time in the app), and the speech-model download described in Section 2 (which happens only when a local model you selected is missing). None of the three ever carries audio, text, or transcribed content.

## 4. Automatic update check

VoiceWink contacts updates.voicewink.app to check whether a newer version of the Software is available. By default this check fires shortly after the application starts and then repeats approximately every 24 hours while the application is running. When the automatic-installation setting is on (the default), an update found by the check may be downloaded and installed automatically; the download reveals the same data categories as the check itself (IP address, User-Agent, channel, version — nothing new). You can turn off automatic update checking and automatic update installation at any time in the app.

### 4.1 What is sent

Each update check is a single HTTPS GET to `https://updates.voicewink.app/<channel>/releases.<channel>.json`. The request carries:

- Your **IP address** (visible to Cloudflare as the operator of the endpoint, as for any HTTPS request).
- The **HTTP User-Agent** identifying the Velopack update library and its version (e.g. Velopack/0.0.1298).
- The **release channel** the Software is built against (e.g. win-x64-stable), included as part of the request URL.
- The **currently-installed version** of the Software, included as a query string or as part of the Velopack library's request shape (used by the Software itself to decide whether the manifest reports an upgrade — it is not separately persisted by DV Studio).

No transcribed text, audio, API keys, license keys, or local identifiers other than the four items above are sent.

### 4.2 Who receives it

The endpoint is served by **Cloudflare** from a Cloudflare R2 bucket controlled by DV Studio. Cloudflare's default request logs (which may include source IP, timestamp, request path, and basic HTTP metadata) are governed by Cloudflare's privacy policy and retained per Cloudflare's defaults. DV Studio does **not** independently log update requests for analytics purposes; the manifest is a static object served by R2.

### 4.3 Legal basis

Update checking relies on **Article 6(1)(b) and (f) GDPR** — performance of the contract, because the updates it finds are included with your activation key (see the EULA, section 12.2), and **legitimate interests**, balancing your interest in receiving security and bug-fix updates against the minimal data collected. The data minimization is structural: no app-level analytics, no separately-retained logs, and the user-facing automatic-update-check setting is honored before the network call is made.

### 4.4 Your control

The app provides a setting that turns automatic update checking off; when it is off, the automatic check never fires. That choice is offered during first-run setup, and both automatic update checking and automatic update installation can be turned off at any time. A manual check remains available and works as a one-shot request — using it is itself a deliberate, user-initiated request for that single check.

## 5. Website visitors

This section covers visits to the product website at **voicewink.app** and to the publisher website at **dvstudio.app**. Both are served on the same hosting described below; dvstudio.app is a small publisher page that links to voicewink.app rather than redirecting to it. The rest of this policy describes the VoiceWink application itself.

- **Hosting and security logs.** The websites are served through Cloudflare's edge network. Like any web host, Cloudflare processes connection data (IP address, user agent, requested URL, timestamps) to deliver the sites and to detect and block abuse. DV Studio does not build visitor profiles from these logs. Legal basis: **legitimate interests** (Article 6(1)(f) GDPR) in operating and securing the websites.
- **Strictly necessary cookies.** Cloudflare may set a small number of strictly necessary cookies for security and bot mitigation. These are exempt from consent; the website's Cookie Notice (at voicewink.app/cookies) describes the cookies in use.
- **Web analytics (cookieless).** The website uses Cloudflare Web Analytics, a privacy-first measurement tool that sets no cookies, uses no browser local storage, does not fingerprint visitors, and reports only aggregate, non-identifying statistics derived from short-lived performance-timing readings. Legal basis: **legitimate interests** (Article 6(1)(f) GDPR) in understanding aggregate site usage. The websites do not use advertising or cross-site trackers.
- **Buying a license.** The Buy flow hands off to a checkout operated by **Lemon Squeezy** acting as merchant of record; the data you enter there (billing details, email address) is governed by Lemon Squeezy's own privacy policy, and as merchant of record Lemon Squeezy is responsible in its own right for the billing, tax, and fraud-prevention data it collects at its checkout. What DV Studio then receives is the order and license data described in Section 3 (Category A).
- **Transfers.** Cloudflare's processing is covered by the transfer safeguards described in Section 3 (Category A).

## 6. Legal bases

We rely on the following legal bases under Article 6 GDPR:

- **Performance of a contract** (Article 6(1)(b)) for activation, the periodic license-validation egress described in Section 3 Category A, deactivation, and processing of paid features and the automatic update check.
- **Consent** (Article 6(1)(a)) for crash reporting. (Category B and Category C cloud features are not processing by DV Studio at all: you enable them, the data travels from your device directly to the provider you selected under your own account and terms, and DV Studio therefore asserts no legal basis of its own for it — see Section 3.)
- **Legitimate interest** (Article 6(1)(f)) for security logging, fraud-prevention measures attached to license activation, the automatic update check described in Section 4, the download of speech-model files you have selected (Section 2), and the website hosting and analytics described in Section 5.

You can withdraw consent at any time by turning the relevant feature off in the app.

We do not sell or share your personal information, and we do not use it for advertising or behavioral profiling.

## 7. Retention

Local data (transcripts, settings, history) remains on your device until you delete it. **Uninstalling the Software leaves your local data in place by default.** You can remove it at any time with the data-management controls inside the app (see section 9), or by deleting the %LOCALAPPDATA%/VoiceWink folder.

The local items with their own shorter lifetimes are: a recording kept after a failed transcription, or after VoiceWink detected no speech in it (both removed when you retry, start a new recording, or close the app, with a 7-day sweep as backstop); recordings kept by the keep-recordings debugging option, unsent problem-report bundles, and prompt-trace logs (all swept after 7 days); and reference-image copies (kept while any History entry references them, then erased with the last of those entries). The memory-only capture buffer described in Section 2 is never written to disk at all.

Cloud-provider retention follows each provider's policy. Most providers offer zero-retention or short-retention modes; we encourage you to review the privacy policy of each provider you enable.

Lemon Squeezy and Stripe retain billing records as required by financial-services law.

Sentry retains crash reports for up to 90 days by default.

Cloudflare's logs for the update and speech-model endpoints follow Cloudflare's standard retention; DV Studio does not separately retain update-check or model-download records. A fallback model-download request to Hugging Face is governed by Hugging Face's own privacy policy (Section 2).

**Support correspondence.** Email exchanged with support@dvstudio.app (including any logs, screenshots, or diagnostic bundles you choose to attach) is retained for **90 days** by default after the conversation has closed, and longer only where the exchange relates to an open support ticket, a billing dispute, a security incident, or a legal/regulatory inquiry that requires us to keep the record. We will delete or anonymize support correspondence on request via the rights process in section 9.

A report you build with "Report a problem" is prepared locally and attached to an email **you** send from your own mail client — the app never transmits it. By default the report is redacted: API keys and license keys are removed, and prompt content appears only as summaries with the text replaced by its length. The dialog additionally offers, each behind its own checkbox and advisory, **raw prompt logs** (your dictated text and AI prompts and results verbatim, including any Dictionary words) and **your audio recordings**. Both are your own content, included only by your explicit choice for that one report. Standard application logs can also contain provider response content.

## 8. How we protect your data

We apply technical and organizational measures appropriate to the risk (Article 32 GDPR):

- **API keys and license keys** are encrypted at rest on your device with the Windows Data Protection API (DPAPI), readable only by your Windows user account on that device.
- **Network calls** (license validation, optional cloud features, the update check, the speech-model download) use encrypted (TLS) connections.
- **Crash reports** are scrubbed on your device before transmission: a redaction step removes API keys, license keys, authorization headers, transcription text, the machine hostname, and provider response bodies before any event is sent to Sentry, and crash reporting is off unless you opt in.
- **Source transparency.** The Software is open source; its data-handling code can be independently inspected at the repository linked in the EULA section 2.1.

## 9. Your rights

Under Articles 15 to 22 GDPR you may:

- Ask for a copy of your personal data ("right of access").
- Have inaccurate data corrected.
- Have your data erased.
- Restrict or object to processing under the conditions provided in Articles 18 and 21 GDPR.
- Receive your data in a portable format.

You can exercise local-data rights directly inside the app: its settings include data-management controls that let you **export your data** (a portable copy of your settings, history, vocabulary, and word replacements), **delete your history**, or **delete all app data**. For anything the in-app controls do not cover — including data held by DV Studio, such as support correspondence — contact us at support@dvstudio.app and we will action your request. We aim to respond within one month, as required by Article 12 GDPR; if a request is particularly complex we may extend this and will tell you why.

We do not carry out automated decision-making that produces legal or similarly significant effects concerning you (Article 22 GDPR). The fraud-prevention measures attached to license activation are rule-based limits on activation counts, not automated profiling.

You also have the right to lodge a complaint with the Belgian Data Protection Authority (Gegevensbeschermingsautoriteit / Autorité de protection des données):

Address: Drukpersstraat 35, 1000 Brussels

Email: contact(at)apd-gba.be

Telephone number: +32 (0)2 274 48 00

[www.gegevensbeschermingsautoriteit.be](https://www.gegevensbeschermingsautoriteit.be/)

## 10. Changes to this policy

When this policy changes you will be asked to review and accept the new version before continuing to use the Software. Past acceptances are recorded as content hashes locally so the in-app gate can detect when an update needs your attention.
