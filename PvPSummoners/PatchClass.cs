using ACE.Database;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics;
using ACE.Server.Physics.Common;
using PvPSummoners.Extensions;
using static ACE.Server.Physics.Common.ObjectMaint;

namespace PvPSummoners
{
    [HarmonyPatch]
    public class PatchClass
    {
        #region Settings
        const int RETRIES = 10;

        public static Settings Settings = new();
        static string settingsPath => Path.Combine(Mod.ModPath, "Settings.json");
        private FileInfo settingsInfo = new(settingsPath);

        private JsonSerializerOptions _serializeOptions = new()
        {
            WriteIndented = true,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private void SaveSettings()
        {
            string jsonString = JsonSerializer.Serialize(Settings, _serializeOptions);

            if (!settingsInfo.RetryWrite(jsonString, RETRIES))
            {
                ModManager.Log($"Failed to save settings to {settingsPath}...", ModManager.LogLevel.Warn);
                Mod.State = ModState.Error;
            }
        }

        private void LoadSettings()
        {
            if (!settingsInfo.Exists)
            {
                ModManager.Log($"Creating {settingsInfo}...");
                SaveSettings();
            }
            else
                ModManager.Log($"Loading settings from {settingsPath}...");

            if (!settingsInfo.RetryRead(out string jsonString, RETRIES))
            {
                Mod.State = ModState.Error;
                return;
            }

            try
            {
                Settings = JsonSerializer.Deserialize<Settings>(jsonString, _serializeOptions);
            }
            catch (Exception)
            {
                ModManager.Log($"Failed to deserialize Settings: {settingsPath}", ModManager.LogLevel.Warn);
                Mod.State = ModState.Error;
                return;
            }
        }
        #endregion

        #region Start/Shutdown
        public void Start()
        {
            //Need to decide on async use
            Mod.State = ModState.Loading;
            LoadSettings();

            if (Mod.State == ModState.Error)
            {
                ModManager.DisableModByPath(Mod.ModPath);
                return;
            }

            Mod.State = ModState.Running;
        }

        public void Shutdown()
        {
            //if (Mod.State == ModState.Running)
            // Shut down enabled mod...

            //If the mod is making changes that need to be saved use this and only manually edit settings when the patch is not active.
            //SaveSettings();

            if (Mod.State == ModState.Error)
                ModManager.Log($"Improper shutdown: {Mod.ModPath}", ModManager.LogLevel.Error);
        }
        #endregion

        #region Patches
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PetDevice), nameof(PetDevice.CheckUseRequirements), new Type[] { typeof(WorldObject) })]
        public static bool PreCheckUseRequirements(WorldObject activator, ref PetDevice __instance, ref ActivationResult __result)
        {
            if (!(activator is Player player))
            {
                __result = new ActivationResult(false);
                return false;
            }


            var baseRequirements = player.CheckUseRequirements(activator);
            if (!baseRequirements.Success)
            {
                __result = baseRequirements;
                return false;
            }


            // verify summoning mastery
            if (!Settings.IgnoreSummonerMasteries && __instance.SummoningMastery != null && player.SummoningMastery != __instance.SummoningMastery)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You must be a {__instance.SummoningMastery} to use the {__instance.Name}", ChatMessageType.Broadcast));
                __result = new ActivationResult(false);
                return false;
            }

            // duplicating some of this verification logic here from Pet.Init()
            // since the PetDevice owner and the summoned Pet are separate objects w/ potentially different heartbeat offsets,
            // the cooldown can still expire before the CombatPet's lifespan
            // in this case, if the player tries to re-activate the PetDevice while the CombatPet is still in the world,
            // we want to return an error without re-activating the cooldown

            if (player.CurrentActivePet != null && player.CurrentActivePet is CombatPet)
            {
                if (PropertyManager.GetBool("pet_stow_replace").Item)
                {
                    // original ace
                    player.SendTransientError($"{player.CurrentActivePet.Name} is already active");
                    __result = new ActivationResult(false);
                    return false;
                }
                else
                {
                    // retail stow
                    var weenie = DatabaseManager.World.GetCachedWeenie((uint)__instance.PetClass);


                    if (weenie == null || weenie.WeenieType != WeenieType.Pet)
                    {
                        player.SendTransientError($"{player.CurrentActivePet.Name} is already active");
                        __result = new ActivationResult(false);
                        return false;
                    }
                }
            }

            __result = new ActivationResult(true);
            return false;
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(ObjectMaint), "ApplyFilter", new Type[] { typeof(List<PhysicsObj>), typeof(VisibleObjectType) })]
        public static bool PreApplyFilter(List<PhysicsObj> objs, VisibleObjectType type, ref ObjectMaint __instance, ref IEnumerable<PhysicsObj> __result)
        {
            IEnumerable<PhysicsObj> results = objs;

            if (type == VisibleObjectType.Players)
            {
                results = objs.Where(i => i.IsPlayer);
            }
            else if (type == VisibleObjectType.AttackTargets)
            {
                var obj = __instance;
                if (obj.PhysicsObj.WeenieObj.IsCombatPet)
                    results = objs.Where((i) => i.WeenieObj.IsMonster || i.WeenieObj.IsPK());
                else if (obj.PhysicsObj.WeenieObj.IsFactionMob)
                    results = objs.Where(i => i.IsPlayer || i.WeenieObj.IsCombatPet || i.WeenieObj.IsMonster && !i.WeenieObj.SameFaction(obj.PhysicsObj));
                else
                {
                    // adding faction mobs here, even though they are retaliate-only, for inverse visible targets
                    results = objs.Where(i => i.IsPlayer || i.WeenieObj.IsCombatPet && obj.PhysicsObj.WeenieObj.PlayerKillerStatus != PlayerKillerStatus.PK || i.WeenieObj.IsFactionMob || i.WeenieObj.PotentialFoe(obj.PhysicsObj));
                }
            }
            __result = results;
            return false;
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(ObjectMaint), "AddVisibleTarget", new Type[] { typeof(PhysicsObj), typeof(bool), typeof(bool) })]
        public static bool PreAddVisibleTarget(PhysicsObj obj, bool clamp, bool foeType, ref ObjectMaint __instance, ref bool __result)
        {
            if (__instance.PhysicsObj.WeenieObj.IsCombatPet)
            {
                var isPk = obj.WeenieObj.IsPK();

                if (!obj.WeenieObj.IsMonster && !isPk)
                {
                    Console.WriteLine($"{__instance.PhysicsObj.Name}.ObjectMaint.AddVisibleTarget({obj.Name}): tried to add a non-monster or pk");
                    __result = false;
                    return false;
                }

                try
                {
                    if (obj.WeenieObj.IsPetAlly(__instance.PhysicsObj.WeenieObj.WorldObject))
                    {
                        __result = false;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    ModManager.Log($"Crash prevented for adding visible target!", ModManager.LogLevel.Warn);
                    ModManager.Log(ex.StackTrace, ModManager.LogLevel.Error);
                    __result = false;
                    return false;
                }
            }
            else if (__instance.PhysicsObj.WeenieObj.IsFactionMob)
            {
                // only tracking players, combat pets, and monsters of differing faction
                if (!obj.IsPlayer && !obj.WeenieObj.IsCombatPet && (!obj.WeenieObj.IsMonster || __instance.PhysicsObj.WeenieObj.SameFaction(obj)))
                {
                    Console.WriteLine($"{__instance.PhysicsObj.Name}.ObjectMaint.AddVisibleTarget({obj.Name}): tried to add a non-player / non-combat pet / non-opposing faction mob");
                    __result = false;
                    return false;
                }
            }
            else
            {
                // handle special case:
                // we want to select faction mobs for monsters inverse targets,
                // but not add to the original monster
                if (obj.WeenieObj.IsFactionMob)
                {
                    obj.ObjMaint.AddVisibleTargets(new List<PhysicsObj>() { __instance.PhysicsObj });
                    __result = false;
                    return false;
                }

                // handle special case:
                // if obj has a FoeType of this creature, and this creature doesn't have a FoeType for obj,
                // we only want to perform the inverse
                if (obj.WeenieObj.FoeType != null && obj.WeenieObj.FoeType == __instance.PhysicsObj.WeenieObj.WorldObject?.CreatureType &&
                    (__instance.PhysicsObj.WeenieObj.FoeType == null || obj.WeenieObj.WorldObject != null && __instance.PhysicsObj.WeenieObj.FoeType != obj.WeenieObj.WorldObject.CreatureType))
                {
                    obj.ObjMaint.AddVisibleTargets(new List<PhysicsObj>() { __instance.PhysicsObj });
                    __result = false;
                    return false;
                }

                // only tracking players and combat pets
                if (!obj.IsPlayer && !obj.WeenieObj.IsCombatPet && __instance.PhysicsObj.WeenieObj.FoeType == null)
                {
                    Console.WriteLine($"{__instance.PhysicsObj.Name}.ObjectMaint.AddVisibleTarget({obj.Name}): tried to add a non-player / non-combat pet");
                    __result = false;
                    return false;
                }
            }
            if (__instance.PhysicsObj.DatObject)
            {
                Console.WriteLine($"{__instance.PhysicsObj.Name}.ObjectMaint.AddVisibleTarget({obj.Name}): tried to add player for dat object");
                __result = false;
                return false;
            }

            ObjectMaint objMaintInstance = obj.ObjMaint;
            Type objMaintType = objMaintInstance.GetType();
            MethodInfo addVisisbleTargetMethod = objMaintType.GetMethod("AddVisibleTarget", BindingFlags.NonPublic | BindingFlags.Instance);
            PropertyInfo knownObjectsProperty = objMaintType.GetProperty("KnownObjects", BindingFlags.NonPublic | BindingFlags.Instance);
            PropertyInfo visibleTargetsProperty = objMaintType.GetProperty("VisibleTargets", BindingFlags.NonPublic | BindingFlags.Instance);


            // __instance reflection properties and methods
            var objMaint = __instance;
            Type instanceObjMaintType = objMaint.GetType();
            PropertyInfo instanceVisibleTargetsProperty = instanceObjMaintType.GetProperty("VisibleTargets", BindingFlags.NonPublic | BindingFlags.Instance);

            if (clamp && InitialClamp && obj.IsPlayer && knownObjectsProperty != null)
            {
                //!knownObjectsProperty.GetValue(objMaintInstance).ContainsKey(obj.ID)
                var knownObjectsValue = knownObjectsProperty.GetValue(objMaintInstance);

                if (knownObjectsValue is Dictionary<uint, PhysicsObj> dic && !dic.ContainsKey(obj.ID))
                {
                    var distSq = __instance.PhysicsObj.Position.Distance2DSquared(obj.Position);

                    if (distSq > InitialClamp_DistSq)
                    {

                        __result = false;
                        return false;
                    }
                }

            }

            if (instanceVisibleTargetsProperty != null)
            {
                var instanceVisibleTargetsValue = instanceVisibleTargetsProperty.GetValue(__instance);

                // TryAdd for existing keys still modifies collection?
                if (instanceVisibleTargetsValue is Dictionary<uint, PhysicsObj> dic && dic.ContainsKey(obj.ID))
                {
                    __result = false;
                    return false;
                }

            }

            //Console.WriteLine($"{PhysicsObj.Name} ({PhysicsObj.ID:X8}).ObjectMaint.AddVisibleTarget({obj.Name})");

            if (instanceVisibleTargetsProperty != null)
            {
                var instanceVisibleTargetsValue = instanceVisibleTargetsProperty.GetValue(__instance);

                // TryAdd for existing keys still modifies collection?
                if (instanceVisibleTargetsValue is Dictionary<uint, PhysicsObj> dic)
                {
                    dic.Add(obj.ID, obj);
                }

            }

            // maintain inverse for monsters / combat pets
            if (!obj.IsPlayer)
                obj.ObjMaint.AddVisibleTargets(new List<PhysicsObj>() { __instance.PhysicsObj });

            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), nameof(Player.IsPKDeath), new Type[] { typeof(DamageHistoryInfo) })]
        public static bool PreIsPKDeath(DamageHistoryInfo topDamager, ref Player __instance, ref bool __result)
        {
            if (topDamager.PetOwner != null && topDamager.PetOwner.TryGetTarget(out Player target))
            {
                __result = __instance.IsPKDeath(target?.Guid.Full);
                return false;
            }

            return true;
        }
        #endregion
    }
}