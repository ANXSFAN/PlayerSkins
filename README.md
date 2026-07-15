# PlayerSkins

给 **真人玩家** 上武器 / 刀 / 手套皮肤的 CounterStrikeSharp 插件，用于 **`-insecure` 离线打人机自用**。

配合 [CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) 使用：它的 `BotRandomizer` 只给 **bot** 上皮肤，本插件只给 **真人**（`!IsBot`）上皮肤，两者互不冲突。

---

## ⚠️ 安全须知（务必阅读）

- 本插件是 **服务器端** 插件，只在你 **自己当服务器打人机（listen server + `-insecure`）** 时生效。
- **绝对不要带着 Metamod / CounterStrikeSharp 去连 VAC 官方匹配服 —— 会被 VAC 封号。** 这是整套增强人机框架的通用纪律，跟本插件无关。
- 皮肤只存在于你本地服务器的显示，**不进 Steam 库存、不发送到 V 社服务器**，纯离线自娱自乐。
- 联机前请确保 Metamod 未加载（用 CS2-Bot-Improver 的启动器切换模式，或把 `addons` 移出游戏目录）。

> 一句话：**只在 `-insecure` 打人机时用，别碰联机服。**

---

## 前提条件

目标机需已安装 **CS2-Bot-Improver v1.4.2**（即 Metamod + **CounterStrikeSharp 1.0.371**）。
本插件在 **CounterStrikeSharp 1.0.371 / net10.0** 上测试通过；CSSharp 版本差太多可能需要重新编译（见下方「从源码编译」）。

---

## 安装（普通用户）

1. 从 [Releases](../../releases) 下载 `PlayerSkins.zip`。
2. 解压得到 `PlayerSkins` 文件夹，整个放到：
   ```
   ...\Counter-Strike Global Offensive\game\csgo\addons\counterstrikesharp\plugins\
   ```
   最终结构：
   ```
   plugins\PlayerSkins\
     ├─ PlayerSkins.dll
     ├─ PlayerSkins.deps.json
     ├─ skins_en.json      (legacy 老模型皮肤位置判断)
     └─ skins_db.json      (!skinsearch 皮肤名称库)
   ```
3. 启动打人机（开图自动加载），或在服务器控制台执行：
   ```
   css_plugins reload PlayerSkins
   ```

---

## 指令

聊天框输入 `!`，或服务器控制台输入 `css_`。

| 指令 | 说明 | 例子 |
|---|---|---|
| `!skin <代号>` | 给 **当前手持** 的武器上皮肤 | 拿着 AK 输 `!skin 180` |
| `!knife <刀名\|defindex> [代号]` | 换刀（模型 + 皮肤） | `!knife karambit 415` |
| `!gloves <defindex> <代号>` | 换手套 | `!gloves 5030 10048` |
| `!skinsearch <关键词>` / `!ss` | 按中英文名搜皮肤代号 | `!ss 龙` / `!ss awp dragon` |
| `!reskin` | 立即重新应用全部已保存皮肤 | |
| `!clearskins` | 清空全部皮肤设置 | |

- 选择会自动保存到插件目录的 `config.json`，**重启游戏后仍生效**。
- 刀名支持：`karambit` `butterfly` `m9` `bayonet` `flip` `talon` `stiletto` `ursus` `skeleton` `kukri` 等。
- 手套 defindex：`5027`(血猎) `5030`(运动) `5031`(驾驶) `5032`(缠绕) `5033`(摩托) `5034`(专业) `5035`(九头蛇) `4725`(狂牙)。

### 怎么找皮肤代号？

代号 = 皮肤的 **paint_index（图案模板编号）**。三种方式：
1. **局内搜**：`!ss <关键词>`，中英文都能搜，直接返回代号（最省事）。
2. 查 [bymykel.com/CSGO-API](https://bymykel.com/CSGO-API/)，找到皮肤看它的 `paint_index`。
3. 多普勒 / 伽玛多普勒等 **每个相位代号不同**（如爪刀红宝石 415、蓝宝石 416、黑珍珠 417、翡翠 568），认准相位。

---

## 从源码编译（开发者 / 需要适配其它 CSSharp 版本时）

需要 **.NET 10 SDK**（CounterStrikeSharp 1.0.371 目标框架为 net10.0）。

```bash
cd src
dotnet build -c Release
```

编译产物在 `src/bin/Release/net10.0/`，其中 `PlayerSkins.dll` + `PlayerSkins.deps.json` + `skins_en.json` + `skins_db.json` 就是要放进 `plugins/PlayerSkins/` 的全部文件。

- CSSharp 版本在 `src/PlayerSkins.csproj` 的 `PackageReference` 里改。
- 若某次 CS2 更新后皮肤不显示，多半是 CSSharp 核心 gamedata 偏移失效 —— 升级到修复了该版本的 CounterStrikeSharp 并重新编译即可（本插件写皮肤用的内存特征码取自 BotRandomizer，随其更新）。

### 刷新皮肤名称库（`skins_db.json`）

`skins_db.json` 由 [ByMykel/CSGO-API](https://github.com/ByMykel/CSGO-API) 的英文 + 简中 `skins.json` 按 `id` 关联生成（字段：`p`=paint_index, `w`=武器, `en`=英文名, `cn`=中文名）。出新皮肤想更新时，重新拉取两份 skins.json 合并生成即可。

---

## 致谢

- 写皮肤的底层实现思路参考自 [ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) 的 `BotRandomizer`。
- 皮肤名称 / paint_index 数据来自 [ByMykel/CSGO-API](https://github.com/ByMykel/CSGO-API)。
- 基于 [roflmuffin/CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) 开发。

## 许可证

[MIT](LICENSE)。仅供离线打人机自用娱乐，请勿用于任何 VAC 保护的服务器。
