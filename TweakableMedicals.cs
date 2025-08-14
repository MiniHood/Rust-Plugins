using HarmonyLib;
using Oxide.Core;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("TweakableMedicals", "HowNiceOfYou", "0.1.1")]
    [Description("Makes medical items tweakable.")]
    class TweakableMedicals : RustPlugin
    {
        public static TweakableMedicals Instance;
        private MedicalConfig _config;
        private Dictionary<string, ConsumableEffects> _healingItemsDict;

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating new config file.");
            var defaultConfig = new MedicalConfig();
            Config.WriteObject(defaultConfig, true);
        }

        void Init()
        {
            Instance = this;
            LoadConfigValues();
        }

        void Unload()
        {
            Instance = null;
        }

        private void ValidateConfig()
        {
            foreach (var kvp in _healingItemsDict)
            {
                var effects = kvp.Value;
                effects.HealthChance = Mathf.Clamp01(effects.HealthChance);
                effects.RadiationChance = Mathf.Clamp01(effects.RadiationChance);
                effects.PoisonChance = Mathf.Clamp01(effects.PoisonChance);
                effects.HealthOverTimeChance = Mathf.Clamp01(effects.HealthOverTimeChance);
            }
        }

        private void LoadConfigValues()
        {
            _config = Config.ReadObject<MedicalConfig>() ?? new MedicalConfig();

            if (_config == null)
            {
                PrintWarning("Config was null, creating new config.");
                _config = new MedicalConfig();
                Config.WriteObject(_config, true);
            }

            _healingItemsDict = _config.HealingItems;
            ValidateConfig();
        }

        private static void TryApply(BasePlayer player, MetabolismAttribute.Type type, float amount, float chance)
        {
            if (chance >= 1f || (chance > 0f && UnityEngine.Random.value <= chance))
                player.metabolism.ApplyChange(type, amount, 0);
        }

        // Medkit & anti rad pills are consumables (<-- stupid) hence the extra steps -_-
        [HarmonyPatch(nameof(Item), "UseItem")]
        [AutoPatch]
        class Medkit_Patch
        {
            static bool Prefix(Item __instance, int amountToConsume = 1)
            {
                if (amountToConsume <= 0)
                    return false;

                // TODO: Change this to a list of some sort if expanding bc im not dealing with one big ass if statement
                if (__instance.info.shortname != "largemedkit" && __instance.info.shortname != "antiradpills")
                    return true;

                if (!Instance._healingItemsDict.TryGetValue(__instance.info.shortname, out var customEffects) || customEffects == null)
                    return true;

                object obj = Interface.CallHook("OnItemUse", __instance, amountToConsume);
                if (obj is int newAmt) amountToConsume = newAmt;

                __instance.amount -= amountToConsume;
                __instance.ReduceItemOwnership(amountToConsume);
                if(__instance.amount <= 0)
                {
                    __instance.amount = 0;
                    __instance.Remove(0f);
                } else
                {
                    __instance.MarkDirty();
                }

                var player = __instance.GetOwnerPlayer();

                TryApply(player, MetabolismAttribute.Type.Health, customEffects.Health, customEffects.HealthChance);
                TryApply(player, MetabolismAttribute.Type.Radiation, customEffects.Radiation, customEffects.RadiationChance);
                TryApply(player, MetabolismAttribute.Type.Poison, customEffects.Poison, customEffects.PoisonChance);
                TryApply(player, MetabolismAttribute.Type.HealthOverTime, customEffects.HealthOverTime, customEffects.HealthOverTimeChance);
                // TODO: Bleeding doesn't stack and I have no idea why...
                // Already tried adding player.metabolism.bleeding.value but it's always 0?
                TryApply(player, MetabolismAttribute.Type.Bleeding, customEffects.Bleeding, customEffects.BleedingChance);
                TryApply(player, MetabolismAttribute.Type.Hydration, customEffects.Hydration, customEffects.HydrationChance);

                return false;
            }
        }
        
        [HarmonyPatch(typeof(MedicalTool), "GiveEffectsTo")]
        [AutoPatch]
        class Medical_Patch
        {
            static bool Prefix(BasePlayer player, MedicalTool __instance)
            {
                if (!player) return false;

                var ownerItemDefinition = __instance.GetOwnerItemDefinition();
                var component = ownerItemDefinition.GetComponent<ItemModConsumable>();
                if (!component) return false;

                if (Interface.CallHook("OnHealingItemUse", __instance, player) != null)
                    return false;

                var ownerPlayer = __instance.GetOwnerPlayer();
                Facepunch.Rust.Analytics.Azure.OnMedUsed(ownerItemDefinition.shortname, ownerPlayer, player);

                if (player != ownerPlayer)
                {
                    if (Interface.CallHook("OnPlayerRevive", ownerPlayer, player) != null)
                        return false;

                    if (player.IsWounded() && __instance.canRevive)
                        player.StopWounded(ownerPlayer);
                }

                Instance._healingItemsDict.TryGetValue(component.name, out var customEffects);
                bool hasCustom = customEffects != null;

                foreach (var effect in component.effects)
                {
                    float amount = effect.amount;
                    float chance = 1f;

                    if (hasCustom)
                    {
                        switch (effect.type)
                        {
                            case MetabolismAttribute.Type.Health:
                                amount = customEffects.Health;
                                chance = customEffects.HealthChance;
                                break;
                            case MetabolismAttribute.Type.Radiation:
                                amount = customEffects.Radiation;
                                chance = customEffects.RadiationChance;
                                break;
                            case MetabolismAttribute.Type.Poison:
                                amount = customEffects.Poison;
                                chance = customEffects.PoisonChance;
                                break;
                            case MetabolismAttribute.Type.HealthOverTime:
                                amount = customEffects.HealthOverTime;
                                chance = customEffects.HealthOverTimeChance;
                                break;
                            case MetabolismAttribute.Type.Bleeding:
                                amount = customEffects.Bleeding;
                                chance = customEffects.BleedingChance;
                                break;
                            case MetabolismAttribute.Type.Hydration:
                                amount = customEffects.Hydration;
                                chance = customEffects.HydrationChance;
                                break;
                        }
                    }

                    TryApply(player, effect.type, amount, chance);
                }

                if (player is BasePet)
                    player.SendNetworkUpdateImmediate(false);

                return false;
            }
        }
    }

    public class ConsumableEffects
    {
        
        public float Health { get; set; } = 0f;
        public float HealthChance { get; set; } = 1f;
        public float Radiation { get; set; } = 0f;
        public float RadiationChance { get; set; } = 1f; 

        public float Poison { get; set; } = 0f;
        public float PoisonChance { get; set; } = 1f;

        public float HealthOverTime { get; set; } = 0f;
        public float HealthOverTimeChance { get; set; } = 1f;
        public float Bleeding { get; set; } = 0f;
        public float BleedingChance { get; set; } = 1f;
        public float Hydration { get; set; } = 1f;
        public float HydrationChance { get; set; } = 1f;
    }

    public class MedicalConfig
    {
        public Dictionary<string, ConsumableEffects> HealingItems { get; set; } = new Dictionary<string, ConsumableEffects>
        {
            { "syringe_medical.item", new ConsumableEffects
                {
                    Health = 15f,
                    HealthChance = 1f,
                    Radiation = -10f,
                    RadiationChance = 1f, 
                    Poison = -5f,
                    PoisonChance = 1f,
                    HealthOverTime = 20f,
                    HealthOverTimeChance = 1f,
                    Bleeding = 0f,
                    BleedingChance = 1f,
                    Hydration = 0f,
                    HydrationChance = 1f,
               }
            },
            { "bandage.item", new ConsumableEffects
                {
                    Health = 5f,
                    HealthChance = 1f,
                    Radiation = 0f,
                    RadiationChance = 1f,   
                    Poison = -2f,
                    PoisonChance = 1f,     
                    HealthOverTime = 0f,
                    HealthOverTimeChance = 1f,
                    Bleeding = 0f,
                    BleedingChance = 1f,
                    Hydration = 0f,
                    HydrationChance = 1f,
                }
            },
            { "largemedkit", new ConsumableEffects
                {
                    Health = 10f,
                    HealthChance = 1f,
                    Radiation = 0f,
                    RadiationChance = 1f,
                    Poison = -10f,
                    PoisonChance = 1f,
                    HealthOverTime = 100f,
                    HealthOverTimeChance = 1f,
                    Bleeding = -100f,
                    BleedingChance = 1f,
                    Hydration = -5f,
                    HydrationChance = 1f,
                }
            },
                        { "antiradpills", new ConsumableEffects
                {
                    Health = 0f,
                    HealthChance = 1f,
                    Radiation = -75f,
                    RadiationChance = 1f,
                    Poison = 0f,
                    PoisonChance = 1f,
                    HealthOverTime = 0f,
                    HealthOverTimeChance = 1f,
                    Bleeding = 0f,
                    BleedingChance = 1f,
                    Hydration = -5f,
                    HydrationChance = 1f,
                }
            }
        };

    }
}
