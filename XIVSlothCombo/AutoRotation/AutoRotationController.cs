﻿using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Linq;
using XIVSlothCombo.Combos;
using XIVSlothCombo.Combos.PvE;
using XIVSlothCombo.CustomComboNS.Functions;
using XIVSlothCombo.Extensions;
using XIVSlothCombo.Services;
using XIVSlothCombo.Window.Functions;
using Action = Lumina.Excel.GeneratedSheets.Action;

namespace XIVSlothCombo.AutoRotation
{
    internal unsafe static class AutoRotationController
    {
        static long LastHealAt = 0;
        internal static void Run()
        {
            if (!Service.Configuration.RotationConfig.Enabled || !Player.Available || Svc.Condition[ConditionFlag.Mounted] || (Service.Configuration.RotationConfig.InCombatOnly && !CustomComboFunctions.InCombat()))
                return;

            if (Player.Object.CurrentCastTime > 0) return;

            if (!EzThrottler.Throttle("AutoRotController", 150))
                return;

            if (Player.Job is ECommons.ExcelServices.Job.SGE && Service.Configuration.RotationConfig.HealerSettings.ManageKardia)
                UpdateKardiaTarget();

            var healTarget = Player.Object.GetRole() is CombatRole.Healer ? AutoRotationHelper.GetSingleTarget(Service.Configuration.RotationConfig.HealerRotationMode) : null;
            var aoeheal = Player.Object.GetRole() is CombatRole.Healer ? HealerTargeting.CanAoEHeal() : false;

            if (Player.Object.GetRole() is CombatRole.Healer)
            {
                bool needsHeal = healTarget != null || aoeheal;

                if (Service.Configuration.RotationConfig.HealerSettings.AutoCleanse && !needsHeal)
                {
                    CleanseParty();
                    if (CustomComboFunctions.GetPartyMembers().Any((x => CustomComboFunctions.HasCleansableDebuff(x))))
                        return;
                }

                if (Service.Configuration.RotationConfig.HealerSettings.AutoRez)
                {
                    RezParty();
                    if (CustomComboFunctions.GetPartyMembers().Any(x => x.IsDead && CustomComboFunctions.FindEffectOnMember(2648, x) == null))
                        return;
                }
            }

            foreach (var preset in Service.Configuration.AutoActions.OrderByDescending(x => Presets.Attributes[x.Key].AutoAction.IsHeal)
                                                                    .ThenByDescending(x => Presets.Attributes[x.Key].AutoAction.IsAoE))
            {
                if (!CustomComboFunctions.IsEnabled(preset.Key) || !preset.Value) continue;

                var attributes = Presets.Attributes[preset.Key];
                var action = attributes.AutoAction;
                var gameAct = attributes.ReplaceSkill.ActionIDs.First();
                var sheetAct = Svc.Data.GetExcelSheet<Action>().GetRow(gameAct);
                var classToJob = CustomComboFunctions.JobIDs.ClassToJob((byte)Player.Job);
                if ((byte)Player.Job != attributes.CustomComboInfo.JobID && classToJob != attributes.CustomComboInfo.JobID)
                    continue;

                var outAct = AutoRotationHelper.InvokeCombo(preset.Key, attributes);

                if (action.IsHeal)
                {
                    if (!AutomateHealing(preset.Key, attributes, gameAct) && Svc.Targets.Target != null && !Svc.Targets.Target.IsHostile() && Environment.TickCount64 > LastHealAt + 1000)
                        Svc.Targets.Target = null;

                    if ((healTarget != null && !action.IsAoE) || (aoeheal && action.IsAoE))
                        return;
                    else
                        continue;
                }


                if (Player.Object.GetRole() is CombatRole.Tank)
                {
                    AutomateTanking(preset.Key, attributes, gameAct);
                    continue;
                }

                AutomateDPS(preset.Key, attributes, gameAct);
            }


        }

        private static void RezParty()
        {
            uint resSpell = Player.Job switch
            {
                Job.CNJ or Job.WHM => WHM.Raise,
                Job.SCH => SCH.Resurrection,
                Job.AST => AST.Ascend,
                Job.SGE => SGE.Egeiro,
                _ => throw new NotImplementedException(),
            };

            if (Player.Object.CurrentMp >= CustomComboFunctions.GetResourceCost(resSpell))
            {
                if (CustomComboFunctions.GetPartyMembers().FindFirst(x => x.IsDead && CustomComboFunctions.FindEffectOnMember(2648, x) == null, out var member))
                {
                    ActionManager.Instance()->UseAction(ActionType.Action, resSpell, member.GameObjectId);
                }
            }
        }

        private static void CleanseParty()
        {
            if (ActionManager.Instance()->QueuedActionId == All.Esuna)
                ActionManager.Instance()->QueuedActionId = 0;

            if (CustomComboFunctions.GetPartyMembers().FindFirst(x => CustomComboFunctions.HasCleansableDebuff(x), out var member))
                ActionManager.Instance()->UseAction(ActionType.Action, All.Esuna, member.GameObjectId);
        }

        private static void UpdateKardiaTarget()
        {
            if (!CustomComboFunctions.LevelChecked(SGE.Kardia)) return;
            if (CustomComboFunctions.CombatEngageDuration().TotalSeconds < 3) return;

            foreach (var member in CustomComboFunctions.GetPartyMembers().OrderByDescending(x => x.GetRole() is CombatRole.Tank))
            {
                if (Service.Configuration.RotationConfig.HealerSettings.KardiaTanksOnly && member.GetRole() is not CombatRole.Tank) continue;

                var enemiesTargeting = Svc.Objects.Where(x => x.IsTargetable && x.IsHostile() && x.TargetObjectId == member.GameObjectId).Count();
                if (enemiesTargeting > 0 && CustomComboFunctions.FindEffectOnMember(SGE.Buffs.Kardion, member) is null)
                {
                    ActionManager.Instance()->UseAction(ActionType.Action, SGE.Kardia, member.GameObjectId);
                    return;
                }
            }

        }

        private static bool AutomateDPS(CustomComboPreset preset, Presets.PresetAttributes attributes, uint gameAct)
        {
            if (Svc.Targets.Target != null && !Svc.Targets.Target.IsHostile()) return false;

            var mode = Service.Configuration.RotationConfig.DPSRotationMode;
            if (attributes.AutoAction.IsAoE)
            {
                return AutoRotationHelper.ExecuteAoE(mode, preset, attributes, gameAct);
            }
            else
            {
                return AutoRotationHelper.ExecuteST(mode, preset, attributes, gameAct);
            }
        }

        private static bool AutomateTanking(CustomComboPreset preset, Presets.PresetAttributes attributes, uint gameAct)
        {
            var mode = Service.Configuration.RotationConfig.DPSRotationMode;
            if (attributes.AutoAction.IsAoE)
            {
                return AutoRotationHelper.ExecuteAoE(mode, preset, attributes, gameAct);
            }
            else
            {
                return AutoRotationHelper.ExecuteST(mode, preset, attributes, gameAct);
            }
        }

        private static bool AutomateHealing(CustomComboPreset preset, Presets.PresetAttributes attributes, uint gameAct)
        {
            var mode = Service.Configuration.RotationConfig.HealerRotationMode;
            if (Player.Object.IsCasting()) return false;

            if (attributes.AutoAction.IsAoE)
            {
                if (Environment.TickCount64 < LastHealAt + 1500) return false;
                var ret = AutoRotationHelper.ExecuteAoE(mode, preset, attributes, gameAct);
                return ret;
            }
            else
            {
                if (Environment.TickCount64 < LastHealAt + 1500) return false;
                var ret = AutoRotationHelper.ExecuteST(mode, preset, attributes, gameAct);

                return ret;
            }
        }

        public static class AutoRotationHelper
        {
            public static IGameObject? GetSingleTarget(System.Enum rotationMode)
            {
                if (rotationMode is DPSRotationMode dpsmode)
                {
                    IGameObject? target = dpsmode switch
                    {
                        DPSRotationMode.Manual => Svc.Targets.Target,
                        DPSRotationMode.Highest_Max => DPSTargeting.GetHighestMaxTarget(),
                        DPSRotationMode.Lowest_Max => DPSTargeting.GetLowestMaxTarget(),
                        DPSRotationMode.Highest_Current => DPSTargeting.GetHighestCurrentTarget(),
                        DPSRotationMode.Lowest_Current => DPSTargeting.GetLowestCurrentTarget(),
                        DPSRotationMode.Tank_Target => DPSTargeting.GetTankTarget(),
                        DPSRotationMode.Nearest => DPSTargeting.GetNearestTarget(),
                        DPSRotationMode.Furthest => DPSTargeting.GetFurthestTarget(),
                    };
                    return target;
                }
                if (rotationMode is HealerRotationMode healermode)
                {
                    if (Player.Object.GetRole() != CombatRole.Healer) return null;
                    IGameObject? target = healermode switch
                    {
                        HealerRotationMode.Manual => HealerTargeting.ManualTarget(),
                        HealerRotationMode.Highest_Current => HealerTargeting.GetHighestCurrent(),
                        HealerRotationMode.Lowest_Current => HealerTargeting.GetLowestCurrent(),
                    };

                    return target;
                }

                return null;
            }

            public static bool ExecuteAoE(Enum mode, CustomComboPreset preset, Presets.PresetAttributes attributes, uint gameAct)
            {
                if (attributes.AutoAction.IsHeal)
                {
                    uint outAct = InvokeCombo(preset, attributes, Player.Object);
                    if (!CustomComboFunctions.ActionReady(outAct))
                        return false;

                    if (HealerTargeting.CanAoEHeal(outAct))
                    {
                        var castTime = ActionManager.GetAdjustedCastTime(ActionType.Action, outAct);
                        if (CustomComboFunctions.IsMoving && castTime > 0)
                            return false;

                        var ret = ActionManager.Instance()->UseAction(ActionType.Action, outAct);

                        if (ret)
                            LastHealAt = Environment.TickCount64 + castTime;

                        return ret;
                    }
                }
                else
                {
                    uint outAct = InvokeCombo(preset, attributes, Player.Object);
                    if (!CustomComboFunctions.ActionReady(outAct))
                        return false;

                    var target = GetSingleTarget(mode);
                    var sheet = Svc.Data.GetExcelSheet<Action>().GetRow(gameAct);
                    var mustTarget = sheet.CanTargetHostile;
                    var numEnemies = CustomComboFunctions.NumberOfEnemiesInRange(gameAct, target);
                    if (numEnemies >= Service.Configuration.RotationConfig.DPSSettings.DPSAoETargets ||
                        (sheet.EffectRange == 0 && sheet.CanTargetSelf && !mustTarget))
                    {
                        var castTime = ActionManager.GetAdjustedCastTime(ActionType.Action, outAct);
                        if (CustomComboFunctions.IsMoving && castTime > 0)
                            return false;

                        if (mustTarget)
                            Svc.Targets.Target = target;

                        return ActionManager.Instance()->UseAction(ActionType.Action, outAct, mustTarget && target != null ? target.GameObjectId : Player.Object.GameObjectId);
                    }
                }
                return false;
            }

            public static bool ExecuteST(Enum mode, CustomComboPreset preset, Presets.PresetAttributes attributes, uint gameAct)
            {
                var target = GetSingleTarget(mode);
                if (target is null)
                    return false;

                var outAct = InvokeCombo(preset, attributes, target);
                var castTime = ActionManager.GetAdjustedCastTime(ActionType.Action, outAct);
                if (CustomComboFunctions.IsMoving && castTime > 0)
                    return false;

                if (outAct is SGE.Druochole && !attributes.AutoAction.IsHeal)
                {
                    if (CustomComboFunctions.GetPartyMembers().Where(x => CustomComboFunctions.FindEffectOnMember(SGE.Buffs.Kardion, x) is not null).TryGetFirst(out var newtarget))
                    {
                        Svc.Log.Debug($"DChole switch");
                        target = newtarget;
                    }
                }

                var areaTargeted = Svc.Data.GetExcelSheet<Action>().GetRow(outAct).TargetArea;
                var inRange = ActionManager.GetActionInRangeOrLoS(outAct, Player.GameObject, target.Struct()) != 562;
                var canUseTarget = ActionManager.CanUseActionOnTarget(outAct, target.Struct());
                var canUseSelf = ActionManager.CanUseActionOnTarget(outAct, Player.GameObject);

                var canUse = canUseSelf || canUseTarget || areaTargeted;

                if (canUse && inRange)
                {
                    Svc.Targets.Target = target;

                    var ret = ActionManager.Instance()->UseAction(ActionType.Action, outAct, canUseTarget ? target.GameObjectId : Player.Object.GameObjectId);
                    if (mode is HealerRotationMode && ret)
                        LastHealAt = Environment.TickCount64 + castTime;

                    return ret;
                }

                return false;
            }

            public static uint InvokeCombo(CustomComboPreset preset, Presets.PresetAttributes attributes, IGameObject? optionalTarget = null)
            {
                var outAct = attributes.ReplaceSkill.ActionIDs.FirstOrDefault();
                foreach (var actToCheck in attributes.ReplaceSkill.ActionIDs)
                {
                    var customCombo = Service.IconReplacer.CustomCombos.FirstOrDefault(x => x.Preset == preset);
                    if (customCombo != null)
                    {
                        if (customCombo.TryInvoke(actToCheck, (byte)Player.Level, ActionManager.Instance()->Combo.Action, ActionManager.Instance()->Combo.Timer, out var changedAct, optionalTarget))
                        {
                            outAct = changedAct;
                            break;
                        }
                    }
                }

                return outAct;
            }
        }

        public class DPSTargeting
        {
            public static System.Collections.Generic.IEnumerable<IGameObject> BaseSelection => Svc.Objects.Where(x => x is IBattleChara chara && x.IsHostile() && CustomComboFunctions.IsInRange(x) && !x.IsDead && x.IsTargetable && ActionManager.GetActionInRangeOrLoS(7, Player.GameObject, x.Struct()) != 562).OrderByDescending(x => IsPriority(x));

            private static bool IsPriority(IGameObject x)
            {
                bool isFate = Service.Configuration.RotationConfig.DPSSettings.FATEPriority && x.Struct()->FateId != 0;
                var namePlateIcon = x.Struct()->NamePlateIconId;
                bool isQuest = Service.Configuration.RotationConfig.DPSSettings.QuestPriority && namePlateIcon is 71204 or 71144 or 71224 or 71344;
                if (Player.Object.GetRole() is CombatRole.Tank && x.TargetObjectId != Player.Object.GameObjectId)
                    return true;

                return isFate || isQuest;
            }

            public static IGameObject? GetTankTarget()
            {
                var tank = CustomComboFunctions.GetPartyMembers().Where(x => x.GetRole() == CombatRole.Tank).FirstOrDefault();
                if (tank == null)
                    return null;

                return tank.TargetObject;
            }

            public static IGameObject? GetNearestTarget()
            {
                return BaseSelection.OrderBy(x => CustomComboFunctions.GetTargetDistance(x)).FirstOrDefault();
            }

            public static IGameObject? GetFurthestTarget()
            {
                return BaseSelection.OrderByDescending(x => CustomComboFunctions.GetTargetDistance(x)).FirstOrDefault();
            }

            public static IGameObject? GetLowestCurrentTarget()
            {
                return BaseSelection.OrderBy(x => (x as IBattleChara).CurrentHp).FirstOrDefault();
            }

            public static IGameObject? GetHighestCurrentTarget()
            {
                return BaseSelection.OrderByDescending(x => (x as IBattleChara).CurrentHp).FirstOrDefault();
            }

            public static IGameObject? GetLowestMaxTarget()
            {
                return BaseSelection.OrderBy(x => (x as IBattleChara).MaxHp).ThenBy(x => CustomComboFunctions.GetTargetHPPercent(x)).ThenBy(x => CustomComboFunctions.GetTargetDistance(x)).FirstOrDefault();
            }

            public static IGameObject? GetHighestMaxTarget()
            {
                return BaseSelection.OrderByDescending(x => (x as IBattleChara).MaxHp).ThenBy(x => CustomComboFunctions.GetTargetHPPercent(x)).FirstOrDefault();
            }
        }

        public static class HealerTargeting
        {
            internal static IGameObject? ManualTarget()
            {
                if (Svc.Targets.Target == null) return null;
                var t = Svc.Targets.Target;
                bool goodToHeal = CustomComboFunctions.GetTargetHPPercent(t) <= (TargetHasRegen(t) ? Service.Configuration.RotationConfig.HealerSettings.SingleTargetRegenHPP : Service.Configuration.RotationConfig.HealerSettings.SingleTargetHPP);
                if (goodToHeal)
                {
                    return t;
                }
                return null;
            }
            internal static IGameObject? GetHighestCurrent()
            {
                if (CustomComboFunctions.GetPartyMembers().Count == 0) return Player.Object;
                var target = CustomComboFunctions.GetPartyMembers()
                    .Where(x => !x.IsDead && x.IsTargetable && CustomComboFunctions.GetTargetHPPercent(x) <= (TargetHasRegen(x) ? Service.Configuration.RotationConfig.HealerSettings.SingleTargetRegenHPP : Service.Configuration.RotationConfig.HealerSettings.SingleTargetHPP))
                    .OrderByDescending(x => CustomComboFunctions.GetTargetHPPercent(x)).FirstOrDefault();
                return target;
            }

            internal static IGameObject? GetLowestCurrent()
            {
                if (CustomComboFunctions.GetPartyMembers().Count == 0) return Player.Object;
                var target = CustomComboFunctions.GetPartyMembers()
                    .Where(x => !x.IsDead && x.IsTargetable && CustomComboFunctions.GetTargetHPPercent(x) <= (TargetHasRegen(x) ? Service.Configuration.RotationConfig.HealerSettings.SingleTargetRegenHPP : Service.Configuration.RotationConfig.HealerSettings.SingleTargetHPP))
                    .OrderBy(x => CustomComboFunctions.GetTargetHPPercent(x)).FirstOrDefault();
                return target;
            }

            internal static bool CanAoEHeal(uint outAct = 0)
            {
                var members = CustomComboFunctions.GetPartyMembers().Where(x => (outAct == 0 ? CustomComboFunctions.GetTargetDistance(x) <= 15 : CustomComboFunctions.InActionRange(outAct, x)) && CustomComboFunctions.GetTargetHPPercent(x) <= Service.Configuration.RotationConfig.HealerSettings.AoETargetHPP);
                if (members.Count() < Service.Configuration.RotationConfig.HealerSettings.AoEHealTargetCount)
                    return false;

                return true;
            }

            private static bool TargetHasRegen(IGameObject target)
            {
                ushort regenBuff = JobID switch
                {
                    AST.JobID => AST.Buffs.AspectedBenefic,
                    WHM.JobID => WHM.Buffs.Regen,
                    _ => 0
                };

                return CustomComboFunctions.FindEffectOnMember(regenBuff, target) != null;
            }
        }

        public static class TankTargeting
        {
            public static IGameObject? GetLowestCurrentTarget()
            {
                return Svc.Objects.Where(x => x is IBattleChara && x.IsHostile() && CustomComboFunctions.IsInRange(x) && !x.IsDead && x.IsTargetable)
                    .OrderByDescending(x => x.TargetObject?.GameObjectId != Player.Object?.GameObjectId)
                    .ThenBy(x => (x as IBattleChara).CurrentHp)
                    .ThenBy(x => CustomComboFunctions.GetTargetHPPercent(x)).FirstOrDefault();
            }

            public static IGameObject? GetHighestCurrentTarget()
            {
                return Svc.Objects.Where(x => x is IBattleChara && x.IsHostile() && CustomComboFunctions.IsInRange(x) && !x.IsDead && x.IsTargetable)
                    .OrderByDescending(x => x.TargetObject?.GameObjectId != Player.Object?.GameObjectId)
                    .ThenByDescending(x => (x as IBattleChara).CurrentHp)
                    .ThenBy(x => CustomComboFunctions.GetTargetHPPercent(x)).FirstOrDefault();
            }

            public static IGameObject? GetLowestMaxTarget()
            {
                return Svc.Objects.Where(x => x is IBattleChara && x.IsHostile() && CustomComboFunctions.IsInRange(x) && !x.IsDead && x.IsTargetable)
                    .OrderByDescending(x => x.TargetObject?.GameObjectId != Player.Object?.GameObjectId)
                    .OrderBy(x => (x as IBattleChara).MaxHp)
                    .ThenBy(x => CustomComboFunctions.GetTargetHPPercent(x)).FirstOrDefault();
            }

            public static IGameObject? GetHighestMaxTarget()
            {
                return Svc.Objects.Where(x => x is IBattleChara && x.IsHostile() && CustomComboFunctions.IsInRange(x) && !x.IsDead && x.IsTargetable)
                    .OrderByDescending(x => x.TargetObject?.GameObjectId != Player.Object?.GameObjectId)
                    .ThenByDescending(x => (x as IBattleChara).MaxHp)
                    .ThenBy(x => CustomComboFunctions.GetTargetHPPercent(x)).FirstOrDefault();
            }
        }
    }
}
