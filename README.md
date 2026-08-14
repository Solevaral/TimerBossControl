# BossGate — sequential boss unlocking for TShock

A plugin for **TShock 6.1.0** (Terraria 1.4.5.6, .NET 9).

Bosses unlock one at a time: every N real-world hours (72 = 3 days by default), the next
boss in a configured order becomes available. A locked boss cannot be summoned by any
means — not with an item, not through natural spawning, not through a cheating client.

## How it works

All the logic runs **server-side only**. The client is never trusted:

| Summon path | What the plugin does |
|---|---|
| Summon item (packet 61 `SpawnBossorInvasion`) | The packet is cancelled before processing — the item is not consumed, a copy is returned to the player, and they get a whisper explaining why |
| Natural night spawn (Eye of Cthulhu, mechanical bosses) | Intercepted in `NpcSetDefaults` — the NPC is never created in the first place |
| Event/ritual spawns (Cultist, Moon Lord), multi-segment bosses (Eater of Worlds) | Every NPC ID belonging to a boss, including segments and body parts, is listed in the config and blocked the same way |
| Bypass via third-party plugins / unusual spawn paths | Once a second, `Main.npc` is swept and any locked boss found is despawned before it syncs |
| Wall of Flesh and the hardmode transition | A dedicated `BlockWallOfFleshHardmode` flag blocks dropping the Guide Voodoo Doll and reverts `Main.hardMode` if it gets flipped anyway |

The timer counts **real time in UTC** and keeps ticking even while the server is offline:
absolute timestamps are stored in the database, and on startup the plugin catches up on any
unlocks that were missed. State lives in the TShock database (SQLite by default:
`tshock/tshock.sqlite`, table `BossGateState`) and is scoped to the **world ID**, so multiple
worlds on one server don't interfere with each other.

## Installation

1. Download the prebuilt DLL: **[dist/BossGate.dll](https://github.com/Solevaral/TimerBossControl/raw/main/dist/BossGate.dll)**
   (or grab it from [Releases](https://github.com/Solevaral/TimerBossControl/releases), or build it yourself — see below).
2. Drop the file into your TShock server's `ServerPlugins/` folder. Nothing else needs to
   be copied — the plugin uses assemblies already present on the server.
3. Restart the server — `tshock/BossGate.json` will be created automatically with defaults.
4. Grant your admin group the `bossgate.admin` permission:

```bash
/group addperm admin bossgate.admin
```

### Building from source

```bash
dotnet build BossGate/BossGate.csproj -c Release
```

The built `BossGate.dll` will appear in `BossGate/bin/Release/`.

If the `TShock 6.1.0` NuGet package isn't available in your feed, open
`BossGate/BossGate.csproj`, comment out the `PackageReference` block and uncomment the
`Reference`-to-DLL block instead, then build with a path to your server:

```bash
dotnet build BossGate/BossGate.csproj -c Release -p:TShockPath=C:\TShock
```

## Config — `tshock/BossGate.json`

| Field | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Fully turns the restrictions on/off |
| `UnlockIntervalHours` | `72` | Real-world hours between unlocking the next boss |
| `BlockWallOfFleshHardmode` | `true` | Extra guard against premature hardmode (see table above) |
| `AnnounceHourBefore` | `true` | Chat reminder one hour before the next unlock |
| `LogBlockedAttempts` | `true` | Logs every blocked summon attempt to the console |
| `RequireUsePermission` | `false` | Requires the `bossgate.use` permission for `/bosses` and `/bosstime` |
| `Bosses` | vanilla progression | The boss order, see below |
| `Messages` | Russian by default | All player-facing messages |

### Boss order

The order of the `Bosses` array is the unlock order. The first boss is available as soon as
the timer starts. A single entry can bundle several bosses together — they then unlock at
the same time (used for Eater of Worlds / Brain of Cthulhu, and for the three mechanical
bosses).

```jsonc
{
  "Key": "eow_boc",
  "DisplayName": "Eater of Worlds / Brain of Cthulhu",
  // every relevant NPC id, including worm segments and creepers
  "NpcIds": [13, 14, 15, 266, 267],
  // "boss NPC id": "summon item id" — the item is returned to the player when blocked
  "SummonItems": { "13": 70, "266": 1331 }
}
```

Default progression:

1. King Slime
2. Eye of Cthulhu
3. Eater of Worlds / Brain of Cthulhu
4. Queen Bee
5. Skeletron
6. Deerclops
7. Wall of Flesh
8. Queen Slime
9. Mechanical bosses (The Twins / The Destroyer / Skeletron Prime)
10. Plantera
11. Golem
12. Duke Fishron
13. Empress of Light
14. Lunatic Cultist
15. Moon Lord

### Messages

`Messages` supports `{0}`, `{1}` placeholders (documented in the comments of
`BossGateConfig.cs`) and Terraria color tags like `[c/ff5555:text]`. A malformed template
never crashes the plugin — the message is just sent as-is.

## Commands

| Command | Permission | What it does |
|---|---|---|
| `/bosses` | — (or `bossgate.use`) | List of bosses: unlocked and locked, plus time to the next |
| `/bosstime` | — (or `bossgate.use`) | Time remaining until the next unlock |
| `/bossunlock` | `bossgate.admin` | Manually unlock the next boss (restarts the timer) |
| `/bosslock <n>` | `bossgate.admin` | Roll the counter back by `n` bosses |
| `/bossreload` | `bossgate.admin` | Reload the config without restarting the server |

Permissions are plain TShock permission strings, so they work alongside Permissions++ or
any other group manager.

### Timer control — `/boss`

Everything above is also available as one command with subcommands, plus timer control:

| Command | Permission | What it does |
|---|---|---|
| `/boss` | — | Shows the subcommand help |
| `/boss list` | — | Same as `/bosses` |
| `/boss time` | — | Same as `/bosstime` |
| `/boss addtime 1h 30m` | `bossgate.admin` | Pushes the next unlock back by the given duration |
| `/boss removetime 1h` | `bossgate.admin` | Brings the next unlock closer by the given duration |
| `/boss timestop` | `bossgate.admin` | Pauses the timer |
| `/boss timestart` | `bossgate.admin` | Resumes the timer from the same remaining time |
| `/boss unlock` / `/boss lock <n>` / `/boss reload` | `bossgate.admin` | Same as the standalone commands above |

Duration format: `1h`, `30m`, `10s`, `2d` and any combination of them — `1h 30m 10s`,
`1h30m`. Russian suffixes also work: `1ч 30м`. Anything that doesn't parse as a duration is
rejected with a usage hint instead of being applied.

Every one of these actions is **broadcast to all online players**: how much time was
added or removed, whether the timer was paused or resumed, and how much time is left.

While the timer is paused:

* time does not pass — skipped hours are not "caught up" once resumed;
* `/bosstime` and `/boss time` show the frozen remaining time along with a note that the
  timer is paused;
* the one-hour reminder is not sent;
* `addtime` / `removetime` adjust that same frozen remaining time.

The pause state is stored in the database alongside the remaining time, so it survives a
server restart.

## Announcements

* On unlock — a broadcast to all online players naming the new boss.
* One hour before unlock — a chat reminder (`AnnounceHourBefore`).
* Unlocks that happened while the server was offline are applied silently and logged.

## Fault tolerance

* A broken or missing JSON config → the plugin logs the reason to the console and falls
  back to defaults.
* Invalid values (`UnlockIntervalHours <= 0`, a boss with no `NpcIds`, a missing `Messages`
  section) are automatically fixed on load.
* Database init failure → the restrictions are **disabled**, the server keeps running.
* Every hook and packet parser is wrapped in `try/catch` — the plugin cannot crash the
  server.

## License

MIT, see [LICENSE](LICENSE).
