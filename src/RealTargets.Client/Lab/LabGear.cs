using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;

namespace RealTargets.Lab
{
    /// <summary>
    /// Fixed armour tiers for test dummies, applied to the bot's profile before it spawns (so the
    /// model loads with the right gear). Items come from EFT's default presets, which include the
    /// plates and soft inserts. Template ids verified against the SPT 4.1.3 item database.
    /// </summary>
    internal static class LabGear
    {
        internal sealed class Tier
        {
            public string Label;
            public string Armor;
            public string Helmet;
            public string FaceShield;
        }

        // 0 naked, 1 soft, 2 class 4, 3 class 5, 4 class 6.
        internal static readonly Tier[] Tiers =
        {
            new Tier { Label = "T0 naked" },
            new Tier { Label = "T1 soft (PACA, cl2)", Armor = "5648a7494bdc2d9d488b4583" },
            new Tier { Label = "T2 cl4 (TFO + SSh-68)", Armor = "5c0e655586f774045612eeb2", Helmet = "5c06c6a80db834001b735491" },
            new Tier { Label = "T3 cl5 (Korund-VM + ULACH)", Armor = "5f5f41476bdad616ad46d631", Helmet = "5b40e1525acfc4771e1c6611" },
            new Tier
            {
                Label = "T4 cl6 (Zabralo + Altyn + shield)", Armor = "545cdb794bdc2d3a198b456a",
                Helmet = "5aa7e276e5b5b000171d0647", FaceShield = "5aa7e373e5b5b000137b76f0",
            },
        };

        // Everything that can carry armour or hide hits (backpacks block back shots).
        private static readonly EquipmentSlot[] Strip =
        {
            EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.Headwear,
            EquipmentSlot.FaceCover, EquipmentSlot.Eyewear, EquipmentSlot.Backpack,
        };

        internal static void Apply(Profile profile, int tierIndex)
        {
            var equipment = profile.Inventory.Equipment;
            foreach (var slotName in Strip)
            {
                var slot = equipment.GetSlot(slotName);
                if (slot?.ContainedItem != null)
                {
                    var removed = slot.RemoveItem();
                    if (removed.Failed)
                    {
                        Plugin.Log.LogWarning($"Lab: couldn't strip {slotName}: {removed.Error}");
                    }
                }
            }

            var tier = Tiers[tierIndex];
            var factory = Singleton<ItemFactory>.Instance;
            if (tier.Armor != null)
            {
                Equip(equipment.GetSlot(EquipmentSlot.ArmorVest), factory.GetPresetItem(tier.Armor));
            }

            if (tier.Helmet != null)
            {
                var helmet = factory.GetPresetItem(tier.Helmet);
                if (tier.FaceShield != null && helmet is CompoundItem compound)
                {
                    var shield = factory.GetPresetItem(tier.FaceShield);
                    var shieldSlot = compound.Slots.FirstOrDefault(s => s.ContainedItem == null && s.CheckCompatibility(shield));
                    if (shieldSlot != null)
                    {
                        Equip(shieldSlot, shield);
                    }
                    else
                    {
                        Plugin.Log.LogWarning("Lab: no face shield slot on the helmet.");
                    }
                }
                Equip(equipment.GetSlot(EquipmentSlot.Headwear), helmet);
            }
        }

        private static void Equip(Slot slot, Item item)
        {
            if (slot == null || item == null)
            {
                return;
            }

            var added = slot.AddWithoutRestrictions(item);
            if (added.Failed)
            {
                Plugin.Log.LogWarning($"Lab: couldn't equip {item.TemplateId} in {slot.ID}: {added.Error}");
            }
        }
    }
}
