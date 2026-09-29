namespace MoonMovie.Core.Models;

/// <summary>Chinese display names for ISO 3166-1 codes TMDB commonly returns.</summary>
public static class Regions
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CN"] = "中国大陆", ["HK"] = "中国香港", ["TW"] = "中国台湾", ["MO"] = "中国澳门",
        ["US"] = "美国", ["GB"] = "英国", ["JP"] = "日本", ["KR"] = "韩国", ["FR"] = "法国",
        ["DE"] = "德国", ["IT"] = "意大利", ["ES"] = "西班牙", ["CA"] = "加拿大", ["AU"] = "澳大利亚",
        ["NZ"] = "新西兰", ["IN"] = "印度", ["TH"] = "泰国", ["RU"] = "俄罗斯", ["IE"] = "爱尔兰",
        ["BE"] = "比利时", ["NL"] = "荷兰", ["SE"] = "瑞典", ["DK"] = "丹麦", ["NO"] = "挪威",
        ["FI"] = "芬兰", ["PL"] = "波兰", ["MX"] = "墨西哥", ["BR"] = "巴西", ["AR"] = "阿根廷",
        ["ZA"] = "南非", ["SG"] = "新加坡", ["MY"] = "马来西亚", ["ID"] = "印度尼西亚", ["PH"] = "菲律宾",
        ["VN"] = "越南", ["TR"] = "土耳其", ["IL"] = "以色列", ["IR"] = "伊朗", ["CH"] = "瑞士",
        ["AT"] = "奥地利", ["CZ"] = "捷克", ["HU"] = "匈牙利", ["IS"] = "冰岛", ["CO"] = "哥伦比亚",
    };

    public static string Name(string code) =>
        string.IsNullOrWhiteSpace(code) ? string.Empty : Names.TryGetValue(code, out var name) ? name : code.ToUpperInvariant();
}
