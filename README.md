# OpenRewind

**Rewind your Mac or PC.** OpenRewind records your screen and audio, pulls the text
out, and lets you search or scroll back through everything you saw and heard.
100% local. Open source. A small Swift app on the Mac, a native .NET app on
[Windows](windows/README.md).

OpenRewind was called openscreen until October 2026. On first launch it moves
`~/.openscreen` to `~/.openrewind` and keeps every recording and your menu settings.
macOS treats it as a new app, so it asks for Screen Recording, Microphone and
Accessibility again; you can then delete the old OpenScreen.app (and its Login Item).

## The story

### 2022: a search engine for your life

On November 1, 2022, [Rewind](https://techcrunch.com/2022/11/01/rewind-wants-to-revamp-how-you-remember-with-millions-from-a16z/)
launched with **$10M from Andreessen Horowitz**, at a $75M valuation.
The founders were Dan Siroker (co-founder of Optimizely) and Brett Bejcek.

The idea was simple and a little wild. Your Mac records everything on your
screen and everything said in your meetings. Then you can search any word and
jump back to that moment. It all ran on your own Mac, made possible by Apple
Silicon. Rewind promised it would "never sell your data or do advertising."

### 2023: the hottest pitch on Twitter

In April 2023, Rewind raised its Series A in public. Siroker posted the pitch
deck and asked investors to send offers. [More than a thousand investors showed
interest and 170 made offers](https://fortune.com/2023/05/26/a-i-startup-founder-trend/).
NEA led the round at a **$350M valuation**.

### 2024: the pivot away from the Mac

In April 2024, Rewind [renamed itself Limitless](https://techcrunch.com/2024/04/17/a16z-backed-rewind-pivots-to-build-ai-powered-pendant-to-record-your-conversations)
and moved to a $99 AI pendant that records your conversations. The Mac app
[stopped getting updates](https://sacra.com/research/why-meta-bought-limitless/).

### 2025: lights out

On December 5, 2025, [Meta bought Limitless](https://9to5mac.com/2025/12/05/rewind-limitless-meta-acquisition/).
The team moved to Meta's wearables group. The Rewind Mac app was shut off:
**on December 19, 2025, all screen and audio capture stopped.**
Users in the EU, UK, and some other countries had two weeks to export their
data before it was deleted.

### Now: OpenRewind

A tool that remembers your whole digital life should not be able to vanish
when a company is sold. So we built it again, in the open:

- **Local only.** Your data stays in `~/.openrewind`. No account. No cloud.
- **Open source ([MIT](LICENSE)).** Read the code. Change it. Nobody can switch it off.
- **Plain files.** SQLite and images. Your history is yours to keep or move.

### How it was built

OpenRewind is new code. It does not contain any Rewind code. The idea and the
full-screen timeline (⌥⌘T) are modeled on Rewind's.

OpenRewind is not made by or connected to Rewind AI, Limitless, or Meta.

## What it does (v0.1)

- Takes a screenshot every 2 s (ScreenCaptureKit).
- Skips frames that did not change. **Settings → Save Frames While Typing** can
  save fewer frames (every 5, 10 or 30 s) while only a few words change; the
  default is every 2 s, and the screen you stop on is always saved.
- Reads text from the focused window's **accessibility tree**. Falls back to
  **Apple Vision OCR**. Never reads password fields.
- Saves a HEIC (about half the size of a JPEG; JPEG on Macs that cannot encode
  HEIC) and a row in SQLite with an **FTS5** index.
- Can serve a local API + search page at `http://127.0.0.1:3031` (off in the app until you turn it on).
- **Blocked apps** are blurred in every screenshot, wherever they are on screen, and their text
  is never saved. First launch asks what to block (password managers, money apps, private
  browsing windows and bank sites are ticked); change it in **Settings → Blocked Apps & Sites**.
  `OPENREWIND_EXCLUDE` adds more app names from the terminal.
- Records **microphone** (AVAudioEngine) and **system audio** (ScreenCaptureKit)
  in 30 s chunks, 16 kHz mono WAV. Silent chunks are dropped.
- Transcribes each chunk on your computer with **local Whisper** (`whisper-cli` from
  whisper.cpp) or **NVIDIA Parakeet v3**, your pick in **Settings → Transcription Model**
  (see [Choosing a transcription model](#choosing-a-transcription-model)). Transcripts go into
  their own FTS5 index and show up in the same search as screen text.
- **Ask AI** (menu bar or tray → Ask AI…) answers questions like "what did I work on today?"
  with your own key: Anthropic, OpenAI, Gemini, OpenRouter (any model it offers), or any
  OpenAI-compatible server (Ollama, LM Studio). See [Ask AI](#ask-ai).
- **Search by Meaning** (off until you turn it on) finds moments by what they were about, not
  only the exact words. It runs on your computer. See [Search by Meaning](#search-by-meaning).
- **Journal** (menu bar or tray → Journal…): one page per day with your top apps, sites and
  windows, worked out on your computer, plus an optional AI recap. See [Journal](#journal).
- **Meetings** (menu bar or tray → Meetings…) lists your Zoom, Teams, Meet, Webex, FaceTime and
  Slack huddle calls with their transcript, and can summarize them with your AI key. Within seconds
  of joining a call, a small popup asks whether to take notes for it. See [Meetings](#meetings).
- **Insights** (menu bar or tray → Insights…) shows where your time went today or this week,
  worked out on your computer without AI. See [Insights](#insights).
- **Claude, Cursor and other AI apps** can search your history too, through MCP, once you turn
  it on. See [Use with Claude and other AI apps (MCP)](#use-with-claude-and-other-ai-apps-mcp).

## Search by Meaning

Keyword search finds the words you type. Search by Meaning also finds moments that are *about*
the same thing in other words: "the flight I was booking" finds the airline page, "when we talked
about hiring" finds the call where someone said "we need another engineer".

Turn it on in **Settings → Search by Meaning**. OpenRewind then builds an index in the
background: it goes through your old recordings first and then keeps up with new ones. It works
slowly while you use the computer, slower still on battery (or in Low Power Mode on a Mac), and
faster when you are away. The line under the switch shows how far it has got.

Once it is on, the timeline's search box has a **Keyword | Meaning** switch. Meaning shows the
closest moments first; click one to go there, as with keyword search. The browser search page and
Ask AI (`search_by_meaning`) can use it too, and so can AI apps connected through MCP.

How it works:

- A "moment" is a stretch of one window (until the app, window or page changes, up to 10 minutes),
  or one 30-second audio clip. Repeated text in a stretch is counted once.
- Each moment gets a sentence vector made **on your computer**. The Mac uses the sentence model
  that comes with macOS, so nothing is downloaded. Windows downloads one model the first time
  (all-MiniLM-L6-v2, about 90 MB, from Hugging Face), checks it against a fixed SHA-256, and runs
  it with ONNX Runtime. Your recordings are never uploaded.
- The index is stored in the same database. Each moment keeps a copy of its text (up to about
  1,500 characters of screen text or transcript) with its app, window title and web address
  (table `meaning_moments`), and its vector (table `meaning_vectors`): up to about 2 KB per moment.
  So the index holds screen text too, not only numbers, and is deleted with it.
- Blocked apps, sites and windows are never indexed, and are hidden from results if you block
  them later. As with Ask AI, audio is not filtered by app. Apps you unblock are only indexed from
  then on.
- Deleting recordings (by hand or with Keep Recordings For) deletes their vectors too. Turning
  Search by Meaning off deletes the whole index; Windows keeps the downloaded model so turning it
  back on downloads nothing.
- The Mac model and the Windows model are different, so their scores are not comparable. The Mac
  uses your system language when macOS has a sentence model for it, otherwise English.

## Ask AI

Open it from the menu bar or tray (**Ask AI…**) or with the **Ask AI** button in the timeline,
which opens the same chat beside the timeline.

Set it up in **Settings → AI Settings…**: pick a provider, paste your key and pick a model.
Type any model name, or **Refresh List** to ask the provider which models it has, so new
models work without an app update. Keys are kept in the Mac Keychain or the Windows
Credential Manager, never in the settings file or the database.

Requests go straight from your computer to the provider; there is no OpenRewind server.
The model never gets your whole history. It calls lookups that run on your database and
send back capped, trimmed text:

| Lookup | What it sends |
| --- | --- |
| `activity_summary` | Time per app, window and website, and a timeline of what was in front |
| `search_screen` | Matching moments from the screen-text search, with a short snippet |
| `search_audio` | Matching transcript lines (or all of them in a time range) |
| `search_by_meaning` | The moments closest in meaning to a question, with a short snippet. Only offered while **Search by Meaning** is on |
| `get_screen_text` | Screen text from a short time range, sampled and trimmed |
| `view_screenshot` | One screenshot near a moment, as a JPEG. Only offered when you turn on **Let AI look at screenshots** (off by default) |
| `list_meetings` | Meetings in a time range, with their summary, decisions and action items when they have notes (see [Meetings](#meetings)) |

Each lookup shows in the chat with the arguments the AI asked for and exactly what was sent
back. Times in answers link back to that moment in the timeline. Each answer shows the
tokens it used and what they cost; the header shows the chat's total and AI Settings keeps a
running total per provider. Prices are built in for Claude and GPT-6 models and come with
OpenRouter's model list; for other models you can type the price. Lookups leave out every app,
site and window on your block list, including anything saved before you blocked it (the activity
summary still counts a blocked app's time, as "(blocked app)", without its name); audio
transcripts are not filtered by app. Web links in an answer only open if you or a lookup
supplied that address, so text on screen can't trick the AI into a link that sends your data
elsewhere. With a local model (Ollama, LM Studio) nothing leaves your computer at all; a custom
server reached over plain http on another network gets a warning, since your key would travel
unencrypted.

## Journal

Open it from the menu bar or tray (**Journal…**). Each page is one day: ‹ and › go to the day
before or after, the date picker jumps to any day, and **Today** comes back.

Every page has a part worked out on your computer, with no AI and nothing sent anywhere:

- first and last activity and the total active time (a screenshot counts until the next one, at most 2 minutes)
- top apps, top websites and top window or document titles
- when audio was recorded (microphone, system audio or both)

Apps, sites and windows on your block list are never shown: a blocked app is left out of every
list, and a blocked site or window keeps only its app name. Screenshots saved while a blocked window
or site was in front are left out of every list too (their time still counts as active). Click a
time to open that moment in the timeline.

**Recap**: with an AI provider set up in **AI Settings**, **Write Recap** asks your model for a
short recap: what you worked on, who you talked to, and open loops to follow up. It sends that
day's history with your key: app and window names, web addresses, transcripts and some screen text.

- It uses the same lookups as Ask AI, and every lookup (including the ones the model asks for) is
  limited to that day. **What was sent** lists each lookup and exactly what went to the provider.
- Blocked sites and windows are left out, and a blocked app shows only as "(blocked app)" with its
  time. **Audio transcripts are not filtered by app**, so a call heard while a blocked app was open
  is still sent.
- The page shows the tokens and cost. Times in the recap link to that moment in the timeline.
- Recaps are saved in the database (`journal_recaps`), so a day is not written (or paid for) again;
  **Rewrite** replaces it. One written before the day was over says so.
- Each recap remembers the block list it was written with. If you block something new later, older
  recaps are hidden (and left out of exports) until you rewrite them, since they may mention it.

**Settings → Write Each Day's Recap Automatically** (also a switch in the Journal window) is off by
default because it spends tokens. When on, it writes **yesterday's** recap the first time the app is
running on the next day, with the AI provider it was turned on for; after you switch providers in
AI Settings it pauses until you turn it on again.

- Days with nothing recorded are skipped. A day the app is not running the day after (say, a long
  weekend with the computer off) gets no automatic recap; open it and use **Write Recap**.
- A recap you wrote by hand during the day is replaced by one of the whole day.
- If it fails (offline at login, a busy provider), it tries again 10 minutes later, at most 3 times
  for that day.

**Deleting recordings**: deleting a time range by hand (**Delete Recordings → Last Hour**, **Today** …) also
deletes the recaps of the days it touches, and deleting all recordings deletes every recap. **Keep …
Recordings For** deletes a day's recap only once nothing of that day (screen or audio) is left, so
with **Keep For 1 Day** yesterday's recap stays while part of yesterday does.

**Copy as Markdown** and **Save as Markdown…** export the page. In the recap, times become plain
text, any other link or image becomes its text and address in plain text, and HTML is escaped, so
opening the file never loads anything.

## Copy and export

Get things out of the timeline, on the Mac and on Windows:

- **Copy text from the past**: drag across words on any screenshot in the timeline to select
  them, then **⌘C** / **Ctrl+C**. The Mac uses Live Text (VisionKit); Windows reads the shown
  screenshot with Windows OCR. Both run on your computer.
- **Copy Screenshot**: **⌘C** / **Ctrl+C** with no text selected copies the whole screenshot as
  an image. **Save Screenshot…** (**⌘S** / **Ctrl+S**) saves it as a PNG.
- **Export Clip…** (**⌘E** / **Ctrl+E**) turns a stretch of the timeline into an MP4 video or an
  animated GIF. Mark where it starts with **[** and where it ends with **]** (the bar shows the
  marked range; **Esc** clears it), or leave it unmarked to get the 5 minutes before the shown
  screenshot. Pick the format and a speed (1× real time, or 2×, 5×, 10×, 30× faster). Before you
  start, it shows how long the clip will be, how many frames it has, its size in pixels, and
  roughly how big the file will be and how long making it takes. Then pick where to save it.
  It says **Preparing…** at once, then shows a bar with the frame it is on ("Frame 120 of 600"),
  the percentage, the time so far and about how long is left. **Cancel** stops it right away and
  leaves no file. When it is done, **Show in Finder** / **Show in Explorer** and **Open** take you
  to the file.
- The share button at the top of the timeline (or a right-click on the screenshot on Windows) has
  all of these, plus **Copy All Text** and **Mark Clip Start / End Here**.

What is left out:

- Screenshots saved while a blocked app, window or site was in front, and ones whose front app,
  window or site is on your block list **now** (even if saved before you blocked it), are left
  out of clips and can't be copied, saved or read for text. With the app filter on, a clip has
  only that app's screenshots, as the timeline shows them.
- Clips are made from the screenshots already saved, never from a new recording. Blurring of
  other windows happens when a screenshot is taken, using the block list as it was then. So an
  app you block today is still visible, unblurred, in older screenshots where another app was in
  front and it was showing beside it. Check a clip before you share it if you blocked something
  recently.
- Copies go to the system clipboard. On Windows OpenRewind marks them so they stay out of
  clipboard history (Win+V) and cloud clipboard sync. On the Mac, Universal Clipboard can still
  hand a copy to your other Apple devices nearby while Handoff is on; turn Handoff off in System
  Settings → General → AirDrop & Handoff to stop that. The Live Text menu on the Mac has only
  Copy and Select All; web search, translate and look up are taken out.

A screenshot lasts until the next one, at most a minute, so long breaks are skipped. A clip
covers at most 2 hours; videos are at most 1920 pixels wide and GIFs 1280, with at most 600
pictures (fast parts drop pictures to stay under that). MP4 uses H.264 (AVAssetWriter on the
Mac, the encoder built into Windows). GIFs use a small encoder in OpenRewind on both, which
writes as it goes, only stores the part of each picture that changed, and stops at once on
Cancel.

## Meetings

OpenRewind finds your calls in what it already records; nothing extra runs during a call.
A meeting starts as soon as a call is on your screen: a Google Meet meeting (not Meet's start page),
Zoom's meeting window or a Zoom meeting in the browser (not Zoom's home), a Teams call or meeting
window, a Webex meeting, a FaceTime call or a Slack huddle. Another window of these apps (say, Zoom or
Teams with no meeting name in its title) counts only once other people can be heard (system audio) for
a minute. A meeting goes on while its window or page is in front; after you switch away, your
microphone keeps it going for up to 30 minutes (say, while you share slides) and other sound for up
to 15. It ends after 3 minutes with none of these, and calls in the same app less than 10 minutes apart
(a dropped call, a rejoin) become one. Blocked apps, sites and private windows never become meetings.
Without Whisper, audio waiting to be transcribed does not count as sound.

**Take notes?** Within about 10 seconds of a call coming to the front, even before anyone speaks, a
small popup asks "Meeting detected in Google Meet. Take notes?" in the corner of the screen. It does
not take focus from the call, and it stays while you switch to other windows (until a while after
the call ends). It asks only once per call. If you say Take Notes, the call is kept with its transcript from the moment
you joined, even if you spend the call in other windows.

- **Take Notes** keeps the call in Meetings with its transcript and, if summaries are on (below),
  sends it for a summary when it ends.
- **Not This Time** leaves the call out of Meetings and Ask AI, and it is never summarized. Audio is
  still recorded as usual under your recording settings; delete it from **Delete Recordings** if you
  want it gone.
- **Remember for Zoom** answers the same way for that app from then on. **Settings → Forget Remembered
  Meeting Choices** asks again for every app.
- **Settings → Ask Before Taking Meeting Notes** (on by default): turn it off to take notes for every
  call without asking (apps you said Never for are still skipped). If you don't answer, the call is
  listed but not summarized by itself; you can still press **Summarize**.

**Meetings…** in the menu bar or tray lists them by day. Each one shows its transcript, built from the
audio transcripts of that time: lines from your microphone are **Me**, lines from system audio are
**Others** (OpenRewind does not tell people apart). **Open in Timeline** jumps to the start,
**Copy as Markdown** and **Save as Markdown…** export the notes and transcript.

**Summaries are off by default.** They use your Ask AI provider and key, cost tokens, and send the
call's whole transcript to that provider. The transcript is not filtered by app: anything said or
played during the call is sent, even while a blocked app or site was in front. Turn on **Settings →
Summarize Meetings with AI** to have each call you take notes for summarized when it ends (only calls
that end after you turn it on), or press **Summarize** on one meeting. Summaries go only to the
provider you agreed to; if you switch providers in AI Settings, OpenRewind asks again and pauses
summaries until you answer. If Whisper is still catching up when a call ends, OpenRewind waits up to
2 hours, and makes the notes once more if more of the call is transcribed afterwards.

You get a short summary, decisions and action items, with the tokens and cost it used, as in Ask AI.
Notes never contain web links (only links into your timeline), since the AI writes them from what
other people said. Ask AI can read them too: "what did we decide in my 2pm call?" Deleting audio
recordings deletes the meetings in that time, and they are not found again from what is left;
deleting screen recordings clears the window titles they kept.

## Insights

Open it from the menu bar or tray (**Insights…**). Switch between **Day** and **Week**, and use
‹ › (or ← →) for the day or week before or after. It shows:

- **Active time**: how long you used the computer, and the change from the day or week before
  (up to the same time while the day or week is still going).
- **Apps** and **Websites**: time in each, as bars, the top 10 and then "Other", each with the change
  from the day or week before.
- **When you were active**: a strip of the day's hours, or for a week a grid of days by hours.
- **Focus sessions**: the longest stretches of 10 minutes or more mostly in one app (short looks at
  another app, under a minute, don't break one).
- **App switches**: how often the app in front changed while you were using the computer.

Click an app to open the timeline showing only that app, at the first moment you used it that
day or week. Click a website or a focus session to jump to it.

Everything is worked out on your computer from what was saved with each screenshot (time, app,
window title, address); nothing is sent anywhere and no AI is used. The window follows
**Settings → Timeline Look**.

**Blocked apps and sites.** Screenshots saved while a blocked app, window or site was in front are
marked as blocked and left out entirely, as is anything that matches your block list now (by app
name, window title or address). One gap: screenshots saved by an older version while a blocked
*site* or *window* was in front kept only the browser's name, and ones from a helper app of a blocked
app (Docker.app shows "Docker Desktop") kept the helper's name, so that older time can still show
under the browser or the helper app. Block the helper's name too to hide it.

**How time is counted.** Time counts from one screenshot to the next. A break of more than 2
minutes (locked, asleep or paused) does not count. A screen whose text doesn't change only gets one
screenshot a minute; after 5 minutes of that, the rest counts as away until something changes,
unless speech was recorded at the time (a call or a video, with **Record Audio** on), which counts
as being there. With audio recording off, a long call or video can count as away after 5
minutes. If you record from the terminal with `--interval` of 50 seconds or more, every screenshot
looks like a still screen and active time is undercounted; keep the interval under 50 seconds.
Ask AI's activity summary counts every gap up to 2 minutes without the still-screen rule, so its
totals can be a little higher than Insights'.

The first launch after updating builds a database index for Insights in the background (seconds,
or about a minute for a very large history on a slow disk). Until it is done, Insights still works,
just more slowly.

## Use with Claude and other AI apps (MCP)

Claude Desktop, Claude Code, ChatGPT, Cursor and other apps that support the
[Model Context Protocol](https://modelcontextprotocol.io) can look things up in your history
with the same lookups as Ask AI (the table above). Ask them "what was I reading about
databases yesterday?" and they search OpenRewind on your computer.

It is **off by default**. Open **Settings → Connect AI Apps…** and click the button for your app:

| Button | What it changes |
| --- | --- |
| **Add to Claude** (Claude Desktop) | the `openrewind` entry in `claude_desktop_config.json` (Mac: `~/Library/Application Support/Claude/`; Windows: `%APPDATA%\Claude\`, or `%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Roaming\Claude\` for the Microsoft Store version) |
| **Add to Claude Code** | runs `claude mcp add --scope user openrewind -- <this app> mcp` when it finds the `claude` command; otherwise writes the same entry in `~/.claude.json` |
| **Add to ChatGPT** | the `[mcp_servers.openrewind]` table in `~/.codex/config.toml` (Windows: `%USERPROFILE%\.codex\config.toml`; `CODEX_HOME` if you set it), which the ChatGPT desktop app and Codex read |
| **Add to Cursor** | the `openrewind` entry in `~/.cursor/mcp.json` |

Adding an app also turns on **Allow AI Apps to Read My History (MCP)**. Each row then says
**Added**, offers **Restart Claude** (or ChatGPT, Cursor) when the app is open, since apps read
their setup when they start, and **Remove**, which takes out exactly that entry. Restart asks
first, then quits the app the normal way and opens it again; a reply it is still writing, or a
message you haven't sent, may be lost. If the entry points at another copy of OpenRewind (you
moved the app) or has options you added, the row says so and the button reads **Update**. If you
turned OpenRewind off in the app (`"disabled": true`, or `enabled = false` for ChatGPT), the row
says **Turned off** and **Turn On** switches it back on.

- Only that one entry changes. Everything else in the file stays as it was. The new version goes
  to a private temporary file next to it and replaces the file in one step, only if the app
  didn't change the file in the meantime (otherwise OpenRewind starts over with what the app
  wrote), so it is never left half-written. Claude Desktop with both a Store and a classic
  install has two files: both change, or neither.
- The previous version is kept as one `<file>.openrewind-backup` next to it, readable only by
  you, and **Remove** deletes it. If the file is a link into a folder you keep in git (dotfiles),
  the backup is created in that folder: add `*.openrewind-backup` to its `.gitignore`.
- It is best to quit Claude Desktop before adding: it may write its own settings back when it
  quits. Claude Code is set up with its own `claude` command when OpenRewind finds it (also when
  installed with nvm, Volta, asdf, fnm, Bun or Homebrew).
- A file OpenRewind can't read safely (not valid JSON, not saved as UTF-8, or ChatGPT's file
  already sets up `openrewind` some other way) is never changed: the row says what's wrong and
  you can set it up by hand.
- **ChatGPT**: OpenRewind works in **Work** chats in the ChatGPT desktop app (Mac and Windows) and
  in Codex, which start local MCP servers from `config.toml`. What OpenRewind returns there goes
  to OpenAI like anything else in the chat. Regular chats and chatgpt.com in a browser can't reach
  it: they only connect to MCP servers on the internet.

For other AI apps, the window also shows the lines to paste, with this copy's path filled in and
a **Copy** button. For most apps they look like this (Mac path shown; on Windows it is the path of
your `OpenRewind.exe`):

```json
{
  "mcpServers": {
    "openrewind": {
      "command": "/Applications/OpenRewind.app/Contents/MacOS/OpenRewind",
      "args": ["mcp"]
    }
  }
}
```

Claude Code by hand, run once:

```bash
claude mcp add --scope user openrewind /Applications/OpenRewind.app/Contents/MacOS/OpenRewind mcp
```

ChatGPT and Codex by hand, in `~/.codex/config.toml`:

```toml
[mcp_servers.openrewind]
command = '/Applications/OpenRewind.app/Contents/MacOS/OpenRewind'
args = ['mcp']
```

How it works:

- The AI app starts `OpenRewind mcp` (Windows: `OpenRewind.exe mcp`, or `--mcp`) and talks to it
  over stdin/stdout. It shows no icon or window, records nothing and never changes the database:
  it opens it read-only, so it works whether or not OpenRewind is running.
- Lookups are the same as in Ask AI and capped the same way. Blocked apps, sites and windows are
  left out of screen text, searches and screenshots; in the activity summary a blocked app shows
  only as "(blocked app)" with its time. **Audio transcripts are not filtered by app**, so a call
  or video heard while a blocked app was open can show up in `search_audio`. One activity summary
  covers at most 31 days. Screenshots are only shared when **Let AI look at screenshots** is on in
  AI Settings.
- Names you add with `OPENREWIND_EXCLUDE` are not applied here: the AI app starts OpenRewind with
  its own environment. They are still never recorded; only frames saved before you added a name
  can show up. Put apps on **Blocked Apps & Sites** to hide them everywhere.
- Each moment comes with its exact local time (like `2026-10-18T14:30:05-07:00`), which the AI
  can pass back to read more around it.
- While the switch is off, the AI app still lists OpenRewind, but every lookup answers that you
  need to turn it on. Turning it off takes effect on the next lookup.
- What the AI app reads goes to that app's AI provider, like anything else you ask it.
- Text on your screen (web pages, emails, messages) reaches the AI app as data. Unlike Ask AI,
  OpenRewind cannot stop an AI app from opening a link or running a command that such text asks
  for, and many AI apps (Claude Code, Cursor) can do both. OpenRewind tells the AI to treat it as
  data only, but keep an eye on what it does with your history.

## Choosing a transcription model

**Settings → Transcription Model** picks what turns recorded speech into text. Every choice runs
on your computer; audio never leaves it.

| Model | Download | Languages | Notes |
| --- | --- | --- | --- |
| **Whisper (Automatic)** (default) | none | 99 | Uses the ggml Whisper model already on the computer (see [Whisper](#whisper)). Needs `whisper-cli`. |
| **Whisper Base** | 148 MB | 99 | Fast, less accurate. Needs `whisper-cli`. |
| **Whisper Small** | 488 MB | 99 | A good balance. Needs `whisper-cli`. |
| **Whisper Large v3 Turbo** | 574 MB | 99 | The most accurate Whisper, and the slowest. Needs `whisper-cli`. |
| **NVIDIA Parakeet v3** | 670 MB | 25 European | Very accurate, with punctuation and capitals, and fast on a CPU. Needs nothing else. |

- Picking a model that is not on the computer yet shows its size and downloads it, with a progress
  bar. Files come from a fixed commit on Hugging Face and are checked against their SHA-256 before
  they are used (again the first time they are used after each start of the app). A download that
  stops, or gets no data for 60 seconds, carries on where it left off with **Try Again**. They are
  saved in `~/.openrewind/models`: Whisper files where Whisper already looks, under their own names
  (`ggml-small.openrewind.bin`, so a `ggml-small.bin` you put there yourself is never replaced);
  Parakeet in `models/parakeet-tdt-0.6b-v3-int8`. Delete a file to free the space.
- The new model is used for audio recorded from then on. **Re-transcribe Recent Audio** (last hour,
  day or week) runs older clips through it too. It says how many clips that is and about how long it
  keeps the processor busy; new recordings still go first. The new text **replaces the old for good**
  (the old text stays searchable only until each clip is redone). Clips where no speech was found
  are left as they are, and meeting notes already made are kept: re-transcribing does not make them
  again or send the transcript to your AI provider again.
- The menu shows the model in use, when it last finished a clip and how long that took, and how many
  clips are waiting.
- Each transcript line records which model wrote it (`audio_transcriptions.engine`:
  `whisper.cpp:ggml-small`, `parakeet-tdt-0.6b-v3`, …). Search, Ask AI, the timeline and MCP read
  every line the same way.
- Parakeet languages: Bulgarian, Croatian, Czech, Danish, Dutch, English, Estonian, Finnish, French,
  German, Greek, Hungarian, Italian, Latvian, Lithuanian, Maltese, Polish, Portuguese, Romanian,
  Russian, Slovak, Slovenian, Spanish, Swedish and Ukrainian. It finds the language by itself; the
  `--language` option only applies to Whisper. For other languages pick a Whisper model: when
  `--language` is set to one Parakeet does not know, picking Parakeet (or re-transcribing with it)
  warns first.
- Parakeet runs on [ONNX Runtime](https://onnxruntime.ai) (MIT), built into the app (its notice is in
  `THIRD_PARTY_NOTICES.txt`, which ships with both apps). It holds about 1 GB of memory while it is
  loaded and lets go of it after 5 minutes with no audio to do. **While audio recording is on, a clip
  arrives every 30 seconds, so it stays loaded the whole time**; turn audio off, or pick Whisper, to
  get that memory back. Long audio (`ingest-audio` of a talk, or a long `--chunk`) goes through the
  model in pieces of at most 30 seconds, so memory stays the same however long it is. On a 4-core
  Linux test machine (the Windows code) a 30 s clip took 3.4–3.6 s, about 9 times faster than real
  time, and 5 minutes took 25 s.
- From the terminal, `--model parakeet-v3` (or `whisper-small`, …) picks a model for one run, for
  example `openrewind ingest-audio talk.wav --model parakeet-v3`.

[NVIDIA Parakeet TDT 0.6B v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) is made by NVIDIA
and licensed [CC-BY-4.0](https://creativecommons.org/licenses/by/4.0/). OpenRewind downloads the
version [converted to ONNX and quantized to int8](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8)
by the [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) project (Apache-2.0), and uses it as it is.
Whisper models are [OpenAI's Whisper](https://github.com/openai/whisper) (MIT) in the ggml format of
[whisper.cpp](https://github.com/ggml-org/whisper.cpp) (MIT).

## Mac app

```bash
scripts/build-app.sh
```

This makes `dist/OpenRewind.app` and `dist/openrewind-<version>.dmg`.
Copy the app to `/Applications` and open it, or run `scripts/install.sh`, which builds,
copies to `/Applications` and restarts the app in one step. It lives in the menu bar:

- **Record Screen** and **Record Audio** switch on and off on their own (remembered).
- **Pause Recording**: for 30 minutes, 1 hour, 1 day, or until you resume. Both screen and
  audio stop; the menu shows **Resume Recording** and when recording comes back on (also
  after a restart). The switches still pick what comes back on.
- **Open Timeline** (or **⌥⌘T** from anywhere) shows a full-screen overlay.
  Scroll or use ← → to move through time, ⇧ for 1 minute, ↑ ↓ for 10 minutes,
  drag or click the bar, **space** plays the audio at that moment (the picture
  follows along), **+ / −** zoom, **N** jumps to now, **Esc** closes.
- **O** (or the button at the bottom right of the screenshot) goes back to that moment:
  it reopens the browser tab (Safari, Chrome, Arc, Dia, Edge, Brave, Firefox), the document
  the app had open (Pages, Preview, Xcode…), or brings the app's window with that title to the front.
- **App filter**: the **All apps** button next to the search box (or **A** for the app in the
  shown screenshot) limits the timeline to the moments when one app was in front. Time spent in
  other apps shrinks to breaks on the bar, ← → skip straight between that app's screenshots, and
  search only finds that app's screen text (and speech heard while it was in front).
- **Copy and export**: drag across text on a screenshot and **⌘C** copies it; with nothing
  selected **⌘C** copies the screenshot, **⌘S** saves it as a PNG, **[** and **]** mark a clip and
  **⌘E** exports it as MP4 or GIF. See [Copy and export](#copy-and-export).
- **Search…** opens the timeline with the cursor in its search box, or the web search page
  when the local search server is on.
- **Insights…** shows where your time went today or this week (see [Insights](#insights)).
- **Settings → Timeline Shortcut**: **Change Shortcut…** waits for the new key combination
  (⌘, ⌥ or ⌃ with a key, or an F-key alone); **Reset** goes back to ⌥⌘T.
- **Settings → Screenshot Resolution**: Full (100%), High (75%), Medium (50%, default) or
  Low (33%), with the pixel size each gives on your main display. Higher looks sharper but
  uses more disk space. Applies to the next screenshot.
- **Settings → Timeline Look**: Match System, Light or Dark.
- **Settings → Permissions** lists Screen Recording, Microphone and Accessibility with their
  state; click one to open its page in System Settings. **Settings** shows ⚠︎ when one is off.
- **Settings → Keep Screen / Audio Recordings For**: 1 Day, 1 Week, 1 Month,
  1 Year, or Until I Delete Them (default). Older recordings are deleted at
  launch and every hour.
- **Settings → Delete Recordings**: last 15 minutes, last hour, today, all
  screen, all audio, or everything (asks first).
- **Settings → Transcription Model**: Whisper (Automatic, the default), Whisper Base, Small or
  Large v3 Turbo, or NVIDIA Parakeet v3, plus **Re-transcribe Recent Audio**. See
  [Choosing a transcription model](#choosing-a-transcription-model).
- **Settings → Local Search Server** turns on the API and browser search page at
  `http://127.0.0.1:3031`. Off by default. Only this Mac can connect. The terminal
  commands `record` and `serve` always start it.
- **Settings → Search by Meaning** (off by default) indexes your recordings in the background
  so you can search by meaning, with the model that comes with macOS. See
  [Search by Meaning](#search-by-meaning).
- **Settings → Launch at Login** starts it with your Mac.
- The lock screen and screen saver are never recorded.
- **Settings → Blocked Apps & Sites** picks apps to blur, plus words that hide a window when its
  title has them (a browser's title is the page title, so "PayPal" hides PayPal tabs). **Websites
  and links** ("chase.com", or a full link to one page) are matched against the address each
  browser window shows, read with Accessibility just before each screenshot: every time for the
  window in front, and for windows behind it whenever their title changes (a tab switch or a new
  page). A window whose address can't be read after it changed is hidden too. **Add Sites…** lists
  the sites and pages you visited lately to tick. Private
  browsing windows are hidden when the browser names them in the title (Incognito, InPrivate,
  Private Browsing).
- **Settings → Show Tutorial** walks through the timeline again; **Run Setup Again…** reopens
  the first-run setup.
- Log: `~/.openrewind/openrewind.log`.

### Put it online for others

Downloads open without a warning only when the app is signed with a
**Developer ID Application** certificate and notarized by Apple:

1. Create the certificate at developer.apple.com → Certificates (needs the paid program).
2. Save notary login once:

```bash
xcrun notarytool store-credentials openrewind --apple-id YOU@EXAMPLE.COM --team-id TEAMID
```

3. Build, sign, notarize, staple:

```bash
NOTARY_PROFILE=openrewind scripts/build-app.sh
```

Upload the `.dmg` (GitHub Releases, your site, etc.).
Without these steps the app still works, but other people must right-click → Open.

### Updates

Both apps ask GitHub for the latest release every 6 hours and when you open the timeline. A newer
version shows a banner at the top of the timeline (✕ hides it until the next version) and a row in
the menu. **Settings → Download Updates Automatically** (on by default) fetches it in the background;
nothing is installed until you click **Update** / **Restart to Update**, which swaps the app in place
and reopens it. **Settings → Check for Updates…** checks right away.
Releases come from the Release workflow (`git tag v0.3.0 && git push origin v0.3.0`); the check needs
the repository to be public. Screen Recording, Accessibility and Microphone stay on across updates
because every build is signed with the same certificate (below).

### Signing

macOS remembers Screen Recording, Accessibility and Microphone for the certificate an app is signed
with. An ad-hoc signature is new on every build, so each update used to lose them. Now every build
is signed with one long-lived certificate:

- **Make it once:** `scripts/make-signing-cert.sh` writes a self-signed "OpenRewind Code Signing"
  certificate to `signing/` (valid 20 years). Keep the `.p12` and password safe and don't commit
  them: anything signed with that key gets the permissions you gave OpenRewind.
- **GitHub Actions:** the real certificate is only for releases. It lives in a `release`
  environment that only `main` and `v*` tags can use, so a pushed branch or a PR can't reach it
  even by editing a workflow. Branch and PR builds sign with a throwaway certificate. Set it up once:
  1. Settings → Environments → **New environment**, name it `release`.
  2. Deployment branches and tags → **Selected branches and tags** → add the branch `main` and the
     tag pattern `v*`. Optionally tick **Required reviewers** and add yourself, so each release waits
     for your click.
  3. In that environment, **Add environment secret**: `MACOS_CERTIFICATE_P12` (the contents of
     `openrewind-signing.p12.b64`) and `MACOS_CERTIFICATE_PASSWORD` (the contents of `password.txt`).
  4. Settings → Secrets and variables → Actions: **delete** any repository secrets with those names
     (and the `AZURE_*` ones, after adding them to the environment). Repository secrets are open to
     every branch.
  5. Settings → Rules → Rulesets → **New tag ruleset** for `v*`, restricting creation to you, so
     nobody else can push a tag that opens the environment.

  CI imports the key as non-extractable and deletes the keychain after signing. The Release workflow
  refuses to build the Mac app without the secrets.
- **Your Mac:** double-click `openrewind-signing.p12` and enter the password. `scripts/build-app.sh`
  picks it up (after a Developer ID certificate, before Apple Development or ad-hoc); allow
  `codesign` to use the key when asked.
- **Developer ID later:** export the Developer ID Application certificate as a `.p12` and put it in
  the same two secrets. That also removes the "unidentified developer" warning (with notarizing).

Changing the certificate (ad-hoc → self-signed, or self-signed → Developer ID) means turning the
permissions on one more time; after that, updates keep them.

## Windows app

Windows 10 (2004+) and 11, x64 and ARM64. Same timeline, search page, API and data
format as the Mac app; the timeline opens with **Ctrl+Alt+T**. Download, setup and
build steps are in [windows/README.md](windows/README.md):

```powershell
scripts\build-windows.ps1
```

## Run from the terminal

```bash
swift build -c release
```

```bash
.build/release/openrewind doctor
```

```bash
.build/release/openrewind
```

Then open http://127.0.0.1:3031.

### Whisper

Needs `whisper-cli` (`brew install whisper-cpp`) and a ggml model. **Settings → Transcription
Model** downloads one (Whisper Base, Small or Large v3 Turbo), or OpenRewind looks for a model in
this order:

1. `--whisper-model PATH` or `$OPENREWIND_WHISPER_MODEL`
2. The Whisper size picked in Settings, once downloaded
3. `~/.openrewind/models/ggml-*.bin`
4. VoiceInk's models folder (already on this Mac: `ggml-large-v3-turbo-q5_0.bin`)

NVIDIA Parakeet v3 needs neither `whisper-cli` nor a ggml file: pick it in Settings and it downloads.

Test it without a mic:

```bash
.build/release/openrewind ingest-audio /opt/homebrew/share/whisper-cpp/jfk.wav
```

macOS asks for **Screen Recording** (screen + system audio) and
**Microphone** permission for the app that runs the
command (for example Terminal). Grant it, then run again. **Accessibility**
permission is optional; it gives cleaner text than OCR.

## API

On when you run `record` or `serve` from the terminal, or after **Settings → Local Search Server** in the app.

| Route | What |
|-------|------|
| `GET /health` | Status, frame count, permissions |
| `GET /search?q=&app_name=&window_name=&start_time=&end_time=&limit=&offset=&max_content_length=` | Full-text search. Times: ISO, `2h ago`, `today`, `yesterday`, `YYYY-MM-DD` |
| `GET /frames/{id}` | Snapshot as JPEG |
| `GET /apps?start_time=` | Apps with frame counts |
| `GET /audio/{chunk_id}` | Audio chunk WAV |

`content_type`: `all` (screen + audio), `ocr` (screen only), `audio`.

`mode=meaning` searches by meaning instead of words (only while **Search by Meaning** is on, else
400; `/health` says `"search_by_meaning": true`). Results come best first, in the same shape plus a
`score`, and leave out blocked apps and sites. A screen result is a moment: `timestamp` is where
it starts and `text` is the moment's text.

Response: `{"data":[{"type":"OCR","content":{"frame_id","text","timestamp","app_name","window_name","file_path","text_source"}},{"type":"Audio","content":{"chunk_id","transcription","timestamp","start_time","end_time","device_name","device_type"}}],"pagination":{"limit","offset","total"}}`

## Data

`~/.openrewind/db.sqlite`, `~/.openrewind/data/<date>/*.heic` (`*.jpg` on Windows and older recordings), and
`~/.openrewind/data/audio/<date>/*.wav`.
Delete the folder to wipe everything, or from the terminal:

```bash
.build/release/openrewind delete older:week
```

(`15m`, `1h`, `today`, `all`, `older:day|week|month|year`; add `--screen-only` or `--audio-only`.)

## Not yet (Rewind had these)

- Speaker identification (who said what)
- Meeting summaries (Ask AI and the Journal recap summarize the day's transcripts, but not each meeting on its own)
- UI input events (clicks, keys)
- Browser URL per frame
- Video chunk encoding (ffmpeg) instead of JPEGs
- Plugins

## License

[MIT](LICENSE). Use it, change it, share it. What the apps include from others, and under which
licenses, is in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
