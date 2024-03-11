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
        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(Creature), nameof(Creature.GetDeathMessage), new Type[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
        //public static void PreDeathMessage(DamageHistoryInfo lastDamagerInfo, DamageType damageType, bool criticalHit, ref Creature __instance)
        //{
        //  ...
        //}
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
        [HarmonyPatch(typeof(Player), nameof(Player.HandlePKDeathBroadcast), new Type[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) })]
        public static bool PreHandlePKDeathBroadcast(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager, ref Player __instance)
        {
            var isSummonerDeath = topDamager.PetOwner != null;

            if ((topDamager == null || !topDamager.IsPlayer) && !isSummonerDeath)
                return false;

            var pkPlayer = topDamager.TryGetPetOwnerOrAttacker() as Player;
            if (pkPlayer == null)
                return false;

            if (isSummonerDeath || __instance.IsPKDeath(topDamager))
            {
                pkPlayer.PkTimestamp = Time.GetUnixTime();
                pkPlayer.PlayerKillsPk++;

                var globalPKDe = $"{lastDamager.Name} has defeated {__instance.Name}!";

                if ((__instance.Location.Cell & 0xFFFF) < 0x100)
                    globalPKDe += $" The kill occured at {__instance.Location.GetMapCoordStr()}";


                globalPKDe += "\n[PKDe]";

                PlayerManager.BroadcastToAll(new GameMessageSystemChat(globalPKDe, ChatMessageType.Broadcast));
            }
            else if (__instance.IsPKLiteDeath(topDamager))
                pkPlayer.PlayerKillsPkl++;

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "Die", new Type[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) })]
        public static bool PreDie(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager, ref Player __instance)
        {
            var isSummonerDeath = topDamager.PetOwner != null;

            __instance.IsInDeathProcess = true;

            if (topDamager?.Guid == __instance.Guid && __instance.IsPKType)
            {
                var topDamagerOther = __instance.DamageHistory.GetTopDamager(false);

                if (topDamagerOther != null && topDamagerOther.IsPlayer)
                    topDamager = topDamagerOther;
            }

            __instance.UpdateVital(__instance.Health, 0);
            __instance.NumDeaths++;
            __instance.suicideInProgress = false;

            // todo: since we are going to be using 'time since Player last died to an OlthoiPlayer'
            // as a factor in slag generation, this will eventually be moved to after the slag generation

            //if (topDamager != null && topDamager.IsOlthoiPlayer)
            //OlthoiLootTimestamp = (int)Time.GetUnixTime();

            if (__instance.CombatMode == CombatMode.Magic && __instance.MagicState.IsCasting)
                __instance.FailCast(false);

            // TODO: instead of setting IsBusy here,
            // eventually all of the places that check for states such as IsBusy || Teleporting
            // might want to use a common function, and IsDead should return a separate error
            __instance.IsBusy = true;

            // killer = top damager for looting rights
            if (topDamager != null)
                __instance.KillerId = topDamager.Guid.Full;

            // broadcast death animation
            var deathAnim = new Motion(MotionStance.NonCombat, MotionCommand.Dead);
            __instance.EnqueueBroadcastMotion(deathAnim);

            // create network messages for player death
            var msgHealthUpdate = new GameMessagePrivateUpdateAttribute2ndLevel(__instance, Vital.Health, 0);

            // TODO: death sounds? seems to play automatically in client
            // var msgDeathSound = new GameMessageSound(Guid, Sound.Death1, 1.0f);
            var msgNumDeaths = new GameMessagePrivateUpdatePropertyInt(__instance, PropertyInt.NumDeaths, __instance.NumDeaths);

            // send network messages for player death
            __instance.Session.Network.EnqueueSend(msgHealthUpdate, msgNumDeaths);

            if (lastDamager?.Guid == __instance.Guid) // suicide
            {
                var msgSelfInflictedDeath = new GameEventWeenieError(__instance.Session, WeenieError.YouKilledYourself);
                __instance.Session.Network.EnqueueSend(msgSelfInflictedDeath);
            }

            var hadVitae = __instance.HasVitae;

            // update vitae
            // players who died in a PKLite fight do not accrue vitae
            if (!__instance.IsPKLiteDeath(topDamager))
                __instance.InflictVitaePenalty();

            if ((isSummonerDeath || __instance.IsPKDeath(topDamager)) || __instance.AugmentationSpellsRemainPastDeath == 0)
            {
                var msgPurgeEnchantments = new GameEventMagicPurgeEnchantments(__instance.Session);
                __instance.EnchantmentManager.RemoveAllEnchantments();
                __instance.Session.Network.EnqueueSend(msgPurgeEnchantments);
            }
            else
                __instance.Session.Network.EnqueueSend(new GameMessageSystemChat("Your augmentation prevents the tides of death from ripping away your current enchantments!", ChatMessageType.Broadcast));

            // wait for the death animation to finish
            var dieChain = new ActionChain();
            var animLength = DatManager.PortalDat.ReadFromDat<MotionTable>(__instance.MotionTableId).GetAnimationLength(MotionCommand.Dead);
            dieChain.AddDelaySeconds(animLength + 1.0f);

            //resolve player
            Player player = PlayerManager.FindByGuid(__instance.Guid) as Player;
            if (player is null)
                return false;

            Type playerType = player.GetType();
            MethodInfo createCorpseMethod = playerType.GetMethod("CreateCorpse", BindingFlags.NonPublic | BindingFlags.Instance);

            dieChain.AddAction(__instance, () =>
            {
                if (createCorpseMethod != null)
                {

                    createCorpseMethod.Invoke(player, new object[] { topDamager, hadVitae });
                }

                player.ThreadSafeTeleportOnDeath(); // enter portal space

                if (isSummonerDeath || player.IsPKDeath(topDamager) || player.IsPKLiteDeath(topDamager))
                    player.SetMinimumTimeSincePK();

                player.IsBusy = false;
            });

            dieChain.EnqueueChain();

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), nameof(Player.PK_DeathTick))]
        public static bool PrePK_DeathTick(ref Player __instance)
        {
            Player player = __instance;
            Type playerType = player.GetType();
            FieldInfo cachedHeartbeatIntervalProperty = playerType.GetField("CachedHeartbeatInterval", BindingFlags.NonPublic | BindingFlags.Instance);

            if (__instance.MinimumTimeSincePk == null || (PropertyManager.GetBool("pk_server_safe_training_academy").Item && __instance.RecallsDisabled))
                return false;

            if (__instance.PkLevel == PKLevel.NPK && !PropertyManager.GetBool("pk_server").Item && !PropertyManager.GetBool("pkl_server").Item)
            {
                __instance.MinimumTimeSincePk = null;
                return false;
            }

            if (cachedHeartbeatIntervalProperty != null)
            {
                var cachedHeartbeatValue = cachedHeartbeatIntervalProperty.GetValue(player);
                if (cachedHeartbeatValue is double val)
                    __instance.MinimumTimeSincePk += val;

            }

            if (__instance.MinimumTimeSincePk < PropertyManager.GetDouble("pk_respite_timer").Item)
                return false;

            __instance.MinimumTimeSincePk = null;

            var werror = WeenieError.None;
            var pkLevel = __instance.PkLevel;

            if (PropertyManager.GetBool("pk_server").Item)
                pkLevel = PKLevel.PK;
            else if (PropertyManager.GetBool("pkl_server").Item)
                pkLevel = PKLevel.PKLite;

            switch (pkLevel)
            {
                case PKLevel.NPK:
                    return false;

                case PKLevel.PK:
                    __instance.PlayerKillerStatus = PlayerKillerStatus.PK;
                    werror = WeenieError.YouArePKAgain;
                    break;

                case PKLevel.PKLite:
                    __instance.PlayerKillerStatus = PlayerKillerStatus.PKLite;
                    werror = WeenieError.YouAreNowPKLite;
                    break;
            }

            __instance.EnqueueBroadcast(new GameMessagePublicUpdatePropertyInt(__instance, PropertyInt.PlayerKillerStatus, (int)__instance.PlayerKillerStatus));
            __instance.Session.Network.EnqueueSend(new GameEventWeenieError(__instance.Session, werror));

            return false;
        }
        #endregion
    }
}