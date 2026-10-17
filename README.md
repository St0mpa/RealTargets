# RealTargets

Realistic blood, wounds and gore for SPT (Single Player Tarkov). Every bullet that enters a body leaves a mark
that follows real ballistics: where it went in, where it came out, how much energy it carried and what was
behind the target.

Purely visual: damage, bleeding HP loss, death and AI stay vanilla EFT (and SAIN, if installed).

**Download:** [The Forge](https://sp-mod.com/mod/3165/realtargets) or [GitHub Releases](https://github.com/St0mpa/RealTargets/releases).

## Features
- **Entry and exit wounds** on clothing and skin, placed where each round actually went through. Exits are
  bigger and ragged, scaled by the bullet's energy.
- **Bleeding per wound**: drips, blood running down the clothing, and spurts when a big artery is hit (neck, groin,
  thigh, upper arm). Heartbeat-paced, fading out over the last beats after death, then draining by gravity.
- **Exit spray, blood mist and wall spatter** on whatever stands behind a through-shot.
- **Pools** under bodies, sized by the blood that ran out, drying darker over time. **Bloody footprints** from
  anyone who walks through a wet pool.
- **Dismemberment**, on kills and corpses only, never on a living bot:
  - Heads come apart from close-range buckshot, slugs, 12.7 mm and similar, and grenades at very close range.
  - High-energy rifle rounds (7.62x51, 7.62x54R and others) burst the skull open at the exit.
  - Repeated hits on a dead body's head add up.
  - Limbs come off from 12.7 mm rifle/MG rounds and grenades.
  - Gibs fly, stick to walls and slide down them. Helmets get thrown off.
- **Corpse impacts**: bullets push dead bodies by their momentum, and grenade blasts throw nearby corpses.
- No blood on doors, windows or other moving objects, so nothing hangs in mid-air when a door opens.
- Lit by the map's own lighting: dark in dark rooms, wet sheen in the light.

## Install
1. Install SPT 4.1.6.
2. Extract the zip into your SPT folder (the one with `EscapeFromTarkov.exe`). You should end up with
   `BepInEx/plugins/RealTargets/RealTargets.dll`.
3. Start the game. The config file `BepInEx/config/com.stompa.realtargets.cfg` is created on the first run.

Uninstall: delete the `BepInEx/plugins/RealTargets` folder (and the config file if you like).

## Configuration
Press **F12** in game (SPT's Configuration Manager) and open **stompa-RealTargets**. Every effect can be
switched off on its own, and the thresholds can be tuned:
- **General / Enabled**: master switch.
- **Blood**: blood volume, bleed rate, arterial chance, the last heartbeats after death, how long corpses keep draining.
- **Visuals**: each blood effect on or off, decal shader, max distance (80 m).
- **Gore**: dismemberment and gibs on or off. Energy thresholds for heads, skull bursts and limbs. Shotgun range
  and how many pellets must hit the head.
- **Impacts**: corpse pushes and blast throws.
- **Hit model**: armour stop threshold, energy thresholds, temporary cavity size.
- **Flinch** (off by default): living bots flinch per limb through EFT's own hit-reaction rig.
- **Debug**: log hits, blood and wound placement to the BepInEx log (all off by default; turn on for a bug report).

## Compatibility
- **SPT 4.1.6** (tested; later 4.1.x patches should work). Client-only: no server mod.
- **Dependencies**: none beyond SPT.
- Works alongside **Ragdoll Kinetics** and **SAIN**.
- **Fika is not supported.**
- Performance: the mod's own CPU time averages about 0.1 ms per frame in a busy raid. Blood marks, pools and
  gore pieces have fixed caps, so a long raid doesn't pile them up.

## Building from source
Requires the .NET SDK (8 or newer) and an SPT 4.1.x install. Game assemblies are referenced in place from the
install and are not part of this repository.
```
dotnet build src/RealTargets.Client/RealTargets.Client.csproj -c Public -p:SptDir="C:\path\to\SPT"
dotnet build src/RealTargets.Client/RealTargets.Client.csproj -c Public -p:SptDir="C:\path\to\SPT" -p:Package=true
```
The first builds `bin/Public/RealTargets.dll`. The second also writes the install-ready zip to `dist/`.

## Credits
- Built on [SPT](https://sp-tarkov.com), [BepInEx](https://github.com/BepInEx/BepInEx) and
  [Harmony](https://github.com/pardeike/Harmony).

## License
MIT, see `LICENSE`. Copyright (c) 2026 Stompa.

## Support
Bug reports: the comments on the [Forge page](https://sp-mod.com/mod/3165/realtargets). Include your
`BepInEx/LogOutput.log` with the Debug logs turned on.
