using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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
// v1.2.0 起：配置按 SteamID 分开存（configs/<steamid>.json），多人各自独立、互不覆盖。
// v1.2.1 起：ItemID 只在配置真正改变时才换新的（见 AssignItemId/BumpItemId）。
// v1.2.2 起：!knife 皮肤参数写错会明确报错；!quality stattrak 自动开计数；!stattrak 自动清冲突品质。
// v1.2.3 起：Windows 特征码放宽，不再写死 sub rsp 的立即数（跟 CS2-Bot-Improver v1.4.3 对齐）。
// v1.2.4 起：插件生成的 StatTrak 会在有效击杀后自行累加并保存。
public class PlayerSkinsPlugin : BasePlugin
{
    public override string ModuleName => "PlayerSkins";
    public override string ModuleVersion => "1.2.4";
    public override string ModuleAuthor => "ANXSFAN";
    public override string ModuleDescription => "给真人玩家上枪/刀/手套皮肤+种子/磨损/StatTrak/品质/贴纸（insecure 打人机自用，支持多人各自配置）";

    private MemoryFunctionVoid<nint, string, float>? _setAttrByName;
    private ulong _nextItemId = 9_000_000_000uL;
    private bool _skinErrorLogged;

    // 每个玩家一套配置（key = SteamID）
    private readonly Dictionary<ulong, SkinConfig> _cfgs = new();
    // 旧版单人 config.json：作为“新玩家的初始模板”，每人拿到的是深拷贝
    private SkinConfig? _legacyTemplate;

    private readonly HashSet<(ushort DefIndex, int Paint)> _legacyPaints = new();
    private List<SkinEntry> _skins = new();
    private List<StickerEntry> _stickers = new();

    private string ConfigDir => Path.Combine(ModuleDirectory, "configs");
    private string LegacyConfigPath => Path.Combine(ModuleDirectory, "config.json");
    private string CfgPathFor(ulong steamId) => Path.Combine(ConfigDir, steamId + ".json");
    private string LegacyDataPath => Path.Combine(ModuleDirectory, "skins_en.json");
    private string SkinsDbPath => Path.Combine(ModuleDirectory, "skins_db.json");
    private string StickersDbPath => Path.Combine(ModuleDirectory, "stickers_db.json");

    // 默认刀（CT weapon_knife=42 / T weapon_knife_t=59 / 未设=0）绝不做 ChangeSubclass，
    // 否则客户端 GetEconWpnData 找不到刀脚本 -> 致命断言崩溃
    private static readonly HashSet<ushort> DefaultKnives = new() { 0, 42, 59 };

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
        LoadLegacyTemplate();
        LoadLegacyPaints();
        _skins = LoadDb<SkinEntry>(SkinsDbPath, "皮肤");
        _stickers = LoadDb<StickerEntry>(StickersDbPath, "贴纸");

        try
        {
            _setAttrByName = new MemoryFunctionVoid<nint, string, float>(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? "55 48 89 E5 41 57 41 56 49 89 FE 41 55 41 54 53 48 89 F3 48 83 EC ? F3 0F 11 85"
                    // sub rsp 的立即数会随 V 社改栈帧变动，通配掉，改用后面的 movaps 当锚点
                    // （与 CS2-Bot-Improver v1.4.3 的 BotRandomizer 保持一致）
                    : "40 53 55 41 56 48 81 EC ? ? ? ? 0F 29 74 24");
        }
        catch (Exception ex)
        {
            _setAttrByName = null;
            Logger.LogError("[PlayerSkins] 特征码定位失败，皮肤功能不可用: " + ex.Message);
        }

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Post);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);
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
        AddCommand("css_clearskins", "清空我的全部皮肤设置", CmdClear);
        AddCommand("css_skinsearch", "搜皮肤代号: !skinsearch <关键词>", CmdSkinSearch);
        AddCommand("css_ss", "!skinsearch 简写", CmdSkinSearch);
        AddCommand("css_stickersearch", "搜贴纸代号: !stickersearch <关键词>", CmdStickerSearch);
        AddCommand("css_sss", "!stickersearch 简写", CmdStickerSearch);

        Logger.LogInformation("[PlayerSkins] 已加载，仅作用于真人玩家，配置按 SteamID 独立。");
    }

    public override void Unload(bool hotReload)
    {
        VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPost, HookMode.Post);
    }

    // ---------------- 每玩家配置 ----------------

    private SkinConfig GetCfg(CCSPlayerController player)
    {
        ulong id = player.SteamID;
        if (_cfgs.TryGetValue(id, out var c)) return c;
        c = LoadPlayerConfig(id);
        _cfgs[id] = c;
        return c;
    }

    private SkinConfig LoadPlayerConfig(ulong steamId)
    {
        try
        {
            var path = CfgPathFor(steamId);
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.TryGetProperty("Guns", out _)
                    ? JsonSerializer.Deserialize<SkinConfig>(text) ?? new SkinConfig()
                    : MigrateOld(doc.RootElement);
            }
            // 没有个人配置 -> 用旧的单人 config.json 作初始模板（必须深拷贝，否则多人共享同一对象）
            if (_legacyTemplate != null)
            {
                Logger.LogInformation($"[PlayerSkins] {steamId} 无个人配置，已从旧 config.json 复制一份作为起点");
                return CloneConfig(_legacyTemplate);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[PlayerSkins] 读取 {steamId} 配置失败: " + ex.Message);
        }
        return new SkinConfig();
    }

    private void SaveCfg(CCSPlayerController player)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var cfg = GetCfg(player);
            File.WriteAllText(CfgPathFor(player.SteamID),
                JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] 写入配置失败: " + ex.Message);
        }
    }

    private static SkinConfig CloneConfig(SkinConfig src)
        => JsonSerializer.Deserialize<SkinConfig>(JsonSerializer.Serialize(src)) ?? new SkinConfig();

    private void LoadLegacyTemplate()
    {
        _legacyTemplate = null;
        try
        {
            if (!File.Exists(LegacyConfigPath)) return;
            var text = File.ReadAllText(LegacyConfigPath);
            using var doc = JsonDocument.Parse(text);
            _legacyTemplate = doc.RootElement.TryGetProperty("Guns", out _)
                ? JsonSerializer.Deserialize<SkinConfig>(text)
                : MigrateOld(doc.RootElement);
            if (_legacyTemplate != null)
                Logger.LogInformation("[PlayerSkins] 已载入旧 config.json，作为新玩家的初始模板");
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] 读取旧 config.json 失败: " + ex.Message);
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

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (!IsRealPlayer(attacker) || victim == null || !victim.IsValid) return HookResult.Continue;
        if (attacker!.Index == victim.Index || attacker.TeamNum == victim.TeamNum) return HookResult.Continue;

        var pawn = attacker.PlayerPawn?.Value;
        var weapon = pawn?.WeaponServices?.ActiveWeapon?.Value;
        if (pawn == null || !pawn.IsValid || weapon == null || !weapon.IsValid) return HookResult.Continue;

        // 投掷物击杀发生时玩家通常已经切回枪；必须核对事件中的武器名，
        // 否则手雷/燃烧弹击杀会错误地给当前手持枪加一。
        if (!KillWeaponMatches(@event.Weapon, weapon.DesignerName)) return HookResult.Continue;

        var cfg = GetCfg(attacker);
        var designer = weapon.DesignerName ?? "";
        bool isKnife = designer.Contains("knife") || designer == "weapon_bayonet";
        Loadout? lo;
        if (isKnife)
        {
            if (DefaultKnives.Contains(cfg.KnifeDef)) return HookResult.Continue;
            lo = cfg.Knife;
        }
        else
        {
            var item = weapon.AttributeManager?.Item;
            if (item == null || !cfg.Guns.TryGetValue(item.ItemDefinitionIndex, out lo))
                return HookResult.Continue;
        }

        if (lo.StatTrak < 0) return HookResult.Continue;
        if (lo.StatTrak < int.MaxValue) lo.StatTrak++;
        SyncStatTrak(weapon, lo);
        SaveCfg(attacker);
        return HookResult.Continue;
    }

    private static bool KillWeaponMatches(string? eventWeapon, string? designerName)
    {
        if (string.IsNullOrWhiteSpace(eventWeapon) || string.IsNullOrWhiteSpace(designerName)) return false;
        string eventName = eventWeapon.StartsWith("weapon_", StringComparison.Ordinal)
            ? eventWeapon[7..]
            : eventWeapon;
        string entityName = designerName.StartsWith("weapon_", StringComparison.Ordinal)
            ? designerName[7..]
            : designerName;

        bool eventIsKnife = eventName == "bayonet" || eventName.StartsWith("knife", StringComparison.Ordinal);
        bool entityIsKnife = entityName == "bayonet" || entityName.StartsWith("knife", StringComparison.Ordinal);
        return eventIsKnife ? entityIsKnife : eventName == entityName;
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

            var cfg = GetCfg(player!);
            var w = weapon;
            // 关键：刀必须在创建这一刻就换 subclass，客户端才会用正确刀模型构建（否则显示默认刀）
            if (designer.Contains("knife") || designer == "weapon_bayonet")
            {
                ApplyKnifeToWeapon(w, cfg.KnifeDef, cfg.Knife);
                Server.NextFrame(() => { if (w.IsValid) ApplyKnifeToWeapon(w, cfg.KnifeDef, cfg.Knife); });
            }
            else
            {
                ApplyToWeapon(w, cfg);
                Server.NextFrame(() => { if (w.IsValid) ApplyToWeapon(w, cfg); });
            }
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
        var cfg = GetCfg(player);
        if (cfg.KnifeDef > 0) ApplyKnife(pawn, cfg.KnifeDef, cfg.Knife);
        if (cfg.GloveDef > 0) ApplyGloves(pawn, cfg.GloveDef, cfg.Gloves);
        var ws = pawn.WeaponServices;
        if (ws == null) return;
        foreach (var h in ws.MyWeapons) ApplyToWeapon(h.Value, cfg);
    }

    private void ApplyToWeapon(CBasePlayerWeapon? weapon, SkinConfig cfg)
    {
        if (_setAttrByName == null || weapon == null || !weapon.IsValid) return;
        var designer = weapon.DesignerName;
        if (string.IsNullOrEmpty(designer)) return;
        if (designer.Contains("knife") || designer == "weapon_bayonet") return; // 刀单独处理

        var item = weapon.AttributeManager?.Item;
        if (item == null) return;
        ushort def = item.ItemDefinitionIndex;
        if (def != 0 && cfg.Guns.TryGetValue(def, out var lo))
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
            AssignItemId(item, lo);

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

    private void SyncStatTrak(CBasePlayerWeapon weapon, Loadout lo)
    {
        if (_setAttrByName == null || !weapon.IsValid || lo.StatTrak < 0) return;
        var item = weapon.AttributeManager?.Item;
        if (item == null) return;

        weapon.FallbackStatTrak = lo.StatTrak;
        float count = ViewAsFloat((uint)lo.StatTrak);
        _setAttrByName.Invoke(item.NetworkedDynamicAttributes.Handle, "kill eater", count);
        _setAttrByName.Invoke(item.AttributeList.Handle, "kill eater", count);
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackStatTrak");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
    }

    // 在指定刀实体上换 subclass + 上皮肤（供创建钩子与 ApplyAll 共用）
    private void ApplyKnifeToWeapon(CBasePlayerWeapon? w, ushort defIndex, Loadout lo)
    {
        if (_setAttrByName == null || w == null || !w.IsValid) return;
        if (DefaultKnives.Contains(defIndex)) return;  // 默认刀不换模型，防止客户端崩溃
        var designer = w.DesignerName;
        if (string.IsNullOrEmpty(designer) || (!designer.Contains("knife") && designer != "weapon_bayonet")) return;
        try
        {
            w.AcceptInput("ChangeSubclass", null, null, defIndex.ToString());
            var item = w.AttributeManager?.Item;
            if (item != null)
            {
                item.ItemDefinitionIndex = defIndex;
                ApplyLoadout(w, defIndex, lo, isKnife: true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[PlayerSkins] ApplyKnifeToWeapon 失败: " + ex.Message);
        }
    }

    private void ApplyKnife(CCSPlayerPawn pawn, ushort defIndex, Loadout lo)
    {
        if (DefaultKnives.Contains(defIndex)) return;
        var ws = pawn.WeaponServices;
        if (ws == null) return;
        foreach (var h in ws.MyWeapons)
        {
            var w = h.Value;
            if (w == null || !w.IsValid) continue;
            var designer = w.DesignerName;
            if (string.IsNullOrEmpty(designer) || (!designer.Contains("knife") && designer != "weapon_bayonet")) continue;
            ApplyKnifeToWeapon(w, defIndex, lo);
            break;
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
            AssignItemId(gloves, lo);
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

    // 客户端按 ItemID 缓存合成好的皮肤材质，缓存条目有限。
    // 每次重生同一件装备要过好几遍 ApplyLoadout（创建钩子 2 次 + spawn 后 3 次补写），
    // 内容完全一样，若每遍都换新 ID 就白占 5 个缓存条目 —— 反复调 seed 时很快把缓存撑满，
    // 之后改什么都不再刷新。所以 ID 挂在 Loadout 上保持稳定，只有配置真变了才 BumpItemId。
    private void AssignItemId(CEconItemView item, Loadout lo)
    {
        if (lo.ItemId == 0) lo.ItemId = _nextItemId++;
        ulong id = lo.ItemId;
        item.ItemID = id;
        item.ItemIDLow = (uint)(id & 0xFFFFFFFFu);
        item.ItemIDHigh = (uint)(id >> 32);
    }

    // 配置改了才换新 ID，让客户端丢掉旧材质重新合成。必须在 ReapplyHeld 之前调用。
    private void BumpItemId(Loadout lo) => lo.ItemId = _nextItemId++;

    // ---------------- 指令：作用于手持武器 ----------------

    // 取当前手持武器的 Loadout（不存在则新建），返回是否成功
    private bool HeldLoadout(CCSPlayerController player, out Loadout lo, out CBasePlayerWeapon weapon, out CCSPlayerPawn pawn, out bool isKnife)
    {
        lo = null!; weapon = null!; pawn = null!; isKnife = false;
        var cfg = GetCfg(player);
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
            // 只有已选过真实的刀才允许改其属性；默认刀不能上皮肤/换属性（会崩）
            if (DefaultKnives.Contains(cfg.KnifeDef))
            {
                Reply(player, "默认刀不能改，先用 !knife <刀名> 选一把（如 !knife karambit）");
                return false;
            }
            lo = cfg.Knife;
        }
        else
        {
            if (!cfg.Guns.TryGetValue(def, out lo!)) { lo = new Loadout(); cfg.Guns[def] = lo; }
        }
        return true;
    }

    private void ReapplyHeld(CCSPlayerController player, CBasePlayerWeapon weapon, CCSPlayerPawn pawn, bool isKnife)
    {
        var cfg = GetCfg(player);
        if (isKnife) ApplyKnife(pawn, cfg.KnifeDef, cfg.Knife);
        else ApplyToWeapon(weapon, cfg);
    }

    private void CmdSkin(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2 || !int.TryParse(info.GetArg(1), out int paint)) { Reply(player!, "用法: !skin <paintId>（手持武器）"); return; }
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        lo.Paint = paint;
        BumpItemId(lo);
        SaveCfg(player!); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, $"已上皮肤 {paint}" + (isKnife ? "（刀）" : ""));
    }

    private void CmdSeed(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2 || !int.TryParse(info.GetArg(1), out int seed)) { Reply(player!, "用法: !seed <值>，如淬火蓝宝石 !seed 661"); return; }
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        lo.Seed = seed;
        BumpItemId(lo);
        SaveCfg(player!); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, $"图案种子已设为 {seed}");
    }

    private void CmdWear(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2 || !float.TryParse(info.GetArg(1), out float wear)) { Reply(player!, "用法: !wear <0-1>，0=崭新 1=战痕"); return; }
        wear = Math.Clamp(wear, 0f, 1f);
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        lo.Wear = wear;
        BumpItemId(lo);
        SaveCfg(player!); ReapplyHeld(player!, w, pawn, isKnife);
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
        // 显式设过的品质会顶掉 StatTrak 隐含的 9（见 ApplyLoadout），计数器就不显示了 —— 开计数时清掉冲突品质
        if (st >= 0 && lo.Quality >= 0 && lo.Quality != 9)
        {
            lo.Quality = -1;
            Reply(player!, "之前设的品质和 StatTrak 冲突，已清掉，否则计数器不显示");
        }
        BumpItemId(lo);
        SaveCfg(player!); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, st < 0 ? "已关闭 StatTrak" : $"StatTrak 计数 = {st}（金色计数器，中途开的话重生/!reskin 后计数器才挂上）");
    }

    private void CmdQuality(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2) { Reply(player!, "用法: !quality <normal|stattrak|souvenir|star>"); return; }
        int q = info.GetArg(1).ToLowerInvariant() switch
        {
            "normal" or "普通" or "none" => -1,
            "stattrak" or "st" => 9,
            "souvenir" or "纪念品" or "sv" => 12,
            "star" or "unusual" or "★" or "刀" => 3,
            "genuine" or "正品" => 1,
            _ => -2
        };
        if (q == -2) { Reply(player!, "品质: normal/stattrak/souvenir/star"); return; }
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        lo.Quality = q;
        // 计数器要靠 kill eater 属性，光有品质 9 只会名字暗金没有计数器 —— 帮用户把计数一并打开
        if (q == 9 && lo.StatTrak < 0)
        {
            lo.StatTrak = 0;
            Reply(player!, "已顺带打开 StatTrak 计数（=0），改数字用 !stattrak <数>");
        }
        BumpItemId(lo);
        SaveCfg(player!); ReapplyHeld(player!, w, pawn, isKnife);
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
            BumpItemId(lo);
            SaveCfg(player!); ReapplyHeld(player!, w, pawn, isKnife);
            Reply(player!, $"已清除槽位 {slot} 的贴纸（重进地图后彻底消失）");
            return;
        }
        if (!int.TryParse(idArg, out int id) || id <= 0) { Reply(player!, "贴纸 id 必须是正整数，或 clear 清除"); return; }
        var s = new Sticker { Id = id };
        if (info.ArgCount >= 4 && float.TryParse(info.GetArg(3), out float sw)) s.Wear = Math.Clamp(sw, 0f, 1f);
        if (info.ArgCount >= 5 && float.TryParse(info.GetArg(4), out float sc)) s.Scale = sc;
        if (info.ArgCount >= 6 && float.TryParse(info.GetArg(5), out float ro)) s.Rotation = ro;
        lo.Stickers[slot] = s;
        BumpItemId(lo);
        SaveCfg(player!); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, $"槽位 {slot} 贴纸 id={id} 已贴（重进地图后显示）");
    }

    private void CmdStickerClear(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (!HeldLoadout(player!, out var lo, out var w, out var pawn, out var isKnife)) return;
        if (isKnife) { Reply(player!, "刀没有贴纸"); return; }
        lo.Stickers.Clear();
        // 立即把 5 个槽位 id 置 0（配合重进地图彻底清除）
        var item = w.AttributeManager?.Item;
        if (_setAttrByName != null && item != null)
            for (int s = 0; s < 5; s++)
                _setAttrByName.Invoke(item.NetworkedDynamicAttributes.Handle, $"sticker slot {s} id", 0);
        BumpItemId(lo);
        SaveCfg(player!); ReapplyHeld(player!, w, pawn, isKnife);
        Reply(player!, "已清空该武器全部贴纸（重进地图后彻底消失）");
    }

    private void CmdKnife(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 2) { Reply(player!, "用法: !knife <名字|defindex> [paintId]"); return; }
        string a = info.GetArg(1);
        ushort def;
        if (!KnifeByName.TryGetValue(a, out def) && !ushort.TryParse(a, out def)) { Reply(player!, "认不出这把刀: " + a); return; }
        // 只允许已知有效的刀，防止无效 def 触发客户端崩溃
        if (DefaultKnives.Contains(def) || !KnifeByName.ContainsValue(def))
        { Reply(player!, "不是有效的刀。可用: karambit/butterfly/m9/talon/stiletto/ursus/skeleton/kukri 等"); return; }

        // 第二个参数必须是数字；"!knife M9 Bayonet 568" 这类把刀名拆成两个词的写法要明确报错，不能静默忽略
        int paint = -1;
        if (info.ArgCount >= 3 && !int.TryParse(info.GetArg(2), out paint))
        { Reply(player!, $"皮肤 id「{info.GetArg(2)}」没读懂，刀名要写成一个词，如 !knife m9 568"); return; }

        var cfg = GetCfg(player!);
        cfg.KnifeDef = def;
        if (paint >= 0) cfg.Knife.Paint = paint;
        BumpItemId(cfg.Knife);
        SaveCfg(player!);
        var pawn = player!.PlayerPawn?.Value;
        if (pawn != null && pawn.IsValid) ApplyKnife(pawn, def, cfg.Knife);
        Reply(player!, $"刀已设为 defindex {def}，皮肤 {cfg.Knife.Paint}（重生一次模型才切换）");
    }

    private void CmdGloves(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        if (info.ArgCount < 3 || !ushort.TryParse(info.GetArg(1), out ushort def) || !int.TryParse(info.GetArg(2), out int paint))
        { Reply(player!, "用法: !gloves <defindex> <paintId>，如 !gloves 5030 10048"); return; }
        var cfg = GetCfg(player!);
        cfg.GloveDef = def; cfg.Gloves.Paint = paint;
        BumpItemId(cfg.Gloves);
        SaveCfg(player!);
        var pawn = player!.PlayerPawn?.Value;
        if (pawn != null && pawn.IsValid) ApplyGloves(pawn, def, cfg.Gloves);
        Reply(player!, $"手套已设为 defindex {def}，皮肤 {paint}");
    }

    private void CmdReskin(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        // !reskin 的语义就是“强制刷新”，所以这里全部换新 ID：配置没变时 ID 本来是稳定的，
        // 不 bump 客户端就会直接复用旧材质，等于这条指令没用。也是客户端缓存抽风时的手动救急。
        var cfg = GetCfg(player!);
        foreach (var lo in cfg.Guns.Values) BumpItemId(lo);
        BumpItemId(cfg.Knife);
        BumpItemId(cfg.Gloves);
        var pawn = player!.PlayerPawn?.Value;
        if (pawn != null && pawn.IsValid) ApplyAll(player, pawn);
        Reply(player!, "已重新应用你的全部皮肤");
    }

    private void CmdClear(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsRealPlayer(player)) return;
        _cfgs[player!.SteamID] = new SkinConfig();
        SaveCfg(player!);
        Reply(player!, "已清空你的全部皮肤设置（下回合或重连生效）");
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

        // 这件装备当前用的 ItemID，0 = 还没分配。只存内存，不进 json：
        // 重启后重新分配即可，客户端的材质缓存本来也随之清空。
        [JsonIgnore] public ulong ItemId { get; set; } = 0;
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
