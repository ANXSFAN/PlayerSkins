using System.Runtime.InteropServices;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace PlayerSkins;

// 仅对“真人玩家(非 bot)”生效。写皮肤底层做法与 BotRandomizer 一致（同一 CSSharp 版本验证可用）。
// BotRandomizer gate 在 IsBot，本插件 gate 在 !IsBot，二者互不干扰。
// 支持 paint(图案) / seed(种子) / wear(磨损) / StatTrak / quality(品质) / 贴纸。
public class PlayerSkinsPlugin : BasePlugin
{
    public override string ModuleName => "PlayerSkins";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "ANXSFAN";
    public override string ModuleDescription => "给真人玩家上枪/刀/手套皮肤+种子/磨损/StatTrak/品质/贴纸（insecure 打人机自用）";

    private MemoryFunctionVoid<nint, string, float>? _setAttrByName;
    private ulong _nextItemId = 9_000_000_000uL;
    private bool _skinErrorLogged;

    private SkinConfig _cfg = new();
    private readonly HashSet<(ushort DefIndex, int Paint)> _legacyPaints = new();
    private List<SkinEntry> _skins = new();
    private List<StickerEntry> _stickers = new();

    private string ConfigPath => Path.Combine(ModuleDirectory, "config.json");
    private string LegacyDataPath => Path.Combine(ModuleDirectory, "skins_en.json");
    private string SkinsDbPath => Path.Combine(ModuleDirectory, "skins_db.json");
    private string StickersDbPath => Path.Combine(ModuleDirectory, "stickers_db.json");

    private static readonly Dictionary<string, ushort> KnifeByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bayonet"] = 500, ["css"] = 503, ["flip"] = 505, ["gut"] = 506,
        ["karambit"] = 507, ["m9"] = 508, ["m9_bayonet"] = 508, ["tactical"] = 509,
        ["huntsman"] = 509, ["falchion"] = 512, ["bowie"] = 514, ["butterfly"] = 515,
        ["push"] = 516, ["shadow"] = 516, ["cord"] = 517, ["canis"] = 518,
        ["ursus"] = 519, ["navaja"] = 520, ["gypsy"] = 520, ["outdoor"] = 521, ["nomad"] = 521,
        ["stiletto"] = 522, ["talon"] = 523, ["widowmaker"] = 523, ["skeleton"] = 525, ["kukri"] = 526,
    };

    public override void Load(bool hotReload)
    {
        _skinErrorLogged = false;
        LoadConfig();
        LoadLegacyPaints();
        _skins = LoadDb<SkinEntry>(SkinsDbPath, "皮肤");
        _stickers = LoadDb<StickerEntry>(StickersDbPath, "贴纸");

        try
        {
            _setAttrByName = new MemoryFunctionVoid<nint, string, float>(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? "55 48 89 E5 41 57 41 56 49 89 FE 41 55 41 54 53 48 89 F3 48 83 EC ? F3 0F 11 85"
                    : "40 53 55 41 56 48 81 EC 90 00 00 00");
        }
        catch (Exception ex)
        {
            _setAttrByName = null;
            Logger.LogError("[PlayerSkins] 特征码定位失败，皮肤功能不可用: " + ex.Message);
        }

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Post);
        VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPost, HookMode.Post);

        AddCommand("css_skin", "手持武器上皮肤: !skin <paintId>", CmdSkin);
        AddCommand("css_seed", "图案种子: !seed <值>", CmdSeed);
        AddCommand("css_wear", "磨损: !wear <0-1>", CmdWear);
        AddCommand("css_stattrak", "StatTrak: !stattrak <数|off>", CmdStatTrak);
        AddCommand("css_st", "!stattrak 简写", CmdStatTrak);
        AddCommand("css_quality", "品质: !quality <normal|stattrak|souvenir|star>", CmdQuality);
        AddCommand("css_sticker", "贴纸: !sticker <槽0-4> <id|clear> [磨损] [缩放] [旋转]", CmdSticker);
        AddCommand("css_stickerclear", "清空手持武器全部贴纸: !stickerclear", CmdStickerClear);
        AddCommand("css_sc", "!stickerclear 简写", CmdStickerClear);
        AddCommand("css_knife", "换刀: !knife <名字|defindex> [paintId]", CmdKnife);
        AddCommand("css_gloves", "换手套: !gloves <defindex> <paintId>", CmdGloves);
        AddCommand("css_reskin", "立即重新应用全部皮肤", CmdReskin);
        AddCommand("css_clearskins", "清空全部皮肤设置", CmdClear);
        AddCommand("css_skinsearch", "搜皮肤代号: !skinsearch <关键词>", CmdSkinSearch);
        AddCommand("css_ss", "!skinsearch 简写", CmdSkinSearch);
        AddCommand("css_stickersearch", "搜贴纸代号: !stickersearch <关键词>", CmdStickerSearch);
        AddCommand("css_sss", "!stickersearch 简写", CmdStickerSearch);

        Logger.LogInformation("[PlayerSkins] 已加载，仅作用于真人玩家。");
    }

    public override void Unload(bool hotReload)
    {
        VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPost, HookMode.Post);
    }

    // ---------------- 事件 / 钩子 ----------------

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsRealPlayer(player)) return HookResult.Continue;
        var pawn = player!.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        var p = player; var pw = pawn;
        Server.NextFrame(() => ApplyAll(p, pw));
        AddTimer(0.1f, () => { if (pw.IsValid) ApplyAll(p, pw); });
        AddTimer(0.25f, () => { if (pw.IsValid) ApplyAll(p, pw); });
        return HookResult.Continue;
    }

    private HookResult OnGiveNamedItemPost(DynamicHook hook)
    {
        if (_setAttrByName == null) return HookResult.Continue;
        try
        {
            var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
            var weapon = hook.GetReturn<CBasePlayerWeapon>();
            if (weapon == null || !weapon.IsValid) return HookResult.Continue;
            var designer = weapon.DesignerName;
            if (string.IsNullOrEmpty(designer) || !designer.Contains("weapon")) return HookResult.Continue;

            var player = GetPlayerFromItemServices(itemServices);
            if (!IsRealPlayer(player)) return HookResult.Continue;

            var w = weapon;
            ApplyToWeapon(w);
            Server.NextFrame(() => { if (w.IsValid) ApplyToWeapon(w); });
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] OnGiveNamedItemPost 失败: " + ex.Message);
        }
        return HookResult.Continue;
    }

    private static CCSPlayerController? GetPlayerFromItemServices(CCSPlayer_ItemServices itemServices)
    {
        var pawn = itemServices.Pawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.Controller.Value == null || !pawn.Controller.IsValid)
            return null;
        var ctrl = new CCSPlayerController(pawn.Controller.Value.Handle);
        return ctrl.IsValid ? ctrl : null;
    }

    // ---------------- 应用 ----------------

    private void ApplyAll(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        if (!IsRealPlayer(player) || pawn == null || !pawn.IsValid) return;
        if (_cfg.KnifeDef > 0) ApplyKnife(pawn, _cfg.KnifeDef, _cfg.Knife);
        if (_cfg.GloveDef > 0) ApplyGloves(pawn, _cfg.GloveDef, _cfg.Gloves);
        var ws = pawn.WeaponServices;
        if (ws == null) return;
        foreach (var h in ws.MyWeapons) ApplyToWeapon(h.Value);
    }

    private void ApplyToWeapon(CBasePlayerWeapon? weapon)
    {
        if (_setAttrByName == null || weapon == null || !weapon.IsValid) return;
        var designer = weapon.DesignerName;
        if (string.IsNullOrEmpty(designer)) return;
        if (designer.Contains("knife") || designer == "weapon_bayonet") return; // 刀单独处理

        var item = weapon.AttributeManager?.Item;
        if (item == null) return;
        ushort def = item.ItemDefinitionIndex;
        if (def != 0 && _cfg.Guns.TryGetValue(def, out var lo))
            ApplyLoadout(weapon, def, lo, isKnife: false);
    }

    // 统一写入 paint/seed/wear/stattrak/quality/贴纸
    private void ApplyLoadout(CEconEntity weapon, ushort defIndex, Loadout lo, bool isKnife)
    {
        if (_setAttrByName == null) return;
        try
        {
            var item = weapon.AttributeManager?.Item;
            if (item == null) return;

            item.AttributeList.Attributes.RemoveAll();
            item.NetworkedDynamicAttributes.Attributes.RemoveAll();
            AssignItemId(item);

            // 品质：StatTrak 需 strange(9)，刀默认 ★(3)，纪念品(12) 等
            int q = lo.Quality;
            if (lo.StatTrak >= 0 && q < 0) q = 9;
            if (isKnife && q < 0) q = 3;
            if (q >= 0) item.EntityQuality = q;

            weapon.FallbackPaintKit = lo.Paint;
            weapon.FallbackSeed = lo.Seed;
            weapon.FallbackWear = lo.Wear;
            weapon.FallbackStatTrak = lo.StatTrak;

            WriteAttrs(item.NetworkedDynamicAttributes.Handle, lo, isKnife, applyStickers: true);
            WriteAttrs(item.AttributeList.Handle, lo, isKnife, applyStickers: false);
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");

            if (!isKnife)
            {
                bool legacy = _legacyPaints.Contains((defIndex, lo.Paint));
                weapon.AcceptInput("SetBodygroup", null, null, $"body,{(legacy ? 1 : 0)}");
            }
        }
        catch (Exception ex)
        {
            if (!_skinErrorLogged) { _skinErrorLogged = true; Logger.LogError("[PlayerSkins] ApplyLoadout 失败: " + ex.Message); }
        }
    }

    private void WriteAttrs(nint handle, Loadout lo, bool isKnife, bool applyStickers)
    {
        _setAttrByName!.Invoke(handle, "set item texture prefab", lo.Paint);
        _setAttrByName!.Invoke(handle, "set item texture seed", lo.Seed);
        _setAttrByName!.Invoke(handle, "set item texture wear", lo.Wear);
        if (lo.StatTrak >= 0)
        {
            // kill eater 计数按“位重解释”传，否则计数器显示乱码数字
            _setAttrByName!.Invoke(handle, "kill eater", ViewAsFloat((uint)lo.StatTrak));
            _setAttrByName!.Invoke(handle, "kill eater score type", 0);
        }
        // 贴纸：只写 NetworkedDynamicAttributes，且必须带 offset x/y（否则不渲染）——与 WeaponPaints 一致。刀不支持。
        if (applyStickers && !isKnife && lo.Stickers != null)
        {
            foreach (var kv in lo.Stickers)
            {
                int slot = kv.Key; var s = kv.Value;
                // 贴纸 id 必须“位重解释”传（属性按 uint 位读），传普通数字会变成无效贴纸 -> 不显示
                _setAttrByName!.Invoke(handle, $"sticker slot {slot} id", ViewAsFloat((uint)s.Id));
                if (s.OffsetX != 0f || s.OffsetY != 0f)
                    _setAttrByName!.Invoke(handle, $"sticker slot {slot} schema", 0f);
                _setAttrByName!.Invoke(handle, $"sticker slot {slot} offset x", s.OffsetX);
                _setAttrByName!.Invoke(handle, $"sticker slot {slot} offset y", s.OffsetY);
                _setAttrByName!.Invoke(handle, $"sticker slot {slot} wear", s.Wear);
                _setAttrByName!.Invoke(handle, $"sticker slot {slot} scale", s.Scale);
                _setAttrByName!.Invoke(handle, $"sticker slot {slot} rotation", s.Rotation);
            }
        }
    }

    private void ApplyKnife(CCSPlayerPawn pawn, ushort defIndex, Loadout lo)
    {
        try
        {
            var ws = pawn.WeaponServices;
            if (ws == null) return;
            foreach (var h in ws.MyWeapons)
            {
                var w = h.Value;
                if (w == null || !w.IsValid) continue;
                var designer = w.DesignerName;
                if (string.IsNullOrEmpty(designer) || (!designer.Contains("knife") && designer != "weapon_bayonet")) continue;

                w.AcceptInput("ChangeSubclass", null, null, defIndex.ToString());
                var item = w.AttributeManager?.Item;
                if (item != null)
                {
                    item.ItemDefinitionIndex = defIndex;
                    ApplyLoadout(w, defIndex, lo, isKnife: true);
                }
                break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] ApplyKnife 失败: " + ex.Message);
        }
    }

    private void ApplyGloves(CCSPlayerPawn pawn, ushort defIndex, Loadout lo)
    {
        if (_setAttrByName == null) return;
        try
        {
            var gloves = pawn.EconGloves;
            gloves.NetworkedDynamicAttributes.Attributes.RemoveAll();
            gloves.AttributeList.Attributes.RemoveAll();
            gloves.ItemDefinitionIndex = defIndex;
            AssignItemId(gloves);
            _setAttrByName.Invoke(gloves.NetworkedDynamicAttributes.Handle, "set item texture prefab", lo.Paint);
            _setAttrByName.Invoke(gloves.NetworkedDynamicAttributes.Handle, "set item texture seed", lo.Seed);
            _setAttrByName.Invoke(gloves.NetworkedDynamicAttributes.Handle, "set item texture wear", lo.Wear);
            _setAttrByName.Invoke(gloves.AttributeList.Handle, "set item texture prefab", lo.Paint);
            _setAttrByName.Invoke(gloves.AttributeList.Handle, "set item texture seed", lo.Seed);
            _setAttrByName.Invoke(gloves.AttributeList.Handle, "set item texture wear", lo.Wear);
            gloves.Initialized = true;
            pawn.AcceptInput("SetBodygroup", null, null, "first_or_third_person,0");
            var p = pawn;
            AddTimer(0.2f, () => { if (p.IsValid) p.AcceptInput("SetBodygroup", null, null, "first_or_third_person,1"); });
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] ApplyGloves 失败: " + ex.Message);
        }
    }

    // 把 uint 的二进制位原样当作 float 传给属性函数（贴纸id/StatTrak计数等按整数位读取的属性用）
    private static float ViewAsFloat(uint value) => BitConverter.Int32BitsToSingle((int)value);

    private void AssignItemId(CEconItemView item)
    {
        ulong id = _nextItemId++;
        item.ItemID = id;
        item.ItemIDLow = (uint)(id & 0xFFFFFFFFu);
        item.ItemIDHigh = (uint)(id >> 32);
    }

    // ---------------- 指令：作用于手持武器 ----------------

    // 取当前手持武器的 Loadout（不存在则新建），返回是否成功
    private bool HeldLoadout(CCSPlayerController player, out Loadout lo, out CBasePlayerWeapon weapon, out CCSPlayerPawn pawn, out bool isKnife)
    {
        lo = null!; weapon = null!; pawn = null!; isKnife = false;
        var pw = player.PlayerPawn?.Value;
        if (pw == null || !pw.IsValid) { Reply(player, "找不到角色，重生后再试"); return false; }
        var active = pw.WeaponServices?.ActiveWeapon?.Value;
        if (active == null || !active.IsValid) { Reply(player, "拿着武器再输指令"); return false; }
        var designer = active.DesignerName ?? "";
        isKnife = designer.Contains("knife") || designer == "weapon_bayonet";
        var item = active.AttributeManager?.Item;
        if (item == null) { Reply(player, "读取武器失败"); return false; }
        ushort def = item.ItemDefinitionIndex;

        pawn = pw; weapon = active;
        if (isKnife)
        {
            if (_cfg.KnifeDef == 0) _cfg.KnifeDef = def;
            lo = _cfg.Knife;
        }
        else
        {
            if (!_cfg.Guns.TryGetValue(def, out lo!)) { lo = new Loadout(); _cfg.Guns[def] = lo; }
        }
        return true;
    }

    private void ReapplyHeld(CCSPlayerController player, CBasePlayerWeapon weapon, CCSPlayerPawn pawn, bool isKnife)
    {
        if (isKnife) ApplyKnife(pawn, _cfg.KnifeDef, _cfg.Knife);
        else ApplyToWeapon(weapon);
    }

    private void CmdSkin(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2 || !int.TryParse(info.GetArg(1), out int paint)) { Reply(player!, "用法: !skin <paintId>（手持武器）"); return; }
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        lo.Paint = paint;
        SaveConfig(); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, $"已上皮肤 {paint}" + (isKnife ? "（刀）" : ""));
    }

    private void CmdSeed(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2 || !int.TryParse(info.GetArg(1), out int seed)) { Reply(player!, "用法: !seed <值>，如淬火蓝宝石 !seed 661"); return; }
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        lo.Seed = seed;
        SaveConfig(); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, $"图案种子已设为 {seed}");
    }

    private void CmdWear(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2 || !float.TryParse(info.GetArg(1), out float wear)) { Reply(player!, "用法: !wear <0-1>，0=崭新 1=战痕"); return; }
        wear = Math.Clamp(wear, 0f, 1f);
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        lo.Wear = wear;
        SaveConfig(); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, $"磨损已设为 {wear}");
    }

    private void CmdStatTrak(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2) { Reply(player!, "用法: !stattrak <数字|off>"); return; }
        int st;
        var a = info.GetArg(1).ToLowerInvariant();
        if (a is "off" or "关" or "-1") st = -1;
        else if (!int.TryParse(a, out st) || st < 0) { Reply(player!, "数字≥0，或 off 关闭"); return; }
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        lo.StatTrak = st;
        SaveConfig(); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, st < 0 ? "已关闭 StatTrak" : $"StatTrak 计数 = {st}（金色计数器）");
    }

    private void CmdQuality(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2) { Reply(player!, "用法: !quality <normal|stattrak|souvenir|star>"); return; }
        int q = info.GetArg(1).ToLowerInvariant() switch
        {
            "normal" or "普通" or "none" => -1,
            "stattrak" or "st" or "暗金" => 9,
            "souvenir" or "纪念品" or "sv" => 12,
            "star" or "unusual" or "★" or "刀" => 3,
            "genuine" or "正品" => 1,
            _ => -2
        };
        if (q == -2) { Reply(player!, "品质: normal/stattrak/souvenir/star"); return; }
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        lo.Quality = q;
        SaveConfig(); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, $"品质已设为 {info.GetArg(1)}（金铭牌等，重生后生效更稳）");
    }

    private void CmdSticker(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 3 || !int.TryParse(info.GetArg(1), out int slot) || slot < 0 || slot > 4)
        { Reply(player!, "用法: !sticker <槽0-4> <id|clear> [磨损0-1] [缩放] [旋转]"); return; }
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        if (isKnife) { Reply(player!, "刀不能贴贴纸"); return; }

        var idArg = info.GetArg(2).ToLowerInvariant();
        if (idArg is "clear" or "clr" or "0" or "删")
        {
            lo.Stickers.Remove(slot);
            SaveConfig(); ReapplyHeld(player!, w, pawn, isKnife);
            Reply(player!, $"已清除槽位 {slot} 的贴纸（重生后彻底消失）");
            return;
        }
        if (!int.TryParse(idArg, out int id) || id <= 0) { Reply(player!, "贴纸 id 必须是正整数，或 clear 清除"); return; }
        var s = new Sticker { Id = id };
        if (info.ArgCount >= 4 && float.TryParse(info.GetArg(3), out float sw)) s.Wear = Math.Clamp(sw, 0f, 1f);
        if (info.ArgCount >= 5 && float.TryParse(info.GetArg(4), out float sc)) s.Scale = sc;
        if (info.ArgCount >= 6 && float.TryParse(info.GetArg(5), out float ro)) s.Rotation = ro;
        lo.Stickers[slot] = s;
        SaveConfig(); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, $"槽位 {slot} 贴纸 id={id} 已贴");
    }

    private void CmdStickerClear(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        if (isKnife) { Reply(player!, "刀没有贴纸"); return; }
        lo.Stickers.Clear();
        // 立即把 4 个槽位 id 置 0（配合重生彻底清除）
        var item = w.AttributeManager?.Item;
        if (_setAttrByName != null && item != null)
            for (int s = 0; s < 5; s++)
                _setAttrByName.Invoke(item.NetworkedDynamicAttributes.Handle, $"sticker slot {s} id", 0);
        SaveConfig(); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, "已清空该武器全部贴纸（重生后彻底消失）");
    }

    private void CmdKnife(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2) { Reply(player!, "用法: !knife <名字|defindex> [paintId]"); return; }
        string a = info.GetArg(1);
        if (!ushort.TryParse(a, out ushort def) && !KnifeByName.TryGetValue(a, out def)) { Reply(player!, "认不出这把刀: " + a); return; }
        _cfg.KnifeDef = def;
        if (info.ArgCount >= 3 && int.TryParse(info.GetArg(2), out int p)) _cfg.Knife.Paint = p;
        SaveConfig();
        var pawn = player!.PlayerPawn?.Value;
        if (pawn != null && pawn.IsValid) ApplyKnife(pawn, def, _cfg.Knife);
        Reply(player!, $"刀已设为 defindex {def}，皮肤 {_cfg.Knife.Paint}");
    }

    private void CmdGloves(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 3 || !ushort.TryParse(info.GetArg(1), out ushort def) || !int.TryParse(info.GetArg(2), out int paint))
        { Reply(player!, "用法: !gloves <defindex> <paintId>，如 !gloves 5030 10048"); return; }
        _cfg.GloveDef = def; _cfg.Gloves.Paint = paint;
        SaveConfig();
        var pawn = player!.PlayerPawn?.Value;
        if (pawn != null && pawn.IsValid) ApplyGloves(pawn, def, _cfg.Gloves);
        Reply(player!, $"手套已设为 defindex {def}，皮肤 {paint}");
    }

    private void CmdReskin(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        var pawn = player!.PlayerPawn?.Value;
        if (pawn != null && pawn.IsValid) ApplyAll(player, pawn);
        Reply(player!, "已重新应用全部皮肤");
    }

    private void CmdClear(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        _cfg = new SkinConfig();
        SaveConfig();
        Reply(player!, "已清空全部皮肤设置（下回合或重连生效）");
    }

    // ---------------- 搜索 ----------------

    private void CmdSkinSearch(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2) { Reply(player!, "用法: !ss <关键词>，如 !ss 多普勒 / !ss awp 龙"); return; }
        if (_skins.Count == 0) { Reply(player!, "皮肤库未加载（缺 skins_db.json）"); return; }
        var keys = ArgsToKeys(info);
        var hits = _skins.Where(e => keys.All(k => ((e.w ?? "") + " " + (e.cn ?? "") + " " + (e.en ?? "")).ToLowerInvariant().Contains(k))).Take(10).ToList();
        if (hits.Count == 0) { Reply(player!, "没找到，换个关键词"); return; }
        Reply(player!, $"皮肤 {hits.Count} 个：");
        foreach (var e in hits) player!.PrintToChat($" \x06{e.p}\x01  {e.w} | {e.cn}");
        Reply(player!, "手持武器后 !skin <代号>");
    }

    private void CmdStickerSearch(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2) { Reply(player!, "用法: !sss <关键词>，如 !sss 皇冠 / !sss katowice 2014"); return; }
        if (_stickers.Count == 0) { Reply(player!, "贴纸库未加载（缺 stickers_db.json）"); return; }
        var keys = ArgsToKeys(info);
        var hits = _stickers.Where(e => keys.All(k => ((e.cn ?? "") + " " + (e.en ?? "")).ToLowerInvariant().Contains(k))).Take(10).ToList();
        if (hits.Count == 0) { Reply(player!, "没找到，换个关键词"); return; }
        Reply(player!, $"贴纸 {hits.Count} 个：");
        foreach (var e in hits) player!.PrintToChat($" \x06{e.i}\x01  {e.cn}");
        Reply(player!, "手持武器后 !sticker <槽0-4> <id>");
    }

    private static List<string> ArgsToKeys(CommandInfo info)
    {
        var keys = new List<string>();
        for (int i = 1; i < info.ArgCount; i++) keys.Add(info.GetArg(i).ToLowerInvariant());
        return keys;
    }

    // ---------------- 工具 ----------------

    private static bool IsRealPlayer(CCSPlayerController? player)
        => player != null && player.IsValid && !player.IsBot && !player.IsHLTV;

    private void Reply(CCSPlayerController player, string msg) => player.PrintToChat($" \x04[皮肤]\x01 {msg}");

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) { _cfg = new SkinConfig(); return; }
            var text = File.ReadAllText(ConfigPath);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("Guns", out _))
                _cfg = JsonSerializer.Deserialize<SkinConfig>(text) ?? new SkinConfig();
            else
                _cfg = MigrateOld(doc.RootElement);   // 旧格式（GunPaints/KnifePaint...）
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] 读取 config.json 失败: " + ex.Message);
            _cfg = new SkinConfig();
        }
    }

    // 兼容 v1.0.0 的旧配置：GunPaints/KnifeDef/KnifePaint/GloveDef/GlovePaint/Seed/Wear
    private static SkinConfig MigrateOld(JsonElement root)
    {
        var cfg = new SkinConfig();
        int seed = root.TryGetProperty("Seed", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
        float wear = root.TryGetProperty("Wear", out var wv) && wv.ValueKind == JsonValueKind.Number ? wv.GetSingle() : 0.0001f;
        if (root.TryGetProperty("GunPaints", out var gp) && gp.ValueKind == JsonValueKind.Object)
            foreach (var kv in gp.EnumerateObject())
                if (ushort.TryParse(kv.Name, out var def) && kv.Value.ValueKind == JsonValueKind.Number)
                    cfg.Guns[def] = new Loadout { Paint = kv.Value.GetInt32(), Seed = seed, Wear = wear };
        if (root.TryGetProperty("KnifeDef", out var kd) && kd.ValueKind == JsonValueKind.Number) cfg.KnifeDef = (ushort)kd.GetInt32();
        if (root.TryGetProperty("KnifePaint", out var kp) && kp.ValueKind == JsonValueKind.Number) cfg.Knife.Paint = kp.GetInt32();
        cfg.Knife.Seed = seed; cfg.Knife.Wear = wear;
        if (root.TryGetProperty("GloveDef", out var gd) && gd.ValueKind == JsonValueKind.Number) cfg.GloveDef = (ushort)gd.GetInt32();
        if (root.TryGetProperty("GlovePaint", out var gpn) && gpn.ValueKind == JsonValueKind.Number) cfg.Gloves.Paint = gpn.GetInt32();
        return cfg;
    }

    private void SaveConfig()
    {
        try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Logger.LogError("[PlayerSkins] 写入 config.json 失败: " + ex.Message); }
    }

    private List<T> LoadDb<T>(string path, string label)
    {
        try
        {
            if (!File.Exists(path)) { Logger.LogWarning($"[PlayerSkins] 未找到 {Path.GetFileName(path)}，{label}搜索不可用"); return new(); }
            var list = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? new();
            Logger.LogInformation($"[PlayerSkins] {label}库已加载 {list.Count} 条");
            return list;
        }
        catch (Exception ex) { Logger.LogError($"[PlayerSkins] 读取 {Path.GetFileName(path)} 失败: " + ex.Message); return new(); }
    }

    private void LoadLegacyPaints()
    {
        _legacyPaints.Clear();
        try
        {
            if (!File.Exists(LegacyDataPath)) { Logger.LogWarning("[PlayerSkins] 未找到 skins_en.json，legacy 模型位置可能不准"); return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(LegacyDataPath));
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.TryGetProperty("legacy_model", out var lm) && lm.ValueKind == JsonValueKind.True
                    && el.TryGetProperty("weapon_defindex", out var di) && el.TryGetProperty("paint", out var pk))
                    _legacyPaints.Add(((ushort)ReadInt(di), ReadInt(pk)));
        }
        catch (Exception ex) { Logger.LogError("[PlayerSkins] LoadLegacyPaints 失败: " + ex.Message); }

        static int ReadInt(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt32() : (int.TryParse(e.GetString(), out var r) ? r : 0);
    }

    // ---------------- 数据结构 ----------------

    private class SkinEntry { public int p { get; set; } public string? w { get; set; } public string? en { get; set; } public string? cn { get; set; } }
    private class StickerEntry { public int i { get; set; } public string? en { get; set; } public string? cn { get; set; } }

    private class Sticker
    {
        public int Id { get; set; }
        public float Wear { get; set; } = 0f;
        public float Scale { get; set; } = 1f;
        public float Rotation { get; set; } = 0f;
        public float OffsetX { get; set; } = 0f;
        public float OffsetY { get; set; } = 0f;
    }

    private class Loadout
    {
        public int Paint { get; set; } = 0;
        public int Seed { get; set; } = 0;
        public float Wear { get; set; } = 0.0001f;
        public int StatTrak { get; set; } = -1;      // -1 = 关闭
        public int Quality { get; set; } = -1;        // -1 = 不覆盖
        public Dictionary<int, Sticker> Stickers { get; set; } = new();
    }

    private class SkinConfig
    {
        public Dictionary<ushort, Loadout> Guns { get; set; } = new();
        public ushort KnifeDef { get; set; } = 0;
        public Loadout Knife { get; set; } = new();
        public ushort GloveDef { get; set; } = 0;
        public Loadout Gloves { get; set; } = new();
    }
}
