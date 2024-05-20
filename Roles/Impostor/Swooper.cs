﻿using Hazel;
using System.Text;
using UnityEngine;
using static TOHE.Options;
using static TOHE.Translator;

namespace TOHE.Roles.Impostor;

internal class Swooper : RoleBase
{
    //===========================SETUP================================\\
    private const int Id = 4700;
    private static readonly HashSet<byte> playerIdList = [];
    public static bool HasEnabled => playerIdList.Any();
    public override bool IsEnable => HasEnabled;
    public override CustomRoles ThisRoleBase => CustomRoles.Impostor;
    public override Custom_RoleType ThisRoleType => Custom_RoleType.ImpostorConcealing;
    //==================================================================\\

    private static OptionItem SwooperCooldown;
    private static OptionItem SwooperDuration;
    private static OptionItem SwooperVentNormallyOnCooldown;

    private static readonly Dictionary<byte, int> ventedId = [];
    private static readonly Dictionary<byte, long> InvisCooldown = [];
    private static readonly Dictionary<byte, long> InvisDuration = [];

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Swooper);
        SwooperCooldown = FloatOptionItem.Create(Id + 2, "SwooperCooldown", new(1f, 180f, 1f), 30f, TabGroup.ImpostorRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.Swooper])
            .SetValueFormat(OptionFormat.Seconds);
        SwooperDuration = FloatOptionItem.Create(Id + 4, "SwooperDuration", new(1f, 60f, 1f), 15f, TabGroup.ImpostorRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.Swooper])
            .SetValueFormat(OptionFormat.Seconds);
        SwooperVentNormallyOnCooldown = BooleanOptionItem.Create(Id + 5, "SwooperVentNormallyOnCooldown", true, TabGroup.ImpostorRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.Swooper]);
    }
    public override void Init()
    {
        playerIdList.Clear();
        InvisCooldown.Clear();
        InvisDuration.Clear();
        ventedId.Clear();
    }
    public override void Add(byte playerId)
    {
        playerIdList.Add(playerId);
        InvisCooldown[playerId] = Utils.GetTimeStamp();
    }
    private static void SendRPC(PlayerControl pc)
    {
        if (pc.AmOwner) return;
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SyncRoleSkill, SendOption.Reliable, pc.GetClientId());
        writer.WritePacked((int)CustomRoles.Swooper);
        writer.Write((InvisCooldown.TryGetValue(pc.PlayerId, out var x) ? x : -1).ToString());
        writer.Write((InvisDuration.TryGetValue(pc.PlayerId, out var y) ? y : -1).ToString());
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    public override void ReceiveRPC(MessageReader reader, PlayerControl NaN)
    {
        InvisCooldown.Clear();
        InvisDuration.Clear();
        long cooldown = long.Parse(reader.ReadString());
        long invis = long.Parse(reader.ReadString());
        if (cooldown > 0) InvisCooldown.Add(PlayerControl.LocalPlayer.PlayerId, cooldown);
        if (invis > 0) InvisCooldown.Add(PlayerControl.LocalPlayer.PlayerId, invis);
    }

    private static bool CanGoInvis(byte id)
        => GameStates.IsInTask && !InvisCooldown.ContainsKey(id);

    private static bool IsInvis(byte id)
        => InvisDuration.ContainsKey(id);

    public override void OnEnterVent(PlayerControl swooper, Vent vent)
    {
        var swooperId = swooper.PlayerId;
        if (!IsInvis(swooperId)) return;

        InvisDuration.Remove(swooperId);
        InvisCooldown.Remove(swooperId);
        InvisCooldown.Add(swooperId, Utils.GetTimeStamp());
        SendRPC(swooper);

        swooper?.MyPhysics?.RpcBootFromVent(vent.Id);
        swooper.Notify(GetString("SwooperInvisStateOut"));
    }
    public override void OnCoEnterVent(PlayerPhysics physics, int ventId)
    {
        var swooper = physics.myPlayer;
        var swooperId = swooper.PlayerId;

        if (!AmongUsClient.Instance.AmHost || IsInvis(swooperId)) return;
        
        _ = new LateTask(() =>
        {
            if (CanGoInvis(swooperId))
            {
                ventedId.Remove(swooperId);
                ventedId.Add(swooperId, ventId);

                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(physics.NetId, (byte)RpcCalls.BootFromVent, SendOption.Reliable, swooper.GetClientId());
                writer.WritePacked(ventId);
                AmongUsClient.Instance.FinishRpcImmediately(writer);

                InvisDuration.Remove(swooperId);
                InvisDuration.Add(swooperId, Utils.GetTimeStamp());
                SendRPC(swooper);
                
                swooper.Notify(GetString("SwooperInvisState"), SwooperDuration.GetFloat());
            }
            else
            {
                if (!SwooperVentNormallyOnCooldown.GetBool())
                {
                    physics?.RpcBootFromVent(ventId);
                    swooper.Notify(GetString("SwooperInvisInCooldown"));
                }
            }
        }, 0.5f, "Swooper Vent");
    }

    public override void OnFixedUpdateLowLoad(PlayerControl player)
    {
        var nowTime = Utils.GetTimeStamp();
        var playerId = player.PlayerId;

        if (InvisCooldown.TryGetValue(playerId, out var oldTime) && (oldTime + (long)SwooperCooldown.GetFloat() - nowTime) < 0)
        {
            InvisCooldown.Remove(playerId);
            if (!player.IsModClient()) player.Notify(GetString("SwooperCanVent"));
            SendRPC(player);
        }

        foreach (var swoopInfo in InvisDuration)
        {
            var swooperId = swoopInfo.Key;
            var swooper = Utils.GetPlayerById(swooperId);
            if (swooper == null) continue;

            var remainTime = swoopInfo.Value + (long)SwooperDuration.GetFloat() - nowTime;

            if (remainTime < 0)
            {
                swooper?.MyPhysics?.RpcBootFromVent(ventedId.TryGetValue(swooperId, out var id) ? id : Main.LastEnteredVent[swooperId].Id);

                ventedId.Remove(swooperId);
                InvisDuration.Remove(swooperId);
                InvisCooldown.Add(swooperId, nowTime);
                SendRPC(swooper);

                swooper.Notify(GetString("SwooperInvisStateOut"));
                continue;
            }
            else if (remainTime <= 10)
            {
                if (!swooper.IsModClient())
                    swooper.Notify(string.Format(GetString("SwooperInvisStateCountdown"), remainTime), sendInLog: false);
            }
        }
    }

    public override bool OnCheckMurderAsKiller(PlayerControl killer, PlayerControl target)
    {
        if (!IsInvis(killer.PlayerId) || target.Is(CustomRoles.Bait)) return true;

        killer.RpcGuardAndKill(target);
        killer.SetKillCooldown();

        target.RpcMurderPlayer(target);
        target.SetRealKiller(killer);
        return false;
    }

    public override void OnReportDeadBody(PlayerControl reporter, PlayerControl target)
    {
        InvisCooldown.Clear();
        InvisDuration.Clear();

        foreach (var swooperId in playerIdList)
        {
            if (!ventedId.ContainsKey(swooperId)) continue;
            var swooper = Utils.GetPlayerById(swooperId);
            if (swooper == null) continue;

            swooper?.MyPhysics?.RpcBootFromVent(ventedId.TryGetValue(swooperId, out var id) ? id : Main.LastEnteredVent[swooperId].Id);
            SendRPC(swooper);
        }

        ventedId.Clear();
    }
    public override void AfterMeetingTasks()
    {
        InvisCooldown.Clear();
        InvisDuration.Clear();

        foreach (var swooperId in playerIdList)
        {
            var swooper = Utils.GetPlayerById(swooperId);
            if (swooper == null) continue;

            InvisCooldown.Add(swooperId, Utils.GetTimeStamp());
            SendRPC(swooper);
        }
    }

    public override string GetLowerText(PlayerControl seer, PlayerControl seen = null, bool isForMeeting = false, bool isForHud = false)
    {
        // Only for modded
        if (seer == null || !isForHud || isForMeeting || !seer.IsAlive()) return string.Empty;
        
        var str = new StringBuilder();
        var seerId = seer.PlayerId;

        if (IsInvis(seerId))
        {
            var remainTime = InvisDuration[seerId] + (long)SwooperDuration.GetFloat() - Utils.GetTimeStamp();
            str.Append(string.Format(GetString("SwooperInvisStateCountdown"), remainTime + 1));
        }
        else if (InvisCooldown.TryGetValue(seerId, out var time))
        {
            var cooldown = time + (long)SwooperCooldown.GetFloat() - Utils.GetTimeStamp();
            str.Append(string.Format(GetString("SwooperInvisCooldownRemain"), cooldown + 1));
        }
        else
        {
            str.Append(GetString("SwooperCanVent"));
        }
        return str.ToString();
    }

    public override void SetAbilityButtonText(HudManager hud, byte playerId)
    {
        hud.ImpostorVentButton?.OverrideText(GetString(IsInvis(playerId) ? "SwooperRevertVentButtonText" : "SwooperVentButtonText"));
    }
    public override Sprite ImpostorVentButtonSprite(PlayerControl player) => CustomButton.Get("invisible");
}