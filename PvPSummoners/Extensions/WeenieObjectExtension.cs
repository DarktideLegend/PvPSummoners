using ACE.Server.Physics.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PvPSummoners.Extensions
{
    public static class WeenieObjectExtension
    {
        public static bool IsPetAlly(this WeenieObject obj, WorldObject pet)
        {
            if (!(pet is CombatPet combatPet))
                return false;

            if (obj.WorldObject is CombatPet)
                return IsAllyCombatPet(obj.WorldObject as CombatPet, combatPet);

            if (obj.WorldObject is Player player)
                return IsPetOwner(player, combatPet) || IsPetAllegianceMember(player, combatPet) || IsPetFellowshipMember(player, combatPet);

            return false;
        }

        private static bool IsAllyCombatPet(CombatPet petA, CombatPet petB)
        {

            if (IsPetAllegianceMember(petA.P_PetOwner, petB))
                return true;

            if (IsPetFellowshipMember(petA.P_PetOwner, petB))
                return true;

            return false;

        }

        private static bool IsPetFellowshipMember(Player player, CombatPet combatPet)
        {
            return combatPet.P_PetOwner.GetFellowshipTargets().Contains(player);

        }

        private static bool IsPetAllegianceMember(Player player, CombatPet combatPet)
        {
            if (player.Allegiance == null)
                return false;

            var monarchId = combatPet?.P_PetOwner?.MonarchId;
            var playerMonarchId = player?.MonarchId;

            if (monarchId != null && playerMonarchId != null && monarchId == playerMonarchId)
                return true;

            return false;
        }

        private static bool IsPetOwner(Player player, CombatPet pet)
        {
            if (pet.P_PetOwner == player)
                return true;

            return false;
        }
    }
}
