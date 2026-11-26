using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace SaleFish.Save;

/// <summary>
/// 存档文件读写（静态壳，与 WarehouseRegistry 同风格）：user://saves/ 下的 JSON。
/// 多槽 = 槽名当文件名；此层只做「文件 ⇄ SaveFile」，不做任何运行态还原（那是 SaveManager 的事）。
/// 路径约定：Windows 上 user:// 落在 %APPDATA%\Godot\app_userdata\SaleFish\ 下。
/// </summary>
public static class SaveService
{
    private const string Dir = "user://saves/";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,     // 人类可读，调试方便
        IncludeFields = true,     // 存档 DTO 全部用公共字段（本项目数据类风格）
    };

    /// <summary>槽的存档文件路径（user:// 相对）。</summary>
    public static string PathFor(string slot) => Dir + slot + ".json";

    /// <summary>写槽；目录不存在自动建。已存在同名槽 = 覆盖。</summary>
    public static void Save(string slot, SaveFile data)
    {
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(Dir));
        var json = JsonSerializer.Serialize(data, Options);
        var f = FileAccess.Open(PathFor(slot), FileAccess.ModeFlags.Write);
        if (f == null)
        {
            GD.PushError($"[Save] 无法写入存档「{PathFor(slot)}」。");
            return;
        }
        using (f)
        {
            f.StoreString(json);
        }
    }

    /// <summary>读槽；槽不存在或文件损坏返回 null（损坏仅告警，不中断游戏）。</summary>
    public static SaveFile? Load(string slot)
    {
        if (!FileAccess.FileExists(PathFor(slot))) return null;
        var f = FileAccess.Open(PathFor(slot), FileAccess.ModeFlags.Read);
        if (f == null) return null;
        try
        {
            using (f)
            {
                return JsonSerializer.Deserialize<SaveFile>(f.GetAsText(), Options);
            }
        }
        catch (Exception e)
        {
            GD.PushError($"[Save] 读取存档「{slot}」解析失败（文件损坏？）：{e.Message}");
            return null;
        }
    }

    /// <summary>槽是否存在。</summary>
    public static bool Exists(string slot) => FileAccess.FileExists(PathFor(slot));

    /// <summary>删除槽（永久，不撤销）。</summary>
    public static void Delete(string slot)
    {
        if (!Exists(slot)) return;
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(PathFor(slot)));
    }

    /// <summary>列出已有槽名（按 .json 文件名去扩展名）。</summary>
    public static List<string> ListSlots()
    {
        var list = new List<string>();
        if (!DirAccess.DirExistsAbsolute(Dir)) return list;
        var dir = DirAccess.Open(Dir);
        if (dir == null) return list;
        using (dir)
        {
            foreach (var file in dir.GetFiles())
            {
                if (!file.EndsWith(".json")) continue;
                list.Add(file[..^".json".Length]);
            }
        }
        return list;
    }
}