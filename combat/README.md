# Bannerlord Combat Bridge - prototype

Separate, optional single-player module for Bannerlord **v1.4.8.119303**. It can
coexist with Bannerlord Strategic Bridge. It does not change campaign bridge
commands, save data, character stats, damage, or battle difficulty.

This version exposes character input, native troop orders, and a bounded local
melee pilot. A connected assistant can choose tactics and run short goals while
the local pilot handles movement, facing, swings and defensive reactions.
See [the pilot guide](PILOT.md) for agent instructions and limitations. This is
an experimental prototype; see the repository's
[live-test status](https://github.com/Grosspa1/Bannerlord-AI-Control/blob/feature/realtime-combat-control/docs/REALTIME_COMBAT.md).

## Install and try

1. Close Bannerlord and its launcher before installing a new DLL.
2. Extract `BannerlordCombatBridge-v0.1.0.zip` into the game's `Modules` folder.
   The result must be `Modules/BannerlordCombatBridge/SubModule.xml`, with its DLL
   under `bin/Win64_Shipping_Client`.
3. Enable **Bannerlord Combat Bridge (Prototype)** in the launcher after Native
   and SandBoxCore. The strategic bridge is optional.
4. Start a **single-player Custom Battle**, choose a character on foot, and finish
   deployment. Keep the battle focused with menus closed.
5. Use the included `combatctl.py` through Python 3 or a local assistant. Use one
   command producer at a time. Launch the client without taking focus away from
   the game, or start it externally through the assistant.

You can perform launcher/battle setup yourself. An assistant needs an available
desktop connector to do that UI setup and focus the game; it needs local file
access and command execution to operate the bridge and pilot once the battle is
ready. An unavailable or disabled connector must be reported, not bypassed.
If Local Commander reports that the local operator disabled agent control, the
normal local operator enable control must be used at the user's explicit request
before agent control resumes. For Local Commander, run the clients from the
approved source checkout rather than using the protected live installation as
the working directory; see the exact tool arguments in [PILOT.md](PILOT.md).

For a local terminal, from the installed module directory (adjust for a different
install; Local Commander users should use the source-checkout instructions above):

```powershell
Set-Location -LiteralPath 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\BannerlordCombatBridge'
python combatctl.py status
python combatctl.py input --forward 1 --seconds 0.5
python combatctl.py input --strafe -1 --seconds 0.5
python combatctl.py input --block up --seconds 0.5
python combatctl.py input --attack right --seconds 0.3
python combatctl.py input --jump --seconds 0.1
python combatctl.py release
```

If `status` reports stale state while the game is closed or loading, start the
game with the module enabled and finish loading a battle before checking again.
A fresh main-menu state can report no active mission. Run actions only after
fresh state reports an eligible battle; the stale-state guard remains required.

`--yaw` and `--pitch` are absolute world angles in radians. Omit them to retain
the character's current aim. Yaw zero faces +Y, positive yaw turns toward -X;
positive pitch looks up. `--target INDEX` faces an enemy from the current
nearby-enemy sample. It does not navigate, assess line of sight, or guarantee a
hit. The camera following programmatic aim still needs in-game verification.

Attack/block directions are `up`, `down`, `left`, and `right`. Attack is held
for the interval, then released as ordinary input; already committed swings
cannot be undone. Manually equip a suitable weapon first. Weapon switching,
horse control and siege engines are not included. The separate `combatpilot.py`
client can select a nearby melee target and react during a bounded goal.

For formations, first obtain the actual commandable formation `index` from
`status`. Examples using index 0 (substitute the returned index):

```powershell
python combatctl.py order 0 charge
python combatctl.py order 0 stop
python combatctl.py order 0 shield_wall
python combatctl.py order 0 line
python combatctl.py order 0 hold_fire
python combatctl.py order 0 fire_at_will
python combatctl.py order 0 move 123.5 456.0
```

Movement destinations use scene coordinates and must pass native battlefield
and navigation checks. Orders apply only to nonempty formations already owned
by the player. Mounted commanders may issue orders; direct character input
currently requires being on foot. Orders use the native player controller,
including normal AI delegation changes, and persist after the client exits.
Releasing the bridge does not cancel an already issued formation order.

## Return control

**F10 stops bridge control immediately and latches it off. F9 clears that latch;
a fresh command must then explicitly enable control.** Input also expires when
its short lease ends, the game loses focus, a menu opens, the character changes
or dies, or the mission ends. The included client renews input only during its
requested interval (0.05 to 10 seconds), then releases it. A paused game is still
checked by an application-tick watchdog. No client response means an unknown
outcome, not permission to repeat the same action automatically.

## Development

Development tests are optional and are **not required to play**. The installed
ZIP does not include `combat/tests`, `build.ps1`, or the source tree. Run the
following only from the source checkout, which is located on this PC at
`C:\Users\Public\BannerlordControllerBuild-combat`:

```powershell
Set-Location -LiteralPath 'C:\Users\Public\BannerlordControllerBuild-combat'
.\combat\build.ps1
.\combat\tests\Test-Combat.ps1
```

Builds use `/nostdlib+`, the game's bundled Mono 4.7.1 reference APIs, the bundled
.NET Standard 2.0 facade and installed TaleWorlds assemblies. They create a ZIP
under `combat/out`; they do not deploy into the game. The offline C# suites run
under Windows .NET Framework, not the game's Mono host.

Detailed command protocol and acceptance checklist: `docs/REALTIME_COMBAT.md`
in the source repository.
