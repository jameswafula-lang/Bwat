# Bwat – Live Classroom Quiz

**BCS 3105 Client Server Programming for Apps – Semester Final Project (Topic i: Gaming Application)**

*Work it out, type it on the calculator, and be the FIRST to get it right.*

Bwat is a networked, real-time multiplayer quiz game. An **admin** logs in, creates a quiz room (any name,
up to **40 players**) and shares the **room link**. **Players** open the link, type their **name or nickname**
(no account needed) and play. Everybody gets the same math question and types the answer on a calculator.
**Each question carries 5 marks, and only the first correct answer gets them.**

| Part | Technology |
|---|---|
| Language / IDE | C# 5, .NET Framework 4.5, SharpDevelop 5.1 (also opens in Visual Studio) |
| Server | Console app, TCP sockets (`Socket` Bind/Listen/Accept), one thread per client (master/slave concurrent server) |
| Client | Windows Forms (thin client) using `TcpClient` + `StreamReader`/`StreamWriter`; `ClientWebSocket` (wss://) for the online server |
| Hosting | Local network (plain TCP) or **Render** cloud (Docker, `Dockerfile` + `render.yaml`) for players on other networks |
| Database | SQLite (`bwat.db`) through System.Data.SQLite 1.0.118 (`lib/`), accessed **only by the server** |
| Security | Two roles (Admin / Guest), salted PBKDF2 password hashes for admins, server-side permission checks, parameterised SQL, login lock-out, audit log |

---

## 1. Game rules

* **Joining:** the admin creates a room and shares its link (`bwat://192.168.1.20:8765/BWAT-4821`).
  Players open it, type a name (2–20 characters, unique in the room) and wait in the lobby. Up to 40 players.
* **Start:** at the start time the room locks, or the admin clicks *Start now*. At least 2 players are needed.
* **Each question:**
  * The question appears in the question box. There are no answer choices; players type the answer on the calculator.
  * **Time depends on difficulty:** easy 30 s, medium 45 s, hard 60 s. Questions go from easy to hard.
  * Each player has a **balloon** and **3 tries**. A wrong answer shrinks the balloon, and the 3rd wrong answer **pops** it.
  * The **first correct answer gets 5 marks**: that balloon **floats away** and the question ends at once.
    Everybody else gets 0 and their balloon pops.
  * If nobody has got it right when the time runs out, the **timer restarts** (at most 2 times).
    After the 3rd time-out, or when every balloon has popped, everyone's balloon pops and it's **BONGO!**
* **Between questions:** the **standings** of every player (all 40), then the next question.
* **At the end:** the **top three**, or **BONGO!** if everybody has 0 marks or there is a draw for first place.
* **Active board:** names and marks, re-sorted live during the game. Equal marks share a position (1, 1, 3 …).

## 2. Roles and permissions

| Role | How | Can do |
|---|---|---|
| **Guest (player)** | No account, just a name per room | `JOIN` a room, `ANSWER`, `LEAVE`, `PING` – nothing else |
| **Admin** | Username + password (stored as salted PBKDF2 hash) | Everything: rooms, questions, admins, past games, activity log, watch and control live rooms |

The server checks every request. A guest who sends an admin request gets `Permission denied`, and the attempt
is written to the activity log. Five wrong admin passwords lock that username for 60 seconds.

## 3. Admin controls during a game

The admin opens a room (*Rooms → Open*) and sees the question **with its answer**, **every player's balloon and
tries**, and the active board. Buttons: **Start now, Skip question, Remove player, End game, Copy link, Close**.
A removed player cannot rejoin the room with the same name.

## 4. Project structure

```
Bwat/
├── Bwat.sln                  ← open this in SharpDevelop / Visual Studio
├── Dockerfile, render.yaml   ← how Render builds and runs the server (Mono on Linux)
├── lib/                      ← System.Data.SQLite.dll + x86/x64 SQLite.Interop.dll (external library)
├── BwatCommon/Protocol.cs    ← message format, command names, link format, levels, timing (shared)
├── BwatServer/               ← SERVER + DATABASE (James)
│   ├── Program.cs            master: socket, Bind, Listen, Accept loop, new Thread per client
│   ├── ConnectionHandler.cs  slave: reads requests, checks role and input, sends responses
│   ├── NetChannels.cs        line channels: plain TCP or WebSocket (RFC 6455) + small HTTP health page
│   ├── HttpPolling.cs        HTTP long-polling sessions for networks that block WebSockets
│   ├── GameRoom.cs           one quiz: questions, tries, balloons, timers, marks, BONGO (own thread + lock)
│   ├── RoomManager.cs        open rooms, room creation rules, room links
│   ├── Database.cs           every SQL statement
│   ├── PasswordHasher.cs     salted PBKDF2 password hashing
│   ├── DifficultyEngine.cs   rates questions (→ time) and learns from real results
│   ├── AnswerChecker.cs      compares answers (0.5 = 1/2 = .5)
│   ├── QuestionBank.cs       120 starting math questions (20 per level, Nursery → University)
│   └── Models.cs, ServerLog.cs
└── BwatClient/               ← CLIENT UI (Moritz)
    ├── Program.cs            start-up; registers bwat:// links for the current Windows user
    ├── StartForm.cs          join with link + name, or admin login
    ├── GameForms.cs          PlayerGameForm (question / calculator / active board) and AdminRoomForm
    ├── GameWidgets.cs        question box with balloon, calculator, active board, standings, balloon grid
    ├── AdminForm.cs          rooms, questions (CRUD), past games, admins, activity log
    ├── ServerConnection.cs   TCP / WebSocket / HTTP connection, background reader thread, hands messages to the UI thread
    ├── DirectWebSocket.cs, HttpPollTransport.cs   fallback ways to reach the online server
    └── Session.cs, Theme.cs, UiKit.cs
```

## 5. How to build and run

1. Open **`Bwat.sln`** in SharpDevelop 5.1 (or Visual Studio 2012+). If you ran an older version, **delete the old `bwat.db`**.
2. **Build → Build Solution.** `System.Data.SQLite.dll` and the `x86`/`x64` folders are copied next to the server.
3. **Start the server:** `BwatServer\bin\Debug\BwatServer.exe`.
   * The first time, it creates `bwat.db` with 120 questions and **asks you to create the first admin**.
     No password is written in the code.
   * It shows the computer's IP addresses and the format of the room links.
     For players on the internet, put your tunnel / cloud address in `link_address.txt` (see section 6).
   * If Windows Firewall asks, click **Allow access**.
4. **Admin:** run `BwatClient\bin\Debug\BwatClient.exe`, type the server address and log in. Go to **Rooms → Create room**.
   The link is copied to the clipboard; share it (WhatsApp, e-mail, projector …).
5. **Players:** run `BwatClient.exe` and paste the link (or type just the code plus the server address), type a name, and click **Join**.
   After Bwat has run once on a computer, **clicking a `bwat://` link opens Bwat directly** with the link filled in.
6. Admin: **Rooms → Open** to watch the room, then **Start now** when everybody is in.

## 6. Playing with people on other networks (Render – free cloud server)

Players are at home on different networks, so the server runs on **Render** (render.com), a cloud host.
No laptop needs to stay on and players install nothing extra.

Render only lets web traffic in (HTTPS, port 443). The Bwat server therefore accepts **two kinds of connection
on the same port**: plain TCP (local network, as in the lecture notes) and a **secure WebSocket** (`wss://`) for the
internet. Both carry exactly the same protocol lines. The client picks the right one by itself: port **443** means
"online server → WebSocket", any other port means plain TCP.

Some networks open web pages but block or break WebSockets. So for the online server the client first opens
the server's web page (this also wakes up a sleeping server), then tries up to **three ways**, and remembers the one that worked:
1. WebSocket (`ClientWebSocket`),
2. its own direct WebSocket over `TcpClient` + `SslStream` (avoids Windows proxy / IPv6 problems – `DirectWebSocket.cs`),
3. plain HTTPS requests, "long polling" (`HttpPollTransport.cs` ↔ `HttpPolling.cs`) – works wherever a browser works.

If Windows cannot look up the server's name (some networks' DNS fails, while browsers still work with their own
"secure DNS"), the client asks the public DNS servers 8.8.8.8 / 1.1.1.1 itself, or Cloudflare's secure DNS (`NetHelpers.cs`).

### A. Put the server online (once)
1. Create a free account on **github.com**, make a new repository (e.g. `bwat`) and upload **everything in this folder**
   (`Dockerfile`, `render.yaml`, `BwatCommon`, `BwatServer`, `BwatClient`, …). `Dockerfile` and `render.yaml` must be at the top level.
2. Create a free account on **render.com** and sign in with GitHub.
3. Click **New → Blueprint**, pick the `bwat` repository and click **Apply**.
   (Or: **New → Web Service**, pick the repository, *Language/Runtime* **Docker**, *Instance type* **Free**.)
4. Render asks for two **environment variables** – this is the first admin account (never written in the code):
   * `BWAT_ADMIN_USER` – e.g. `james`
   * `BWAT_ADMIN_PASSWORD` – at least 6 characters
5. Wait for the build to finish (about 5 minutes the first time). In **Logs** you should see
   `Room links will use: bwat://bwat-xxxx.onrender.com:443/BWAT-xxxx`.
6. Open `https://bwat-xxxx.onrender.com` in a browser: it shows **"Bwat server 2.0 is running"**.

### B. Play
1. Admin: open `BwatClient.exe`, type the server address **`bwat-xxxx.onrender.com`** (no port needed), username and password → **Log in**.
2. **Rooms → New room**, then **Copy link** – it looks like `bwat://bwat-xxxx.onrender.com:443/BWAT-4821`. Send it on WhatsApp.
3. Players open `BwatClient.exe`, **paste the link**, type a name and click **Join**.

### Things to know about the free plan
* **It sleeps** after 15 minutes without visitors. The first connection then takes up to a minute
  (the client says "this can take up to a minute"). Tip: open the `https://…onrender.com` page a minute before class.
* **The disk is not kept**: when Render restarts the server, questions you added, rooms and past games are reset
  (the 120 built-in questions and the admin from the environment variables come back automatically).
  To keep the data, add a paid **Disk** mounted at `/var/data` and set `BWAT_DB_PATH=/var/data/bwat.db` (see `render.yaml`).

### Sending the player app
Zip the folder `BwatClient\bin\Debug` (`BwatClient.exe` + `BwatCommon.dll`) and send it by WhatsApp, Google Drive or USB.
Players unzip it and double-click `BwatClient.exe`. If Windows shows "Windows protected your PC", they click **More info → Run anyway**
(the app is not signed). Chat apps often do not make `bwat://` links clickable, so copy and paste is the easiest way.

### Same Wi-Fi / hotspot (e.g. the defence) – no internet needed
Run `BwatServer.exe` on one laptop as in section 5. The admin logs in with `127.0.0.1:8765`; links use the laptop's
LAN address (`bwat://192.168.x.x:8765/…`) and players connect over plain TCP.
(Optional: `link_address.txt` next to `BwatServer.exe` can force another address in the links, e.g. a tunnel.)

### Server settings (environment variables)
| Variable | Meaning | Default |
|---|---|---|
| `PORT` | port to listen on (Render sets it) | 8765 |
| `BWAT_ADMIN_USER`, `BWAT_ADMIN_PASSWORD` | first admin account, created if no admin exists | asked in the console |
| `BWAT_DB_PATH` | where the SQLite file is kept | `bwat.db` |
| `BWAT_LINK_ADDRESS` | address used in room links | Render's own address (`RENDER_EXTERNAL_HOSTNAME:443`), else the LAN IP |

## 7. Protocol (client ⇄ server)

One text line per message, fields separated by `|`. On the local network a line is sent over a plain TCP
stream (`StreamWriter.WriteLine` / `StreamReader.ReadLine`); over the internet each line is one WebSocket text message
(the server tells them apart from the first line: an HTTP `GET` means WebSocket, anything else plain TCP). The client sends
`PING` every 25 s so proxies do not drop an idle connection. A `|`, `\` or new line inside a field is escaped as
`\p`, `\\` or `\n`. Lists are sent as `BEGIN|type`, `ROW|type|…`, `END|type`.

| Client → Server | Who | Meaning |
|---|---|---|
| `JOIN\|code\|nickname` | guest | join a room (an admin sends `JOIN\|code` to watch it) |
| `ANSWER\|round\|typedAnswer` | guest | submit an answer |
| `LEAVE`, `PING` | anyone | leave the room / check the connection |
| `LOGIN\|user\|pass`, `LOGOUT` | admin | log in / out |
| `ROOM_CREATE\|name\|level\|maxPlayers\|questions\|startInMinutes` | admin | create a room (reply contains the link) |
| `ROOM_LIST`, `ROOM_START\|code`, `ROOM_CANCEL\|code` | admin | manage rooms |
| `ROOM_SKIP\|code`, `ROOM_KICK\|code\|nickname`, `ROOM_END\|code` | admin | control a running game |
| `Q_LIST\|level`, `Q_SAVE\|id\|level\|text\|answer\|difficulty`, `Q_DELETE\|id` | admin | question bank CRUD |
| `GAMES_LIST`, `GAME_RESULTS\|code` | admin | past games and their standings |
| `ADMIN_LIST`, `ADMIN_CREATE\|user\|name\|pass`, `ADMIN_SET_ACTIVE\|id\|0/1` | admin | admin accounts |
| `AUDIT_LIST` | admin | activity log |

| Server → Client | Meaning |
|---|---|
| `WELCOME\|text`, `OK\|cmd\|msg\|extra`, `ERR\|cmd\|msg`, `LOGIN_OK\|id\|user\|name` | general replies |
| `ROOM_JOINED\|code\|name\|level\|questions\|toStart\|watching\|status\|nickname\|link` | you are in the room |
| `PLAYERS\|n\|(nickname\|marks\|online)×n` | the active board (sorted) |
| `GAME_START\|questions\|marks` | "Get ready" |
| `QUESTION\|round\|total\|text\|seconds\|marks\|difficulty\|timer` | a new question (**the answer is never sent to players**) |
| `ANSWER_KEY\|round\|answer` | the answer – **only to watching admins** |
| `ANSWER_WRONG\|round\|triesLeft` | your answer was wrong (balloon shrinks / pops) |
| `BALLOON\|nickname\|state\|tries` | a player's balloon changed – only to watching admins |
| `TIMER_RESTART\|round\|seconds\|timer` | nobody has got it yet – the timer restarts (2 of 3, 3 of 3) |
| `ROUND_RESULT\|round\|winner\|answer\|marks\|seconds` | who got it first (empty winner = BONGO) |
| `STANDINGS\|round\|total\|n\|(nickname\|marks\|gained\|rank\|prevRank)×n` | everybody's position between questions |
| `FINAL_RESULTS\|bongo\|n\|(nickname\|marks\|solved\|place)×n` | final results (top 3 or BONGO) |
| `KICKED\|reason`, `ROOM_CANCELLED\|reason`, `INFO\|text` | notices |

## 8. Database (SQLite – `bwat.db`)

| Table | Key fields | Purpose |
|---|---|---|
| `Admins` | Id, Username (unique), DisplayName, PasswordHash, Salt, IsActive | admin accounts |
| `Levels`, `Subjects` | Id, Name | Nursery … University; Mathematics |
| `Questions` | Id, LevelId, Text, Answer, Difficulty, DifficultyManual, TimesAsked, TimesCorrect, IsDeleted | question bank |
| `Rooms` | Id, Code (unique), Name, LevelId, MaxPlayers, StartTime, Status, CreatedBy, Winner, EndedAt | quizzes |
| `RoomQuestions` | RoomId, QuestionId, OrderNo | the questions of each quiz (easy → hard) |
| `RoomPlayers` | Id, RoomId, Nickname (unique per room), Marks, Solved, Placement, Removed | the players of each quiz and their results |
| `PlayerAnswers` | RoomId, RoomPlayerId, RoundNo, QuestionId, Answer, IsCorrect, TimeMs, Marks | every typed answer, right or wrong |
| `GameEvents` | RoomId, EventTime, EventType, Detail | timeline: start, solved / BONGO / skipped, game over |
| `AuditLog` | EventTime, Who, Action, Detail | logins, failed logins, joins, admin actions, denied requests |

Open `bwat.db` with *DB Browser for SQLite* (free) to show the data.

## 9. Unique features

1. **First-to-solve-takes-all with typed answers.** There are no options to guess from. The server checks answers one at a time
   inside a lock, so if two answers arrive at almost the same moment, only the first one can win.
2. **Balloons with 3 tries** and **timer restarts with BONGO**.
3. **Join by link and nickname**, with no registration. Links open the app directly (`bwat://`).
4. **Live admin control room:** the admin sees the answer and every player's balloon, and can skip, remove a player or end the game.
5. **Smart difficulty engine:** rates new questions (which sets their time) and re-rates them from how often they are solved.
6. **Reconnect:** a player who loses the connection rejoins with the same link and name and keeps their marks.

## 10. Defence demo (8–10 minutes)

1. Start the server (show the listening port, IPs and link format).
2. Log in as admin, create a room, and copy the link. Show that a guest gets `Permission denied` for admin requests.
3. Two or three laptops open the link and type names. The admin clicks **Open** and watches the lobby fill up.
4. **Start now.** Show a wrong answer (balloon shrinks), 3 wrong answers (balloon pops), the first correct answer (+5, balloon
   floats, everyone else 0), the active board, and the standings between questions.
5. Let one question run out with no answer to show the timer restart (or click **Skip question**), and show BONGO.
6. **Remove player**, and show that they cannot rejoin. Close a player's window and rejoin with the same name: the marks are kept.
7. Final results (top 3 or BONGO), then **Past games**, the **Activity log**, and the tables in `bwat.db`.
8. Stop the server: the clients show "Connection lost".

## 11. Tests that were run (evidence for the report)

These tests were automated against the real server, with up to 41 clients connected at the same time. All passed.

| # | Test case | Input / action | Expected result | Result |
|---|---|---|---|---|
| 1 | Wrong admin password | LOGIN admin / wrong | `ERR` "Wrong username or password" | Pass |
| 2 | Admin login | LOGIN admin | `LOGIN_OK` | Pass |
| 3 | Guest permissions | guest sends `Q_LIST`, `ROOM_CREATE` | `ERR` "Permission denied" | Pass |
| 4 | Room with any name | name `Mr. Okello's P7 \| Friday Fun!` | room created, link returned, name stored exactly | Pass |
| 5 | Player limit | max players 41 | `ERR` (2–40) | Pass |
| 6 | Join by nickname | guest JOIN with "Amina" | `ROOM_JOINED` with name and link | Pass |
| 7 | Duplicate / bad nickname | "amina" again, "x" | `ERR` (taken / too short) | Pass |
| 8 | 40 players | 40 guests join, then a 41st | 40 accepted, 41st `ERR` "room is full" | Pass |
| 9 | Admin watches | admin JOIN | `ROOM_JOINED` (watching) | Pass |
| 10 | Remove player | `ROOM_KICK` | player gets `KICKED`, cannot rejoin with the same name | Pass |
| 11 | Answer secrecy | each question | players get no answer; only the admin gets `ANSWER_KEY` | Pass |
| 12 | Time by difficulty | each question | 30 / 45 / 60 s for easy / medium / hard | Pass |
| 13 | Three tries | 3 wrong answers | tries left 2, 1, 0; balloon POPPED (admin sees it) | Pass |
| 14 | 4th try | answer after popping | `ERR` "Your balloon has popped" | Pass |
| 15 | First correct wins | two correct answers 0.05 s apart | only the first gets +5 | Pass |
| 16 | Standings | after a question | all 39 players listed, ties share a position | Pass |
| 17 | Timer restarts | nobody answers | restart after 1st and 2nd time-out, BONGO after the 3rd | Pass |
| 18 | Everybody popped | all players use 3 wrong tries | question ends at once with BONGO | Pass |
| 19 | Skip question | admin `ROOM_SKIP` | question ends, no marks | Pass |
| 20 | Final results | normal game | winner with most marks | Pass |
| 21 | Final BONGO (all 0) | nobody scored | `FINAL_RESULTS` with BONGO | Pass |
| 22 | Final BONGO (draw) | 5 – 5 at the top | `FINAL_RESULTS` with BONGO | Pass |
| 23 | Reconnect | player's connection closed, rejoins with same name | marks kept, game continues | Pass |
| 24 | Start with < 2 players | start time reached with 1 player | room waits, starts when the 2nd player joins | Pass |
| 25 | Persistence | after games | Past games, final standings and every answer saved | Pass |
| 26 | Password storage | Admins table | only hash + salt stored | Pass |
| 27 | Full UI run | real client with 6 players and a watching admin | lobby → questions → balloons → standings → BONGO → final | Pass |
| 28 | Cloud settings | server started like Render (`PORT`, `RENDER_EXTERNAL_HOSTNAME`, admin from env vars, no console) | admin created, links use `…onrender.com:443` | Pass |
| 29 | Health page | browser `GET /` | "Bwat server is running" (Render health check) | Pass |
| 30 | WebSocket clients | admin + players over WebSocket, mixed with TCP players in one room | same game, first correct +5, BONGO, keep-alive | Pass |
| 31 | Blocked WebSockets | client with WebSocket and direct connection both failing | falls back to HTTPS polling, whole game works | Pass |
| 32 | Health check noise | browser / Render health check requests | not counted or logged as players | Pass |
| 33 | Windows cannot find the server name (DNS) | DNS lookup fails, browser still works | app asks 8.8.8.8 / 1.1.1.1 itself (or secure DNS) and connects | Pass |

## 12. External code and acknowledgement

* **System.Data.SQLite 1.0.118** (public domain, from the SQLite project, via NuGet) – the database driver in `lib/`.
* Everything else uses only the .NET Framework (System.Net.Sockets, System.Threading, System.Security.Cryptography,
  System.Windows.Forms, System.Drawing, Microsoft.Win32 for the link registration).
* The networking follows the course notes (Chapter "TCP and UDP Programming", *Multi-Client Servers*).
* The code was developed with the help of an AI assistant (Claude). Both members must be able to explain all of it.

## 13. Group members

| Member | Reg. No. | Responsibilities |
|---|---|---|
| James Wafula | _fill in_ | Server, protocol, database, security, game rules |
| Moritz _surname_ | _fill in_ | Client UI: start screen, player game screen, calculator, active board, admin screens |
