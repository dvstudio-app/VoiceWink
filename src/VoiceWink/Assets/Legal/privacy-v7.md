---
version: 7
last-updated: 2026-10-23
title: VoiceWink Privacy Policy
changes: DV Studio now also counts model downloads, and its page beacon on voicewink.app also records clicks on the Microsoft Store and GitHub links. Neither tells DV Studio which downloads or visits are yours.
changes-for: cd2e379eaa76
changes-since: 9a6a93328ef5
---

# VoiceWink Privacy Policy

This policy explains what personal data VoiceWink and its websites process, why, who receives it and how long it is kept. It also explains your rights. It follows the EU General Data Protection Regulation (Regulation 2016/679, "GDPR") and the Belgian law of 30 July 2018 on the protection of natural persons with regard to the processing of personal data.

## In short

- VoiceWink works on your PC. Your recordings, transcripts, history and settings stay on your device unless you turn on a feature that sends them.
- VoiceWink never sends your audio, transcripts or API keys to DV Studio. DV Studio sees them only if you attach them to an email to support yourself.
- On its own, VoiceWink connects to check your license, to check for and install updates, to download the speech and AI models it uses, and to send a few one-time counts, for example when an installation or a free trial starts. Section 3 lists every such connection.
- The counts carry no installation, device or account identifier and no content. Section 4 sets the limits they always stay within.
- Cloud transcription, cloud AI enhancement and image generation are optional. They use your own API key and go straight to the provider you choose, under your own account.
- Crash reports are off unless you turn them on.
- We do not sell your data, and we do not use it for advertising or profiling.
- You can export or delete your data in the app, and you can object to the counts at any time (Section 10).

## 1. Who is responsible

Dieter Verlaeckt, trading as DV Studio · Haverlaan 28, 2500 Lier · Belgium

Enterprise number (KBO/BCE): BE 1037.153.197

Contact for privacy questions: [support@dvstudio.app](mailto:support@dvstudio.app)

DV Studio decides why and how the data in this policy is processed, which makes it the "controller" under the GDPR. Where another company acts on its own responsibility, this policy says so.

## 2. What stays on your device

VoiceWink stores its data under %LOCALAPPDATA%/VoiceWink on your PC. DV Studio does not upload any of it. VoiceWink is open source, so anyone can check how it handles data (see the EULA, section 2.1).

- **Recordings.** Audio is written to a temporary file and deleted once it has been transcribed. If transcription fails or no speech is detected, the recording is kept so you can retry, until you retry, start a new recording or close the app. If you turn on the option to keep recordings for debugging (off by default), recordings are kept instead. While VoiceWink is open, a clean-up removes recordings older than 7 days, unless one is still kept for a retry. VoiceWink does not use your voice to identify you and creates no voiceprint.
- **The microphone between recordings.** With "Instant recording" on (the default), VoiceWink keeps the microphone open while it runs, so that recording starts the moment you press the hotkey. That audio is kept only in memory, holds at most about the last 10 seconds and is constantly overwritten. It is never saved to disk or sent. The start point is approximate, so a recording can include a brief moment from just before the press. Windows shows its microphone indicator while the app runs. Turning the setting off releases the microphone between recordings.
- **History.** Transcribed text is stored in a local database, only if History is on. Images you generate are placed on your clipboard, and saved only if History is on. With History on, VoiceWink also keeps a local copy of each reference image you attach, for as long as a History entry uses it.
- **What you dictate.** What you dictate can include personal or sensitive information about you or other people, such as health, money or private matters. VoiceWink uses it only to do what you ask. It is processed on your PC, or by the provider or server you chose, and DV Studio does not receive it unless you send it to support yourself.
- **Settings and your own content.** Your settings, dictionary, word replacements, prompts, App Mode rules and, if you set one, the address of your own AI server.
- **Usage totals.** Word counts, time saved and days of use, shown on the Metrics page. They are kept even when History is off.
- **Keys.** API keys and your license key are encrypted with the Windows Data Protection API (DPAPI), so only your Windows user account on this PC can read them.
- **License data.** The state of your license and trial. To keep an activation on the PC it was made for, VoiceWink also stores one-way fingerprints (hashes) of three hardware identifiers: the Windows machine ID, the computer's hardware ID and the network card's address. The fingerprints are used only on your PC and are never sent.
- **Logs.** App logs help diagnose problems. VoiceWink keeps the logs of the last 14 days it ran. Sensitive text is not removed from them, and they can contain responses from cloud providers and the names of other programs, such as the app you dictate into. A short summary of each AI request, without its text, is kept for 7 days. If you switch on the optional prompt-trace logging (off by default), the dictated text and AI prompts are additionally written to local log files, which are swept after 7 days.
- **Problem reports.** A report you build with "Report a problem" is saved locally and removed after 7 days if you never send it.
- **Downloaded models**, and small files that record how fast they run on your PC.
- **The clipboard.** To paste text, VoiceWink puts it on the Windows clipboard. The text stays there until something else is copied, unless you turn on "Restore clipboard" in Settings, which puts back what was there before. VoiceWink marks the text it pastes, and the text it copies for you automatically, so that Windows clipboard history and cloud clipboard do not keep or sync it. Generated images are placed on the clipboard for you to paste; if you use Windows clipboard history or cloud clipboard, Windows may keep or sync those under Microsoft's own terms.

**Deleting your data.** In the app you can delete your history, or delete all app data. "Delete history" moves saved images to the Recycle Bin. "Delete all app data" permanently deletes VoiceWink's settings, history database, recordings, images, reference copies, downloaded models, app logs, problem reports and unsent crash reports. It leaves other files in that folder, such as the program itself, and does not empty the Recycle Bin. Uninstalling VoiceWink leaves your data in place. To remove it, delete the %LOCALAPPDATA%/VoiceWink folder. A small record that the free trial was used stays on this PC even after deletion and uninstalling, so the trial can't be started again.

## 3. Connections VoiceWink makes on its own

This is the complete list of connections VoiceWink and its installer make on their own, without you turning on an optional feature. All of them use encrypted connections. None of them carries audio, transcripts or other content you create.

- **License check, to Lemon Squeezy.** When you enter a license key, VoiceWink sends the key and a random label for this PC (for example VoiceWink-3f9a2c71, with no hostname or account name). Lemon Squeezy answers with an identifier for this activation. While a license key is stored on this device and matches it, VoiceWink re-validates that key on each application start after setup is complete, and about once a day while VoiceWink keeps running. Each check sends the key and, once there is one, that identifier. VoiceWink also checks when you press "Check now", and may check when you open the License page. When you remove the license, or when an activation fails, VoiceWink sends the key and the identifier to release the activation. Without a connection, a recent successful check keeps VoiceWink working for up to 30 days. This check is needed to provide the license you bought and cannot be turned off while a paid license is active.
- **Update check and updates, to updates.voicewink.app.** Shortly after start and then about once a day, VoiceWink asks whether a newer version exists. The request carries your IP address and technical details of the installed copy: its update channel, its version, and the Windows and processor type. When automatic installation is on (the default), updates are downloaded and installed automatically. You can turn off automatic update checking and automatic update installation at any time in the app. You can still check by hand.
- **Model downloads, to models.voicewink.app.** When VoiceWink needs a speech or AI model that is not yet on your PC, it downloads the model's files. Each request carries your IP address and the name of the file. For speech models only, if that server cannot deliver a file correctly, VoiceWink downloads it from Hugging Face (huggingface.co) instead. Hugging Face handles that request under its own privacy policy and its own responsibility. DV Studio counts these requests as Section 4 describes.
- **One-time counts, to updates.voicewink.app.** A few short reports that let DV Studio count installations and free-trial starts. Section 4 explains what they can contain, when they are sent and how you can stop them.

## 4. How we count downloads, installs and free trials

DV Studio counts the requests that show how often VoiceWink is downloaded, installed, updated and tried. The counts come from requests to updates.voicewink.app: update checks, downloads of the installer from our website or the Microsoft Store, and short one-time reports the installer and the app send, and model downloads from models.voicewink.app. This section sets the limits that every count stays within.

**What a count can use.**

- Your IP address, as Cloudflare sees it on any request. DV Studio does not store it.
- The name that the program or browser sends with the request (its "User-Agent"): VoiceWink's or its update library's for the app and installer, or your browser's for an installer download. It is read to sort the traffic and is not stored.
- The update channel, the name of the report or file, and the technical details of the installed copy described in Section 3.
- What Cloudflare works out from the request: the approximate country, whether the request looks automated, and whether the network belongs to Microsoft, which checks every Microsoft Store submission.
- The time of the request, the part of a file requested for a download, and how the server answered, such as the status and size of the response.

**What a count never contains.** No installation, device or account identifier, no license key, no name or email address, and no audio, transcript, dictionary, setting or API key. So DV Studio has nothing that links one count to another, or a count to you.

**What the counts are for.** To count the requests and reports received per day or hour, channel and country, and how often each model is downloaded, and to estimate how much of that traffic is automated, comes from networks operated by Microsoft, or is DV Studio's own testing. The counts show requests received, not completed downloads, installations or trials.

**When the one-time reports are sent.**

- **By the installer of a copy built for the Microsoft Store, while it installs VoiceWink.** This happens before VoiceWink first starts, so before you can turn off automatic update checks. It is skipped when the installer finds earlier VoiceWink settings on your PC in which automatic update checking is off, or settings it cannot read.
- **By a copy built for our website, once you accept this policy.** The app sends its installation report once, right after you accept this privacy policy in first-run setup. It is not sent if you do not accept.
- A report shows which channel a copy was built for (the website's or the Microsoft Store's), not where you got it.
- **By the app, after an automatic update check.** Once after a free trial starts, and once from a copy built for the Microsoft Store. These reports are never sent while automatic update checking is off, and a manual check never sends them.
- Earlier versions of VoiceWink may decide whether to send the Microsoft Store report by looking, on your PC, for the Store's record of installing VoiceWink in Windows' event log. That record is never sent.
- A report is meant to be sent once. A reinstallation, a failed delivery or deleted settings can send it again.

**What would change this policy.** A new count does not need a new version of this policy as long as everything in this section, and the recipients, legal basis, retention and rights in Sections 7 to 10, stay true for it. Anything else would. Examples are a new kind of data, any identifier, a new purpose, a report sent at a new moment or more often, a new recipient or a longer retention. We would publish the new version on voicewink.app before the change takes effect, and the app would ask you to review it.

## 5. Features you turn on

**Cloud features with your own API key.** These run only when you add your own key and choose a provider:

- Cloud transcription: Groq, Deepgram, ElevenLabs, OpenAI.
- AI enhancement: Anthropic, OpenAI, Google (Gemini), Groq, Mistral, OpenRouter, Cerebras.
- Image generation: OpenAI, Google (Gemini), OpenRouter.

When you use one, the audio, text or images it needs, and your API key, go from your PC straight to that provider, never to DV Studio. For AI enhancement this includes your prompt and, if the Dictionary is on, your Dictionary words, so that names are spelled right. VoiceWink also uses your key to check it and to list the provider's models. For Deepgram, ElevenLabs and OpenAI, VoiceWink also sends your Dictionary words and trigger words with transcription requests, so that the provider recognizes them. Each has a switch on the Models page, on by default. When a provider returns an image as a link, VoiceWink downloads it from the address the provider gives. The provider handles this data under your account, your terms with it and its own privacy policy. It is responsible for it; DV Studio is not. Most providers are in the United States.

**Your own AI server.** You can run AI enhancement on a server you choose: a local server such as Ollama or LM Studio, or any other server that works the same way, set with a custom base URL. VoiceWink then sends the text, your prompt, your Dictionary words if the Dictionary is on, the model name and any key you set to that address. DV Studio has no relationship with whoever runs that server and does not know where the data goes. The server is your choice and your responsibility.

**Crash reports.**

- **Crash reports (opt-in).** Sent to Sentry only if you have explicitly enabled crash reporting. Stack traces, app version, OS version, your graphics adapter models and display driver versions, and redacted log breadcrumbs are sent. Transcriptions, API keys, license keys, and your machine hostname are never sent.

While crash reporting is on, Sentry also receives errors that did not crash the app, technical details of your PC (processor type, Windows and runtime version, language and memory), and a notice when the app starts and stops. Before anything is sent, VoiceWink removes known sensitive fields, such as keys, authorization headers and transcribed text. To link reports and sessions from the same PC, Sentry's software also sends an identifier. It is normally a random value saved on your PC. If that value cannot be saved, the software can use your network card's address instead, or a one-way hash of your PC's name. Sentry stores the reports in the EU (Frankfurt) on DV Studio's behalf.

**Support email and problem reports.** When you email support@dvstudio.app, we receive what you write and attach. A problem report is attached to an email you send yourself; the app never sends it. By default it is redacted: keys are removed and prompts appear only as their length. You can choose to add raw prompt logs (your dictated text, prompts and results) and your recordings, each behind its own checkbox. App logs in a report can contain provider responses.

## 6. Our websites

This covers voicewink.app and the publisher site dvstudio.app.

- **Hosting and security.** Cloudflare serves both sites. It processes connection data (IP address, browser, requested page, time) to deliver them and to block abuse.
- **Cookies.** Cloudflare may set a few strictly necessary security cookies. The Cookie Notice at voicewink.app/cookies lists them.
- **Cloudflare Web Analytics** (both sites). Cookieless page statistics. It does not fingerprint visitors, and Cloudflare discards your IP address.
- **DV Studio's page beacon** (voicewink.app only). A small script records which pages are opened and how long each is visible, clicks on the Download, Buy, Microsoft Store and GitHub links, and the approximate country. It also records the page you came from, or the tag on a link DV Studio posted (one of a fixed list naming where the link was posted, such as the Microsoft Store listing). It sets no cookie, stores no IP address and writes nothing to your device. To group one day's page views into visits, it makes a code from your IP address, your browser's name, the date and a secret value. The code changes every day, so visits on different days cannot be linked. DV Studio cannot tell which visits are yours.
- **Opting out of the beacon.** Use the switch on the Cookie Notice page, or set vw-notrack to 1 in your browser's local storage for voicewink.app. The choice lasts until your browser clears that site's data; a private window clears it when closed. A browser that blocks local storage for voicewink.app is not counted at all.
- **Buying a license.** The Buy buttons go to a checkout run by Lemon Squeezy as merchant of record. What you enter there is governed by Lemon Squeezy's privacy policy. DV Studio then receives from Lemon Squeezy your order and license details: name, email address, country, order history and license keys. You need to give these details to buy a license. DV Studio uses them to provide and support your license, handle refunds and answer your questions. Card details stay with Lemon Squeezy and its payment provider.

## 7. Who receives data

- **Lemon Squeezy** (license checks, checkout, billing). It processes license checks for DV Studio, and acts on its own responsibility as merchant of record for checkout, billing, tax and fraud prevention.
- **Cloudflare** processes data for DV Studio. It hosts the websites, the update and model servers, and the systems that record and count the requests in Sections 4 and 6.
- **GitHub** processes data for DV Studio. It runs the scheduled jobs that read those records and turn them into daily and hourly totals, and it stores the totals.
- **Google** processes data for DV Studio. It hosts the support@dvstudio.app mailbox (Google Workspace).
- **Sentry** processes data for DV Studio, only if you turn crash reports on.
- **Hugging Face**, on its own responsibility, only for the speech-model fallback in Section 3.
- **The cloud providers and servers you choose** (Section 5), on their own responsibility or yours.

DV Studio has data-processing agreements with the companies that process data for it. Some of them process data outside the European Economic Area, mainly in the United States. Those transfers rely on the EU-US Data Privacy Framework where the company is certified, and in any case on the European Commission's Standard Contractual Clauses. You can ask for a copy of these safeguards at the contact in Section 1.

## 8. Why we may process your data

- **To perform our contract with you** (Article 6(1)(b) GDPR): the license checks, your order and license details from Lemon Squeezy, and the update check and updates included with your license (see the EULA, section 12.2).
- **Legitimate interests** (Article 6(1)(f) GDPR): the update check (alongside the contract), the model downloads, the counts in Section 4, website hosting, security and measurement (Section 6), answering support email, and limits that prevent misuse of license activations. Our interest is to keep VoiceWink working and secure and to understand how it is used. We keep the data minimal, and the counts and website measurement hold nothing that links records to you, so they weigh lightly on you. You can object (Section 10).
- **Consent** (Article 6(1)(a) GDPR): crash reports. You can withdraw it at any time by turning crash reporting off.

Cloud features and your own AI server are covered in Section 5.

We do not make automated decisions that have legal or similarly significant effects on you. The activation limits are simple counts, not profiling.

## 9. How long data is kept

- **On your device:** until you delete it, except the shorter lifetimes and the trial record in Section 2.
- **Records of single requests and visits** (Sections 4 and 6): about 3 months.
- **Totals,** daily and hourly, which hold no record of a single request or visit: as long as VoiceWink is distributed or the websites run.
- **Cloudflare's own logs** for the websites and servers: Cloudflare's standard retention.
- **Crash reports:** up to 90 days at Sentry.
- **Support email:** 90 days after the conversation ends. Longer only for an open ticket, a billing dispute, a security incident or a legal request.
- **Order and license details received from Lemon Squeezy:** while your license exists, then as long as needed for refunds and disputes.
- **Billing records at Lemon Squeezy and its payment provider:** as long as financial law requires.
- **Cloud providers:** under each provider's own policy.

## 10. Your rights

You can ask for a copy of your data, have it corrected or erased, restrict its processing, and receive it in a portable format (Articles 15 to 20 GDPR).

In the app you can export your data (settings, history, vocabulary and word replacements) and delete it (Section 2). For data DV Studio holds, such as support email, write to support@dvstudio.app. We answer within one month, as Article 12 GDPR requires, and tell you if a complex request needs longer.

The counts in Section 4 and the beacon records in Section 6 contain nothing that tells DV Studio which records are yours. So we can apply an access or erasure request to them only if you give us information that identifies them (Article 11 GDPR).

**Your right to object.** You can object at any time to processing based on our legitimate interests (Article 21 GDPR), including the counts in Section 4 and the website measurement in Section 6. Write to the contact in Section 1. To stop the reports that follow an automatic update check yourself, turn off automatic update checking. To stop the beacon, use the opt-out in Section 6.

You can also complain to the Belgian Data Protection Authority (Gegevensbeschermingsautoriteit / Autorité de protection des données):

Address: Drukpersstraat 35, 1000 Brussels

Email: contact(at)apd-gba.be

Telephone number: +32 (0)2 274 48 00

[www.gegevensbeschermingsautoriteit.be](https://www.gegevensbeschermingsautoriteit.be/)

## 11. Changes to this policy

When this policy changes, VoiceWink asks you to review and accept the new version before you continue. It remembers on your PC which version you last accepted. Section 4 says which changes to how we count need a new version.
