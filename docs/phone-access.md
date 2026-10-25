# Phone Access: design and threat model

Phone Access lets a phone you paired search your history, ask AI about it and see your promises, from
the phone's browser. It is **off by default**. Turn it on in **Settings → Security → Phone Access**
(Mac and Windows), then click **Pair a Phone…** and scan the QR code with the phone's camera.

## What the phone can get

| Ask | Answer |
| --- | --- |
| Search (all, screen or audio) | Up to 30 matches: time, app, window title and up to 400 characters of text. Goes through the same `/search` as the local API, so blocked apps, sites and words are left out and App Lock applies |
| Ask AI | The answer text, from your own provider and key, with the same lookups as Ask AI (never screenshots). Only when Ask AI is set up and its privacy note accepted. Costs tokens, added to the totals in AI Settings |
| Promises | Open promises (what, for whom, due, when said, where), leaving out blocked sources |

Screenshots, audio, file paths and settings are never sent to the phone. There is nothing to change
or delete from the phone.

## How it works

- **A separate server.** Port **3032**, apart from the local API on 127.0.0.1:3031. It listens only on
  this computer's **private** IPv4 addresses (10/8, 172.16/12, 192.168/16 and 100.64/10, which
  Tailscale uses) of the network it is really on: on Windows adapters with a default gateway, plus
  Tailscale; on the Mac Wi-Fi and Ethernet first, VPN tunnels only with Tailscale addresses. Virtual
  adapters of WSL, Hyper-V, Docker, VirtualBox and VMware (and Mac bridges) are left out, so nothing in a
  container or VM can reach it. It never listens on a public address, drops connections from any
  other address without answering, and does not share its port (no address reuse). When the
  network changes it listens again on the new addresses. It serves exactly two things: the phone
  page (`GET /`) and `POST /api`, and only to requests whose `Host` is one of its own addresses and
  whose `Origin`, if any, is the page it served (so other web pages and DNS rebinding are refused).
- **Pairing.** Pair a Phone makes a random device id (64 bits) and a random 256-bit **one-time pairing
  key**, saved in the `phone_devices` table (both apps use the same table; with Encrypt Recordings on
  it is encrypted with the rest of the database). The QR code holds
  `http://<address>:3032/#d=<id>&k=<key>`. The part after `#` is a URL fragment: browsers never send it
  to the server, so the key is not on the network, even when the page loads. The page keeps it in the
  phone's local storage and takes it out of the address bar at once.
- **The phone gets its own key.** The answer to the phone's first request carries a new random
  256-bit key, sealed with the pairing key. The page stores it and uses it from the next request on;
  when the computer sees that, the pairing key is deleted and the pairing window closes by itself.
  From then on the QR code is worthless: a photo or screen recording of it opens nothing. A code
  that does not finish pairing within 15 minutes stops working and is forgotten. The pairing window
  is kept out of screenshots: OpenRewind never records its own windows, and the window is also hidden
  from other screen capture (Windows `WDA_EXCLUDEFROMCAPTURE`, Mac `sharingType = .none`).
- **Every request is sealed.** Keys come from the secret with HKDF-SHA256 (salt `OpenRewind phone`),
  one for requests (`openrewind/phone/v1/req/<id>`) and one for answers (`…/res/<id>`). Each message
  is AES-256-GCM with a fresh random 96-bit nonce. A request seals `{seq, ts, op, args}` with the
  extra data `openrewind-phone-v1|req|<id>`; an answer is sealed with
  `openrewind-phone-v1|res|<id>|<seq>`, so it only opens as the answer to that one request.
- **Replays and old requests are refused.** Each request number is used once. Requests can arrive out
  of order (the page sends several at once), so a number is accepted when it is higher than any
  before, or one of the 64 below the highest that was not used yet (a sliding window, as in IPsec and
  WireGuard; checked and recorded under the database lock). `ts` must be within 5 minutes of the
  computer's clock. A replay or a stale request was sealed with the right key, so it answers `409`
  ("try again" or "check the clock") and is not counted as a guess.
- **Anything else is refused** with the same `401 not paired`: unknown or revoked device, expired
  pairing code, wrong key, changed bytes. There is no plaintext mode and no fallback: the page encrypts in the
  browser with SJCL (AES, GCM, SHA-256, HMAC, HKDF), shipped inline in the page, and refuses to send
  when the browser has no secure random source. A strict Content-Security-Policy lets the page talk
  only to the computer that served it, and results are shown as text, never as HTML.
- **Guessing is slowed.** 10 refused requests from one address in 5 minutes block that address for
  15 minutes; 100 wrong-key requests in all within 5 minutes block every phone for 5 minutes.
  Requests refused for their `Host` or `Origin` (stray web pages) never count.
- **App Lock.** While OpenRewind is locked, a paired phone gets only "locked" (sealed), nothing else.
- **Revoke.** Settings lists paired phones with the time they were paired and last used. Revoke
  deletes the phone's secret, so nothing it sends is accepted again.
- **Turns itself off** after 1, 4 (default) or 12 hours with no request from a paired phone, or never.
- **Answers can take time.** A request has 15 seconds to arrive and up to 2 minutes to be answered
  (Ask AI makes several lookups).

The protocol is in `windows/OpenRewind.Core/Phone.cs` (tests in `PhoneTests.cs` and `PhoneOpsTests.cs`:
round trip, tampering, replay and stale requests, requests out of order, the key replacing the QR code's, an expired code, Host and Origin checks, wrong key, unknown and revoked devices, locked,
rate limits, and a test vector made by the page's own SJCL) and `Sources/openrewind/PhoneAccess.swift`
(checked against the same vectors by `OpenRewind features-check` in the Mac CI). The page is
`Sources/openrewind/PhonePage.swift`; the Windows app serves the same file.

## Threat model

| Who | What they can do |
| --- | --- |
| Someone else on the Wi-Fi (or the router) | See that a phone talks to port 3032, how often and how much. They cannot read or change requests or answers, replay them, or use the API without the secret. They can load the page itself (it has no data) and make failed tries, which are rate-limited |
| Someone with the QR code or a photo of it | Nothing once your phone has paired (the code's key is deleted then). Before that, within the 15 minutes, they could pair in your place, and your phone would then fail to pair: if pairing fails, revoke unknown phones in Settings. Don't share the code |
| Someone with your unlocked phone | Use the page like you, while Phone Access is on and the computer is unlocked. Revoke the phone in Settings if you lose it |
| Someone on the internet | Nothing: the server does not listen on public addresses and drops connections from them. Phone Access does not open router ports, and internet access is out of scope |
| A malicious web page on the phone or the network | Cannot read the key (it is stored for the computer's own address only) or the answers, and its requests are refused before they count toward any block |

Limits to know:

- If the computer's address changes (another network, a new DHCP lease), the phone's saved page is
  for the old address and the phone has to be paired again. The computer listens on the new address
  by itself, including on other networks it joins (a café or hotel Wi-Fi with private addresses);
  turn Phone Access off when you don't need it, or rely on the auto-off.
- The page itself comes over plain HTTP on your network. Someone who controls the network *at the
  moment you open the page* could serve a fake page that steals the stored secret. Opening the page
  only on networks you trust (home, or a VPN) avoids this; installing it to the home screen does not
  change it. A future version could ship the page as a fixed, signed app instead.
- Search text is limited by what the block list hides; it is not filtered further for the phone.
- Ask AI sends your question and what it looks up to your AI provider, as Ask AI on the computer does.

## Away from home: Tailscale or another VPN

Phone Access works only where the phone can reach the computer's private address. To use it away
from home, put the phone and the computer on the same VPN, such as **Tailscale** (its 100.x addresses
are accepted) or WireGuard to your home network, then pair while the phone is on the VPN, so the code
holds the computer's VPN address. Do not forward port 3032 on your router.
