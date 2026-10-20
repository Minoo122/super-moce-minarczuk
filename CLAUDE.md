# CLAUDE.md

jRandomSkills is a CounterStrikeSharp plugin for CS2: at the start of every round each player is given a random "skill" (154+ of them). This repository is a fork. Its remotes are `origin` (Minoo122/super-moce-minarczuk) and `bydexter` (ByDexterTR/jRandomSkills, the upstream that gets merged in). Work happens on `develop`, and `main` is the PR target.

## Layout

```
jRandomSkills - SRC Files/        C# plugin project (jRandomSkills.csproj / .sln)
  src/jRandomSkills.cs            BasePlugin entry: Load/Unload, SkillAction reflection dispatcher, SkillData, jSkill_PlayerInfo
  src/player/ISkill.cs            ISkill interface (every hook a skill can implement) + the `Skills` enum
  src/player/skills/*.cs          One file per skill (class name == enum member name)
  src/player/{Player,Round,Entity}Events.cs  Game event / hook wiring, dispatch to skills
  src/player/PlayerOnTick.cs      Tick listener (HUD), map start/end
  src/player/Debug.cs, PerfLog.cs Debug + perf logs → <plugin>/logs/debug_*.txt, perf_*.txt
  src/command/                    Chat/console commands (aliases come from config) + VoteSystem
  src/utils/                      Config, SkillsInfo, Localization, PlayerManager, SkillUtils (big helper grab-bag),
                                  EntityManager, RayTrace, RarityManager, WeaponPool, ...
  src/lang/*.json                 Translations (en, pl, de, fr, pt-br, ru, tr, zh)
  src/gamedata/                   jRandomSkills.gamedata.json (native signatures)
WASDMenuAPI - SRC Files/          Bundled menu library, referenced as a ProjectReference
jRandomSkills - Server Files/     Ready-to-deploy server layout (DLLs, configs, languages, gamedata), committed
README.md / README-PL.md          User docs: skill table + long changelog (EN and PL)
updater.json                      Release feed metadata
.github/                          Contributor/thanks workflow (manual dispatch) + update_thanks.py
```

The directory names contain spaces, so always quote paths.

## Build

```bash
dotnet build "jRandomSkills - SRC Files/jRandomSkills.sln"            # Debug
dotnet build "jRandomSkills - SRC Files/jRandomSkills.sln" -c Release
```

- Targets **net10.0**, with `Nullable` and `AllowUnsafeBlocks` enabled. Packages: CounterStrikeSharp.API, MaxMind.Db and Newtonsoft.Json.
- Post-build targets in the csproj **copy** the built DLLs and PDBs, `src/lang/*` and `src/gamedata/*` into `jRandomSkills - Server Files/`. A build therefore changes tracked files under Server Files (`jRandomSkills.dll`, among others). Expect those diffs, and commit them when shipping a build (as in "feat: cowboy build").
- There are no tests. A build that succeeds is the only automated check. Behaviour can only be verified on a real CS2 server.

## How skills work

Skills are **static classes** found by reflection. Nothing is registered by hand except the enum member.

- `jRandomSkills.SkillAction(skillName, methodName, args)` looks up `src.player.skills.<skillName>`, which must implement `ISkill`, and invokes the **public static** method `methodName` on it. Results are cached. When the method is missing, the call does nothing. Skills implement only the hooks they need, using the exact names and signatures from `ISkill`.
- Lifecycle: `LoadSkill` (on plugin load, if `active`), then `EnableSkill(player)` when the skill is drawn, `UseSkill` / `TypeSkill` for activated or typed skills, and `DisableSkill(player)` when the round resets, the player dies or the player disconnects. `NewRound` runs for every skill used on the current map, and on map change for every skill. `RoundEnd` also exists.
- Game events are dispatched only to skills **currently held by a non-drawing player**, each skill once (`DispatchToActiveSkills`). Exceptions are caught and logged per skill. Inside a handler, check that the player involved actually holds your skill (`PlayerManager.GetPlayer(controller)?.Skill == skillName`).
- `OnTick` runs only for skills held this tick, and it is skipped during freeze time when `disableOnFreezeTime` is set. Keep it cheap. Skills reuse static buffers such as `PlayerManager.FillSkillHolders(skill, buffer)`.
- Hooks that return `bool` (`PlayerHurtPre`, `WeaponDrop`, `OnWeaponCanAcquire`) let a skill block or override the default behaviour.

### Adding a new skill (checklist)

1. Add a member to the `Skills` enum in [src/player/ISkill.cs](jRandomSkills%20-%20SRC%20Files/src/player/ISkill.cs). Append it at the end: the order is otherwise roughly alphabetical, but the numeric values matter.
2. Create `src/player/skills/<Name>.cs` in namespace `src.player.skills`, with `public class <Name> : ISkill`, `private const Skills skillName = Skills.<Name>;` and a `LoadSkill` that calls `SkillUtils.RegisterSkill(skillName, SkillsInfo.GetValue<string>(skillName, "color"));`.
3. Add a nested `public class SkillConfig(...) : SkillsInfo.DefaultSkillInfo(...)` with defaults for every constructor parameter. Copy the parameter list from an existing skill such as [Assassin.cs](jRandomSkills%20-%20SRC%20Files/src/player/skills/Assassin.cs). `SkillsInfoModel` discovers every `SkillConfig` by reflection. Read values with `SkillsInfo.GetValue<T>(skillName, "propertyName")`, which is case-insensitive.
4. Add translation keys `"<name lowercased>"` and `"<name lowercased>_desc"`, plus any extra messages as `<name>_*`, to **every** `src/lang/*.json`.
5. Optionally add the skill to the README skill table and changelog, in both EN and PL.

Some skills are also special-cased by name in the core. Examples: the curse skills in `SkillUtils.IsCurseSkill`, the `lateDamageSkills` list, `Fortnite` and `NoRecoil` in the event and map code. Grep for an existing skill's name before assuming a skill is self-contained.

## Configs and runtime files

- `configs/config.json` (`utils/Config.cs`, `SettingsModel`) holds game mode, language, command aliases and permissions, HUD and voting settings, and `DebugMode`.
- `configs/skillsInfo.json` (`utils/SkillsInfo.cs`) holds per-skill settings (`DefaultSkillInfo` fields + skill-specific ones).
- Both files are loaded at plugin load and rewritten with defaults filled in when keys are missing. The copies in `Server Files/.../configs/` are committed defaults.
- `DebugMode` is a decimal-digit flag set: 1=Skill, 2=Round, 3=Entity, 4=Damage, so `1234` enables all of them (`utils/DebugCategory.cs`). Log with `Debug.WriteToDebug(msg, DebugCategory.X)`.

## Conventions and gotchas

- Do not hold `CCSPlayerController` references across ticks or timers. Store `player.Key()` (uint) and re-resolve with `PlayerManager.GetController(key)`, checking `IsValid` / `PawnIsAlive`. See the timer in `Cowboy.EnableSkill`.
- Use `Instance.IsPlayerValid(player)` for "valid and alive" checks. For entities spawned by skills, use `SkillUtils.SafeKillEntity` and `EntityManager` so they are cleaned up on round or map reset.
- `DisableSkill` and `NewRound` are called from reset loops. Never let them throw (Cowboy wraps its body in try/catch).
- Network state edits need `Utilities.SetStateChanged(entity, "Class", "m_field")`.
- Attacker-side damage modifiers must guard friendly fire with `SkillUtils.IsFriendlyFireBlocked(...)`: the damage hook runs before the engine zeroes teammate damage.
- Use `Instance.AddTimer(..., TimerFlags.STOP_ON_MAPCHANGE)` for delayed actions and `Server.NextFrame` for the next-frame follow-ups.
- Style: file-scoped or block namespaces both appear, and you should match the file you edit. Statics live on `using static src.jRandomSkills;` (`Instance`). Comments are sparse and explain *why*. Some generated and suppression files have Polish comments.
- Bump `ModuleVersion` in [src/jRandomSkills.cs](jRandomSkills%20-%20SRC%20Files/src/jRandomSkills.cs) for releases. On startup the plugin compares it against the latest upstream GitHub release.
