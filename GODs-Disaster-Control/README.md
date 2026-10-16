# GODs Disaster Control

Version 0.14.11 for Cities: Skylines II.

GODs Disaster Control adds a compact in-game disaster launcher with precision map targeting, repeat deployment, selectable reticles, and seven destructive events.

## Events

- **Lightning Strike** — repeatable precision strikes against terrain, buildings, vehicles, and citizens.
- **Earthquake** — wide-area structural damage and destruction.
- **Hailstorm** — native citywide hail with controlled damage pulses.
- **Wildfire** — native fire ignition and spread.
- **Meteor Strike** — a descending meteor with impact destruction and surrounding fires.
- **Missile Strike** — a compact precision strike with localized destruction and fire.
- **Nuclear Strike** — an ICBM, expanding blast dome, mass destruction, fires, extreme pollution, impact artwork, and an optional permanent terrain crater.

## Controls

- Press **Ctrl+G** to open or close the disaster panel.
- Select an event, then left-click the map to deploy it.
- Targeted events remain armed for repeated strikes.
- Right-click, press **Escape**, or select **Cancel** to leave targeting mode.
- Choose **Auto**, **Cross**, or **Rings** under Target Reticle.
- Toggle **Terrain Crater** under Nuclear Strike. The setting is remembered between sessions.

## Important warning

These events intentionally damage or destroy city assets. Back up important saves before use.

The nuclear terrain crater permanently deforms the current save unless it is reloaded. Roads and other networks are not terraformed and may remain suspended over the crater.

## Installation

Install through Paradox Mods from the Cities: Skylines II launcher. Enable the code mod, start the game, and look for **DISASTERS** in the Universal Mod Menu.

## Local development build

1. Rebuild `GODsDisasterControl` in **Release** configuration.
2. From the `UI` folder, run `npm install` once, then `npm run build`.
3. Launch the game and verify the log begins with `GODs Disaster Control v0.14.11 loading`.

## Known limitations

- Cities: Skylines II does not provide a native earthquake presentation system, so the earthquake focuses on structural damage.
- The nuclear strike applies extreme supported air and ground pollution; water pollution is not exposed by the current implementation.
- Terrain deformation does not delete or rebuild road and network topology.
- Visual effects are subject to the game's camera, culling, and rendering behavior.

## Credits

Created by GOD.
