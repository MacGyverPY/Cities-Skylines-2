import React, { useEffect, useRef, useState } from "react";
import { ModRegistrar } from "cs2/modding";
import { bindValue, trigger, useValue } from "cs2/api";
import styles from "./style.module.scss";
import godIcon from "./images/iconGOD.png";

const GROUP = "GODsDisasterControl";
let setPaletteOpen: ((open: boolean) => void) | null = null;

const targetingBinding = bindValue<boolean>(GROUP, "targetingActive", false);
const selectedBinding = bindValue<string>(GROUP, "selectedDisaster", "");
const statusBinding = bindValue<string>(GROUP, "status", "Select a disaster.");
const coordinatesBinding = bindValue<string>(GROUP, "coordinates", "Move over the map.");
const targetDetailsBinding = bindValue<string>(GROUP, "targetDetails", "Aim at a building to inspect lightning damage.");
const reticleStyleBinding = bindValue<string>(GROUP, "reticleStyle", "auto");
const nuclearCraterBinding = bindValue<boolean>(GROUP, "nuclearCraterEnabled", true);
const RETICLE_STORAGE_KEY = "GODsDisasterControl.reticleStyle";
const NUCLEAR_CRATER_STORAGE_KEY = "GODsDisasterControl.nuclearCraterEnabled";

type ReticleStyle = "auto" | "crosshair" | "rings";

type Disaster = {
    id: string;
    label: string;
    icon: string;
    category: "Natural" | "GODs Weapons";
    danger?: boolean;
};

const disasters: Disaster[] = [
    { id: "lightning", label: "Lightning Strike", icon: "LT", category: "Natural" },
    { id: "earthquake", label: "Earthquake", icon: "EQ", category: "Natural" },
    { id: "hailstorm", label: "Hailstorm", icon: "HL", category: "Natural" },
    { id: "wildfire", label: "Wildfire", icon: "WF", category: "Natural" },
    { id: "meteor", label: "Meteor Strike", icon: "MT", category: "Natural", danger: true },
    { id: "missile", label: "Missile Strike", icon: "MS", category: "GODs Weapons", danger: true },
    { id: "nuclear", label: "Nuclear Strike", icon: "NU", category: "GODs Weapons", danger: true }
];

const LauncherButton = () => {
    const open = () => {
        setPaletteOpen?.(true);
    };

    return (
        <button className={styles.launcher} onClick={open} title="GODs Disaster Control">
            <img src={godIcon} alt="GODs Disaster Control" />
            <span>DISASTERS</span>
        </button>
    );
};

const DisasterPalette = () => {
    const [open, setOpen] = useState(false);
    const openRef = useRef(false);
    const updatePanelOpen = (value: boolean) => {
        openRef.current = value;
        setOpen(value);
        trigger(GROUP, "setPanelOpen", value);
    };
    const targeting = useValue(targetingBinding);
    const selected = useValue(selectedBinding);
    const status = useValue(statusBinding);
    const coordinates = useValue(coordinatesBinding);
    const targetDetails = useValue(targetDetailsBinding);
    const reticleStyle = useValue(reticleStyleBinding) as ReticleStyle;
    const nuclearCraterEnabled = useValue(nuclearCraterBinding);
    setPaletteOpen = updatePanelOpen;

    useEffect(() => {
        let saved: ReticleStyle = "auto";
        try {
            const candidate = localStorage.getItem(RETICLE_STORAGE_KEY);
            if (candidate === "crosshair" || candidate === "rings") saved = candidate;
        } catch { /* Cohtml storage can be unavailable during early startup. */ }
        trigger(GROUP, "setReticleStyle", saved);
        let savedCrater = true;
        try {
            savedCrater = localStorage.getItem(NUCLEAR_CRATER_STORAGE_KEY) !== "false";
        } catch { /* Default to enabled. */ }
        trigger(GROUP, "setNuclearCraterEnabled", savedCrater);

    }, []);

    useEffect(() => {
        // Cohtml versions can expose different keyboard event fields.
        let gHeld = false;
        const isG = (event: KeyboardEvent) =>
            (typeof event.key === "string" && event.key.toLowerCase() === "g") ||
            event.code === "KeyG" || event.keyCode === 71;
        const onKeyDown = (event: KeyboardEvent) => {
            if (!isG(event)) return;
            if (gHeld || event.repeat) return;
            gHeld = true;
            if (!event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) return;
            const target = event.target as HTMLElement | null;
            if (target && (target.isContentEditable ||
                /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName))) return;
            event.preventDefault();
            event.stopPropagation();
            updatePanelOpen(!openRef.current);
        };
        const onKeyUp = (event: KeyboardEvent) => { if (isG(event)) gHeld = false; };
        const onBlur = () => { gHeld = false; };
        window.addEventListener("keydown", onKeyDown, true);
        window.addEventListener("keyup", onKeyUp, true);
        window.addEventListener("blur", onBlur);
        return () => {
            window.removeEventListener("keydown", onKeyDown, true);
            window.removeEventListener("keyup", onKeyUp, true);
            window.removeEventListener("blur", onBlur);
            setPaletteOpen = null;
        };
    }, []);

    const close = () => {
        updatePanelOpen(false);
    };

    const arm = (id: string) => {
        updatePanelOpen(false);
        trigger(GROUP, "armDisaster", id);
    };

    const cancel = () => trigger(GROUP, "cancelTargeting", "cancel");

    const setReticle = (style: ReticleStyle) => {
        try { localStorage.setItem(RETICLE_STORAGE_KEY, style); } catch { /* Keep the runtime choice. */ }
        trigger(GROUP, "setReticleStyle", style);
    };

    const setNuclearCrater = (enabled: boolean) => {
        try { localStorage.setItem(NUCLEAR_CRATER_STORAGE_KEY, String(enabled)); } catch { /* Keep runtime choice. */ }
        trigger(GROUP, "setNuclearCraterEnabled", enabled);
    };

    return (
        <>
            {open && (
                <div className={styles.palette}>
                    <header>
                        <div>
                            <strong>GODs DISASTER CONTROL</strong>
                            <small>SELECT AN EVENT</small>
                        </div>
                        <button type="button" onMouseDown={close} className={styles.close}>X</button>
                    </header>

                    {(["Natural", "GODs Weapons"] as const).map(category => (
                        <section key={category}>
                            <h3>{category}</h3>
                            <div className={styles.grid}>
                                {disasters.filter(x => x.category === category).map(disaster => (
                                    <button
                                        type="button"
                                        key={disaster.id}
                                        className={disaster.danger ? styles.danger : styles.disaster}
                                        onMouseDown={() => arm(disaster.id)}
                                    >
                                        <span className={styles.eventIcon}>{disaster.icon}</span>
                                        <span>{disaster.label}</span>
                                    </button>
                                ))}
                            </div>
                        </section>
                    ))}
                    <section className={styles.settings}>
                        <h3>Target Reticle</h3>
                        <div className={styles.reticleChoices}>
                            {([
                                ["auto", "AUTO"],
                                ["crosshair", "CROSS"],
                                ["rings", "RINGS"]
                            ] as [ReticleStyle, string][]).map(([value, label]) => (
                                <button
                                    type="button"
                                    key={value}
                                    className={reticleStyle === value ? styles.reticleSelected : styles.reticleChoice}
                                    onMouseDown={() => setReticle(value)}
                                >
                                    <span>{value === "crosshair" ? "+" : value === "rings" ? "◎" : "A"}</span>
                                    {label}
                                </button>
                            ))}
                        </div>
                        <small>AUTO uses a precision cross for lightning/missiles and area rings for larger events.</small>
                    </section>
                    <section className={styles.settings}>
                        <h3>Nuclear Strike</h3>
                        <div className={styles.optionRow}>
                            <div>
                                <strong>Terrain Crater</strong>
                                <small>Creates the permanent −58 m impact crater.</small>
                            </div>
                            <button
                                type="button"
                                className={nuclearCraterEnabled ? styles.toggleOn : styles.toggleOff}
                                onMouseDown={() => setNuclearCrater(!nuclearCraterEnabled)}
                            >
                                {nuclearCraterEnabled ? "ON" : "OFF"}
                            </button>
                        </div>
                    </section>
                    <footer>{status}</footer>
                    <div className={styles.hotkeyHint}>CTRL+G · OPEN/CLOSE</div>
                </div>
            )}

            {targeting && (
                <div className={styles.targetBanner}>
                    <div className={styles.reticle}>◎</div>
                    <div>
                        <strong>TARGET MODE: {selected.toUpperCase()}</strong>
                        <small>{coordinates}</small>
                        {selected === "lightning" && <small className={styles.targetDetails}>{targetDetails}</small>}
                        <small>{selected === "hailstorm" ? "Citywide event · Cancel: stop early" : "Left-click: confirm · Right-click/Escape: cancel"}</small>
                    </div>
                    <button type="button" onMouseDown={cancel}>CANCEL</button>
                </div>
            )}

            {!open && !targeting && (status.startsWith("TEST TARGET CONFIRMED") || status.startsWith("LIGHTNING STRIKE DEPLOYED")) && (
                <div className={styles.resultBanner}>{status}</div>
            )}
        </>
    );
};

const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.append("UniversalModMenu", LauncherButton);
    moduleRegistry.append("Game", DisasterPalette);
};

export default register;
