using System;
using System.Linq;
using Godot;

namespace SaleFish.Localization;

/// <summary>
/// 本地化门面（autoload，API 全静态）：把项目里的多语言统一走 <b>Godot 官方 TranslationServer</b>。
///
/// 数据管线（官方路线）：
///  ▪ 源表 = res://Data/Localization/strings.csv（表头  keys,zh,en,… 一列一语言、一条 key 一行，
///    多语言并排，人工维护友好；标准 CSV 引号包裹）；
///  ▪ Godot 导入器把每列语言生成一个 .translation（OptimizedTranslation，strings.zh/en.translation）；
///  ▪ 已注册进 project.godot [internationalization] locale/translations —— 引擎启动自动加载，
///    编辑器自己的翻译管理界面也能看到/编辑它们。
///
/// 本类薄壳语义：
///  ▪ T(key) = 当前语言译文；当前语言缺 key → 回退 DefaultLanguage（Godot 4.7 的 TranslationServer
///    已移除跨语言回退，这里在 Lookup 里显式补）→ 再没有原样返回 key；
///  ▪ Tf(key,args) = string.Format(T(key), args)；SetLanguage = SetLocale + 广播本服务
///    LanguageChanged（TranslationServer 本身不广播，供自建 UI/组件刷新）；
///  ▪ 未知语言码归一化到 DefaultLanguage。启动固定默认语言（不吃 OS locale）。
/// </summary>
public sealed partial class Localizer : Node
{
    /// <summary>缺 key / 当前语言无效时兜底的语言码（默认语言）。</summary>
    public static string DefaultLanguage { get; set; } = "zh";

    /// <summary>当前语言码（= TranslationServer.GetLocale()）。赋值 = 切语言并广播。</summary>
    public static string CurrentLanguage
    {
        get => TranslationServer.GetLocale();
        set => SetLanguage(value);
    }

    /// <summary>切语言（自动广播刷新）。</summary>
    public static event Action? LanguageChanged;

    public override void _EnterTree()
    {
        SetLanguage(DefaultLanguage);   // 固定默认语言起步（否则直接吃运行环境/操作系统的 locale）
    }

    /// <summary>切换语言（未知码归一化到 <see cref="DefaultLanguage"/>；同值幂等跳过；有效切换广播）。</summary>
    public static void SetLanguage(string code)
    {
        if (string.IsNullOrEmpty(code) || !TranslationServer.GetLoadedLocales().Contains(code))
            code = DefaultLanguage;
        var prev = TranslationServer.GetLocale();
        TranslationServer.SetLocale(code);
        if (TranslationServer.GetLocale() != prev)
            LanguageChanged?.Invoke();
    }

    /// <summary>当前语言的译文；当前缺 key 回退默认语言表；都没有返回 null。</summary>
    public static string? Lookup(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var text = TranslationServer.Translate(key, "");
        if (text != key) return text;                        // 当前语言命中（含 locale 变体匹配）

        // Godot 4.7 TranslationServer 不自动跨语言回退 → 显式查默认语言表
        if (TranslationServer.GetLocale() == DefaultLanguage) return null;
        foreach (var tr in TranslationServer.GetTranslations())
        {
            if (tr.Locale != DefaultLanguage) continue;   // 精确语言码比对（CompareLocales 此版绑定返回 int）
            var msg = tr.GetMessage(key, "");
            if (!string.IsNullOrEmpty(msg) && msg != key) return msg;
        }
        return null;
    }

    /// <summary>取当前语言文字；找不到 = 原样返回 key（宁可显示 key 也不空白）。</summary>
    public static string T(string key) => Lookup(key) ?? key;

    /// <summary>带参数模板：string.Format(T(key), args)。例：Tf("menu.page", 2, 3) → "第 2 / 3 页"。</summary>
    public static string Tf(string key, params object?[] args) =>
        args == null || args.Length == 0 ? T(key) : string.Format(T(key), args);
}