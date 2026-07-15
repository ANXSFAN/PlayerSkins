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

// 仅对“真人玩家(非 bot)”生效，写皮肤的底层做法与 BotRandomizer 完全一致（同一 1.0.369 版本验证可用）。
// BotRandomizer 全程 gate 在 player.IsBot，本插件全程 gate 在 !player.IsBot，二者互不干扰。
public class PlayerSkinsPlugin : BasePlugin
{
    public override string ModuleName => "PlayerSkins";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "self";
    public override string ModuleDescription => "给真人玩家上枪/刀/手套皮肤（insecure 打人机自用）";

    private MemoryFunctionVoid<nint, string, float>? _setAttrByName;
    private ulong _nextItemId = 9_000_000_000uL; // 与 BotRandomizer (4027435774) 区间错开，避免 ItemID 撞车
    private bool _skinErrorLogged;

    // 玩家选择的配置（持久化到 config.json）
    private SkinConfig _cfg = new();

    // skins_en.json 里 legacy_model=true 的 (defindex,paint)，用于 SetBodygroup body 切换
    private readonly HashSet<(ushort DefIndex, int Paint)> _legacyPaints = new();

    // skins_db.json：paint_index -> 武器/中英文名，用于 !skinsearch
    private List<SkinEntry> _db = new();

    private string ConfigPath => Path.Combine(ModuleDirectory, "config.json");
    private string SkinsDbPath => Path.Combine(ModuleDirectory, "skins_en.json");
    private string SearchDbPath => Path.Combine(ModuleDirectory, "skins_db.json");

    // 常用刀名 -> defindex（取自 BotRandomizer）
    private static readonly Dictionary<string, ushort> KnifeByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bayonet"] = 500, ["css"] = 503, ["flip"] = 505, ["gut"] = 506,
        ["karambit"] = 507, ["m9"] = 508, ["m9_bayonet"] = 508, ["tactical"] = 509,
        ["huntsman"] = 509, ["falchion"] = 512, ["bowie"] = 514, ["butterfly"] = 515,
        ["push"] = 516, ["shadow"] = 516, ["cord"] = 517, ["canis"] = 518,
        ["ursus"] = 519, ["navaja"] = 520, ["gypsy"] = 520, ["outdoor"] = 521, ["nomad"] = 521,
        ["stiletto"] = 522, ["talon"] = 523, ["widowmaker"] = 523, ["skeleton"] = 525, ["kukri"] = 526,
    };

    private static readonly Dictionary<string, ushort> GunByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deagle"] = 1, ["elite"] = 2, ["fiveseven"] = 3, ["glock"] = 4,
        ["ak47"] = 7, ["ak"] = 7, ["aug"] = 8, ["awp"] = 9, ["famas"] = 10,
        ["g3sg1"] = 11, ["galilar"] = 13, ["galil"] = 13, ["m249"] = 14, ["m4a1"] = 16, ["m4a4"] = 16,
        ["mac10"] = 17, ["p90"] = 19, ["mp5"] = 23, ["mp5sd"] = 23, ["ump45"] = 24, ["ump"] = 24,
        ["xm1014"] = 25, ["bizon"] = 26, ["mag7"] = 27, ["negev"] = 28, ["sawedoff"] = 29,
        ["tec9"] = 30, ["nova"] = 35, ["p250"] = 36, ["mp7"] = 38, ["mp9"] = 39, ["scar20"] = 40,
        ["sg556"] = 60, ["ssg08"] = 61, ["m4a1s"] = 60, ["m4a1_silencer"] = 60,
        ["p2000"] = 32, ["usp"] = 61, ["usps"] = 61, ["cz75"] = 63, ["revolver"] = 64,
    };

    public override void Load(bool hotReload)
    {
        _skinErrorLogged = false;
        LoadConfig();
        LoadLegacyPaints();
        LoadSearchDb();

        try
        {
            // CAttributeList_SetOrAddAttributeValueByName 特征码（与 BotRandomizer 相同）
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

        AddCommand("css_skin", "给手持武器上皮肤: !skin <paintId>", CmdSkin);
        AddCommand("css_knife", "换刀: !knife <名字|defindex> [paintId]", CmdKnife);
        AddCommand("css_gloves", "换手套: !gloves <defindex> <paintId>", CmdGloves);
        AddCommand("css_reskin", "立即重新应用全部皮肤", CmdReskin);
        AddCommand("css_clearskins", "清空全部皮肤设置", CmdClear);
        AddCommand("css_skinsearch", "按名字搜皮肤代号: !skinsearch <关键词>", CmdSearch);
        AddCommand("css_ss", "!skinsearch 的简写", CmdSearch);

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

        var p = player;
        var pw = pawn;
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

    // ---------------- 应用皮肤 ----------------

    private void ApplyAll(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        if (!IsRealPlayer(player) || pawn == null || !pawn.IsValid) return;

        if (_cfg.KnifeDef > 0)
            ApplyKnife(pawn, _cfg.KnifeDef, _cfg.KnifePaint);

        if (_cfg.GloveDef > 0)
            ApplyGloves(pawn, _cfg.GloveDef, _cfg.GlovePaint);

        var ws = pawn.WeaponServices;
        if (ws == null) return;
        foreach (var h in ws.MyWeapons)
            ApplyToWeapon(h.Value);
    }

    // 根据武器自身 defindex 决定上枪皮还是刀皮
    private void ApplyToWeapon(CBasePlayerWeapon? weapon)
    {
        if (_setAttrByName == null || weapon == null || !weapon.IsValid) return;
        var designer = weapon.DesignerName;
        if (string.IsNullOrEmpty(designer)) return;

        bool isKnife = designer.Contains("knife") || designer == "weapon_bayonet";
        if (isKnife) return; // 刀由 ApplyKnife 统一处理

        var item = weapon.AttributeManager?.Item;
        if (item == null) return;
        ushort def = item.ItemDefinitionIndex;
        if (def != 0 && _cfg.GunPaints.TryGetValue(def, out int paint))
            ApplySkinToWeapon(weapon, def, paint);
    }

    private void ApplySkinToWeapon(CEconEntity weapon, ushort defIndex, int paintKit)
    {
        if (_setAttrByName == null) return;
        try
        {
            var item = weapon.AttributeManager?.Item;
            if (item == null) return;

            item.AttributeList.Attributes.RemoveAll();
            item.NetworkedDynamicAttributes.Attributes.RemoveAll();
            AssignItemId(item);
            weapon.FallbackPaintKit = paintKit;
            weapon.FallbackSeed = _cfg.Seed;
            weapon.FallbackWear = _cfg.Wear;
            SetTexture(item.NetworkedDynamicAttributes.Handle, paintKit);
            SetTexture(item.AttributeList.Handle, paintKit);
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");

            bool legacy = _legacyPaints.Contains((defIndex, paintKit));
            weapon.AcceptInput("SetBodygroup", null, null, $"body,{(legacy ? 1 : 0)}");
        }
        catch (Exception ex)
        {
            if (!_skinErrorLogged)
            {
                _skinErrorLogged = true;
                Logger.LogError("[PlayerSkins] ApplySkinToWeapon 失败: " + ex.Message);
            }
        }
    }

    private void ApplyKnife(CCSPlayerPawn pawn, ushort defIndex, int paintKit)
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
                if (string.IsNullOrEmpty(designer) || (!designer.Contains("knife") && designer != "weapon_bayonet"))
                    continue;

                w.AcceptInput("ChangeSubclass", null, null, defIndex.ToString());
                var item = w.AttributeManager?.Item;
                if (item != null)
                {
                    item.ItemDefinitionIndex = defIndex;
                    item.EntityQuality = 3;
                    item.AttributeList.Attributes.RemoveAll();
                    item.NetworkedDynamicAttributes.Attributes.RemoveAll();
                    AssignItemId(item);
                    if (_setAttrByName != null && paintKit > 0)
                    {
                        w.FallbackPaintKit = paintKit;
                        w.FallbackSeed = _cfg.Seed;
                        w.FallbackWear = _cfg.Wear;
                        SetTexture(item.NetworkedDynamicAttributes.Handle, paintKit);
                        SetTexture(item.AttributeList.Handle, paintKit);
                    }
                    Utilities.SetStateChanged(w, "CEconEntity", "m_AttributeManager");
                }
                break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] ApplyKnife 失败: " + ex.Message);
        }
    }

    private void ApplyGloves(CCSPlayerPawn pawn, ushort defIndex, int paintKit)
    {
        if (_setAttrByName == null) return;
        try
        {
            var gloves = pawn.EconGloves;
            gloves.NetworkedDynamicAttributes.Attributes.RemoveAll();
            gloves.AttributeList.Attributes.RemoveAll();
            gloves.ItemDefinitionIndex = defIndex;
            AssignItemId(gloves);
            SetTexture(gloves.NetworkedDynamicAttributes.Handle, paintKit);
            SetTexture(gloves.AttributeList.Handle, paintKit);
            gloves.Initialized = true;
            pawn.AcceptInput("SetBodygroup", null, null, "first_or_third_person,0");
            var p = pawn;
            AddTimer(0.2f, () =>
            {
                if (p.IsValid)
                    p.AcceptInput("SetBodygroup", null, null, "first_or_third_person,1");
            });
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] ApplyGloves 失败: " + ex.Message);
        }
    }

    private void SetTexture(nint handle, int paintKit)
    {
        _setAttrByName!.Invoke(handle, "set item texture prefab", paintKit);
        _setAttrByName!.Invoke(handle, "set item texture seed", _cfg.Seed);
        _setAttrByName!.Invoke(handle, "set item texture wear", _cfg.Wear);
    }

    private void AssignItemId(CEconItemView item)
    {
        ulong id = _nextItemId++;
        item.ItemID = id;
        item.ItemIDLow = (uint)(id & 0xFFFFFFFFu);
        item.ItemIDHigh = (uint)(id >> 32);
    }

    // ---------------- 指令 ----------------

    private void CmdSkin(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2) { Reply(player!, "用法: !skin <paintId>（手持要改的枪再输）"); return; }
        if (!int.TryParse(info.GetArg(1), out int paint)) { Reply(player!, "paintId 必须是数字"); return; }

        var pawn = player!.PlayerPawn?.Value;
        var active = pawn?.WeaponServices?.ActiveWeapon?.Value;
        if (active == null || !active.IsValid) { Reply(player!, "拿着武器再输 !skin"); return; }

        var designer = active.DesignerName ?? "";
        bool isKnife = designer.Contains("knife") || designer == "weapon_bayonet";
        var item = active.AttributeManager?.Item;
        if (item == null) { Reply(player!, "读取武器失败"); return; }
        ushort def = item.ItemDefinitionIndex;

        if (isKnife)
        {
            _cfg.KnifeDef = def;
            _cfg.KnifePaint = paint;
            SaveConfig();
            ApplyKnife(pawn!, def, paint);
            Reply(player!, $"刀皮已设为 {paint}（defindex {def}）");
        }
        else
        {
            _cfg.GunPaints[def] = paint;
            SaveConfig();
            ApplySkinToWeapon(active, def, paint);
            Reply(player!, $"已给 defindex {def} 上皮肤 {paint}（重生后保持）");
        }
    }

    private void CmdKnife(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2) { Reply(player!, "用法: !knife <名字|defindex> [paintId]，名字如 karambit/butterfly/m9"); return; }

        string a = info.GetArg(1);
        ushort def;
        if (!ushort.TryParse(a, out def) && !KnifeByName.TryGetValue(a, out def))
        { Reply(player!, "认不出这把刀: " + a); return; }

        int paint = _cfg.KnifePaint;
        if (info.ArgCount >= 3 && int.TryParse(info.GetArg(2), out int p)) paint = p;

        _cfg.KnifeDef = def;
        _cfg.KnifePaint = paint;
        SaveConfig();

        var pawn = player!.PlayerPawn?.Value;
        if (pawn != null && pawn.IsValid) ApplyKnife(pawn, def, paint);
        Reply(player!, $"刀已设为 defindex {def}，皮肤 {paint}");
    }

    private void CmdGloves(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 3 || !ushort.TryParse(info.GetArg(1), out ushort def) || !int.TryParse(info.GetArg(2), out int paint))
        { Reply(player!, "用法: !gloves <defindex> <paintId>，如 !gloves 5027 10006"); return; }

        _cfg.GloveDef = def;
        _cfg.GlovePaint = paint;
        SaveConfig();

        var pawn = player!.PlayerPawn?.Value;
        if (pawn != null && pawn.IsValid) ApplyGloves(pawn, def, paint);
        Reply(player!, $"手套已设为 defindex {def}，皮肤 {paint}");
    }

    private void CmdSearch(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2) { Reply(player!, "用法: !skinsearch <关键词>，如 !skinsearch 多普勒 / !skinsearch awp 龙"); return; }
        if (_db.Count == 0) { Reply(player!, "皮肤库未加载（缺 skins_db.json）"); return; }

        // 支持多个关键词，全部命中才算（武器名/中文名/英文名任意字段）
        var keys = new List<string>();
        for (int i = 1; i < info.ArgCount; i++) keys.Add(info.GetArg(i).ToLowerInvariant());

        var hits = _db.Where(e =>
        {
            string hay = ((e.w ?? "") + " " + (e.cn ?? "") + " " + (e.en ?? "")).ToLowerInvariant();
            return keys.All(k => hay.Contains(k));
        }).Take(10).ToList();

        if (hits.Count == 0) { Reply(player!, "没找到匹配的皮肤，换个关键词试试"); return; }
        Reply(player!, $"找到 {hits.Count} 个（最多显示10个）：");
        foreach (var e in hits)
            player!.PrintToChat($" \x06{e.p}\x01  {e.w} | {e.cn}");
        Reply(player!, "枪: 手持后 !skin <代号>　刀: !knife <刀名> <代号>");
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

    // ---------------- 工具 ----------------

    private static bool IsRealPlayer(CCSPlayerController? player)
        => player != null && player.IsValid && !player.IsBot && !player.IsHLTV;

    private void Reply(CCSPlayerController player, string msg)
        => player.PrintToChat($" \x04[皮肤]\x01 {msg}");

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
                _cfg = JsonSerializer.Deserialize<SkinConfig>(File.ReadAllText(ConfigPath)) ?? new SkinConfig();
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] 读取 config.json 失败: " + ex.Message);
            _cfg = new SkinConfig();
        }
    }

    private void SaveConfig()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] 写入 config.json 失败: " + ex.Message);
        }
    }

    private void LoadSearchDb()
    {
        _db = new List<SkinEntry>();
        try
        {
            if (!File.Exists(SearchDbPath))
            {
                Logger.LogWarning("[PlayerSkins] 未找到 skins_db.json，!skinsearch 不可用");
                return;
            }
            _db = JsonSerializer.Deserialize<List<SkinEntry>>(File.ReadAllText(SearchDbPath)) ?? new List<SkinEntry>();
            Logger.LogInformation($"[PlayerSkins] 皮肤搜索库已加载 {_db.Count} 条");
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] 读取 skins_db.json 失败: " + ex.Message);
        }
    }

    private void LoadLegacyPaints()
    {
        _legacyPaints.Clear();
        try
        {
            if (!File.Exists(SkinsDbPath))
            {
                Logger.LogWarning("[PlayerSkins] 未找到 skins_en.json，legacy 模型皮肤位置可能不准");
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(SkinsDbPath));
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("legacy_model", out var lm) && lm.ValueKind == JsonValueKind.True
                    && el.TryGetProperty("weapon_defindex", out var di)
                    && el.TryGetProperty("paint", out var pk))
                {
                    _legacyPaints.Add(((ushort)ReadInt(di), ReadInt(pk)));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] LoadLegacyPaints 失败: " + ex.Message);
        }

        static int ReadInt(JsonElement e)
            => e.ValueKind == JsonValueKind.Number ? e.GetInt32() : (int.TryParse(e.GetString(), out var r) ? r : 0);
    }

    private class SkinEntry
    {
        public int p { get; set; }
        public string? w { get; set; }
        public string? en { get; set; }
        public string? cn { get; set; }
    }

    private class SkinConfig
    {
        public Dictionary<ushort, int> GunPaints { get; set; } = new();
        public ushort KnifeDef { get; set; } = 0;
        public int KnifePaint { get; set; } = 0;
        public ushort GloveDef { get; set; } = 0;
        public int GlovePaint { get; set; } = 0;
        public int Seed { get; set; } = 0;
        public float Wear { get; set; } = 0.01f;
    }
}
