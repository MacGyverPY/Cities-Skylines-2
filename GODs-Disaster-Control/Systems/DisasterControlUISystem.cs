using System;
using Colossal.UI.Binding;
using Game;
using Game.UI;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace GODsDisasterControl.Systems
{
    /// <summary>
    /// UI bridge for the reusable world-targeting tool and disaster actions.
    /// </summary>
    public sealed partial class DisasterControlUISystem : UISystemBase
    {
        internal const string Group = "GODsDisasterControl";

        private ValueBinding<bool> m_PanelOpen;
        private ValueBinding<bool> m_TargetingActive;
        private ValueBinding<string> m_SelectedDisaster;
        private ValueBinding<string> m_Status;
        private ValueBinding<string> m_Coordinates;
        private ValueBinding<string> m_TargetDetails;
        private ValueBinding<string> m_ReticleStyle;
        private ValueBinding<bool> m_NuclearCraterEnabled;
        private DisasterTargetToolSystem m_TargetTool;

        public override GameMode gameMode => GameMode.Game;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            AddBinding(m_PanelOpen = new ValueBinding<bool>(Group, "panelOpen", false));
            AddBinding(m_TargetingActive = new ValueBinding<bool>(Group, "targetingActive", false));
            AddBinding(m_SelectedDisaster = new ValueBinding<string>(Group, "selectedDisaster", string.Empty));
            AddBinding(m_Status = new ValueBinding<string>(Group, "status", "Select a disaster."));
            AddBinding(m_Coordinates = new ValueBinding<string>(Group, "coordinates", "Move over the map."));
            AddBinding(m_TargetDetails = new ValueBinding<string>(Group, "targetDetails", "Aim at a building to inspect lightning damage."));
            AddBinding(m_ReticleStyle = new ValueBinding<string>(Group, "reticleStyle", "auto"));
            AddBinding(m_NuclearCraterEnabled = new ValueBinding<bool>(Group, "nuclearCraterEnabled", true));

            AddBinding(new TriggerBinding<bool>(Group, "setPanelOpen", SetPanelOpen));
            AddBinding(new TriggerBinding<string>(Group, "armDisaster", ArmDisaster));
            AddBinding(new TriggerBinding<string>(Group, "cancelTargeting", _ => CancelTargeting()));
            AddBinding(new TriggerBinding<string>(Group, "setReticleStyle", SetReticleStyle));
            AddBinding(new TriggerBinding<bool>(Group, "setNuclearCraterEnabled", SetNuclearCraterEnabled));

            m_TargetTool = World.GetOrCreateSystemManaged<DisasterTargetToolSystem>();
            m_TargetTool.TargetMoved += OnTargetMoved;
            m_TargetTool.TargetConfirmed += OnTargetConfirmed;
            m_TargetTool.TargetCancelled += OnTargetCancelled;
            m_TargetTool.TargetNotice += OnTargetNotice;
            Mod.Log.Info("Disaster Control UI bindings created (Batch 0.2A)");
        }

        protected override void OnDestroy()
        {
            if (m_TargetTool != null)
            {
                m_TargetTool.TargetMoved -= OnTargetMoved;
                m_TargetTool.TargetConfirmed -= OnTargetConfirmed;
                m_TargetTool.TargetCancelled -= OnTargetCancelled;
                m_TargetTool.TargetNotice -= OnTargetNotice;
            }
            base.OnDestroy();
        }

        private void SetPanelOpen(bool value)
        {
            m_PanelOpen.Update(value);
        }

        private void ArmDisaster(string disasterId)
        {
            if (string.IsNullOrWhiteSpace(disasterId))
            {
                m_Status.Update("No disaster selected.");
                return;
            }

            string normalized = disasterId.Trim();
            m_SelectedDisaster.Update(normalized);
            m_TargetingActive.Update(true);
            m_PanelOpen.Update(false);

            if (normalized == "hailstorm")
            {
                m_Coordinates.Update("CITYWIDE WEATHER EVENT");
                m_TargetDetails.Update(string.Empty);
                m_Status.Update("HAILSTORM ACTIVE for 60 seconds. Citywide hail damage arrives in controlled pulses; press Cancel to stop early.");
                m_TargetTool.StartHailstorm();
                Mod.Log.Info("Activated disaster: " + normalized);
                return;
            }

            m_Coordinates.Update("Move over the map.");
            m_TargetDetails.Update("Aim at a building to inspect lightning damage.");
            m_Status.Update("TARGET MODE: " + normalized + " — left-click to confirm; right-click or Escape to cancel.");
            m_TargetTool.Arm(normalized);
            Mod.Log.Info("Armed disaster: " + normalized);
        }

        private void CancelTargeting()
        {
            m_TargetTool.CancelFromUI();
        }

        private void SetReticleStyle(string style)
        {
            string normalized = string.IsNullOrWhiteSpace(style) ? "auto" : style.Trim().ToLowerInvariant();
            if (normalized != "auto" && normalized != "crosshair" && normalized != "rings") normalized = "auto";
            m_ReticleStyle.Update(normalized);
            m_TargetTool.SetReticleStyle(normalized);
        }

        private void SetNuclearCraterEnabled(bool enabled)
        {
            m_NuclearCraterEnabled.Update(enabled);
            m_TargetTool.SetNuclearCraterEnabled(enabled);
            m_Status.Update("Nuclear terrain crater: " + (enabled ? "ON" : "OFF") + ".");
        }

        private void OnTargetMoved(float3 position, string details)
        {
            m_Coordinates.Update(DisasterTargetToolSystem.FormatPosition(position));
            m_TargetDetails.Update(details);
        }

        private void OnTargetConfirmed(string disasterId, float3 position)
        {
            string coordinates = DisasterTargetToolSystem.FormatPosition(position);
            m_TargetingActive.Update(true);
            m_SelectedDisaster.Update(disasterId);
            m_Coordinates.Update(coordinates);
            if (disasterId == "lightning")
            {
                m_Status.Update("LIGHTNING IMPACT at " + coordinates + ". Damage accumulates; the second strike can start an ordinary building fire.");
            }
            else if (disasterId == "wildfire")
            {
                m_Status.Update("WILDFIRE IGNITED at " + coordinates + ". Targeting remains active; click another tree/building or cancel.");
            }
            else if (disasterId == "earthquake")
            {
                m_Status.Update("MAJOR EARTHQUAKE RELEASED at " + coordinates + ". Severe destruction: 125m; moderate destruction: 300m; structural damage: 500m. Targeting remains active.");
            }
            else if (disasterId == "meteor")
            {
                m_Status.Update("METEOR LAUNCHED at " + coordinates + ". Targeting remains active; click again or cancel.");
            }
            else if (disasterId == "missile")
            {
                m_Status.Update("MISSILE LAUNCHED at " + coordinates + ". Compact 24m strike zone; targeting remains active.");
            }
            else if (disasterId == "nuclear")
            {
                m_Status.Update("NUCLEAR ICBM LAUNCHED at " + coordinates + ". Erasure dome: 358m; severe outer blast: 450m; mass fires and contamination: 520m.");
            }
            else
            {
                m_Status.Update("TEST TARGET CONFIRMED: " + disasterId + " at " + coordinates + ". Targeting remains active; click again or cancel.");
            }
        }

        private void OnTargetNotice(string message)
        {
            m_Status.Update(message);
        }

        private void OnTargetCancelled()
        {
            m_TargetingActive.Update(false);
            m_SelectedDisaster.Update(string.Empty);
            m_Coordinates.Update("No active target.");
            m_TargetDetails.Update(string.Empty);
            m_Status.Update("Targeting cancelled.");
        }
    }
}
