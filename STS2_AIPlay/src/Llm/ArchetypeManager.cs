using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace STS2_AIPlay.Llm;

/// <summary>
/// 管理流派参考配置和AI个人经验的统一入口
/// </summary>
public class ArchetypeManager
{
    private readonly string _dataDir;
    private readonly Dictionary<string, ArchetypeReference> _referenceCache = new();
    private readonly Dictionary<string, AiExperience> _experienceCache = new();
    
    public ArchetypeManager(string? baseDir = null)
    {
        _dataDir = baseDir ?? Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "Data", "Archetypes");
        
        Directory.CreateDirectory(_dataDir);
    }
    
    /// <summary>
    /// 获取角色的参考流派配置
    /// </summary>
    public ArchetypeReference GetReference(string character)
    {
        var key = character.ToUpperInvariant();
        if (_referenceCache.TryGetValue(key, out var cached))
            return cached;
        
        var path = Path.Combine(_dataDir, $"reference_{key}.json");
        if (!File.Exists(path))
        {
            MainFile.Logger.Info($"[ArchetypeManager] 未找到参考配置: {path}，使用空配置");
            return new ArchetypeReference();
        }
        
        try
        {
            var json = File.ReadAllText(path);
            var reference = JsonSerializer.Deserialize<ArchetypeReference>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            reference ??= new ArchetypeReference();
            _referenceCache[key] = reference;
            return reference;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[ArchetypeManager] 加载参考配置失败: {ex.Message}");
            return new ArchetypeReference();
        }
    }
    
    /// <summary>
    /// 获取AI的个人经验
    /// </summary>
    public AiExperience GetExperience(string character)
    {
        var key = character.ToUpperInvariant();
        if (_experienceCache.TryGetValue(key, out var cached))
            return cached;
        
        var path = Path.Combine(_dataDir, $"experience_{key}.json");
        if (!File.Exists(path))
        {
            // 创建空的经验文件
            var empty = new AiExperience();
            SaveExperience(key, empty);
            _experienceCache[key] = empty;
            return empty;
        }
        
        try
        {
            var json = File.ReadAllText(path);
            var experience = JsonSerializer.Deserialize<AiExperience>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            experience ??= new AiExperience();
            _experienceCache[key] = experience;
            return experience;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[ArchetypeManager] 加载经验失败: {ex.Message}，创建新的");
            var empty = new AiExperience();
            _experienceCache[key] = empty;
            return empty;
        }
    }
    
    /// <summary>
    /// 保存AI经验
    /// </summary>
    public void SaveExperience(string character, AiExperience experience)
    {
        var key = character.ToUpperInvariant();
        var path = Path.Combine(_dataDir, $"experience_{key}.json");
        
        try
        {
            experience.LastUpdated = DateTime.Now;
            var json = JsonSerializer.Serialize(experience, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(path, json);
            _experienceCache[key] = experience;
            MainFile.Logger.Info($"[ArchetypeManager] 保存经验: {path}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[ArchetypeManager] 保存经验失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 清除缓存（用于重新加载）
    /// </summary>
    public void ClearCache()
    {
        _referenceCache.Clear();
        _experienceCache.Clear();
    }
}

/// <summary>
/// 卡组分析器 - 分析当前卡组与流派的匹配程度
/// </summary>
public class DeckAnalyzer
{
    private readonly ArchetypeManager _manager;
    
    public DeckAnalyzer(ArchetypeManager manager)
    {
        _manager = manager;
    }
    
    /// <summary>
    /// 分析当前卡组
    /// </summary>
    public DeckAnalysis Analyze(
        List<string> deckCards, 
        List<string> relics, 
        string character,
        int currentAct = 1)
    {
        var reference = _manager.GetReference(character);
        var experience = _manager.GetExperience(character);
        
        var analysis = new DeckAnalysis
        {
            TotalCards = deckCards.Count,
            AttackCount = 0, // TODO: 需要卡牌类型信息
            SkillCount = 0,
            PowerCount = 0,
            KeyCards = deckCards.Where(c => IsKeyCard(c, reference)).ToList()
        };
        
        // 分析参考流派匹配度
        foreach (var archetype in reference.Archetypes)
        {
            var match = AnalyzeArchetypeMatch(deckCards, relics, archetype);
            if (match.MatchScore > 10)
                analysis.ArchetypeMatches.Add(match);
        }
        
        // 分析AI发现的流派匹配度
        foreach (var build in experience.DiscoveredBuilds.Where(b => b.Status != "deprecated"))
        {
            var match = AnalyzeDiscoveredBuildMatch(deckCards, relics, build);
            if (match.MatchScore > 20)
                analysis.ArchetypeMatches.Add(match);
        }
        
        // 排序
        analysis.ArchetypeMatches = analysis.ArchetypeMatches
            .OrderByDescending(m => m.MatchScore)
            .ToList();
        
        // 查找相似的AI发现流派
        analysis.SimilarDiscoveredBuilds = FindSimilarBuilds(deckCards, experience);
        
        return analysis;
    }
    
    private ArchetypeMatchResult AnalyzeArchetypeMatch(
        List<string> deck, 
        List<string> relics, 
        ReferenceArchetype archetype)
    {
        var deckSet = new HashSet<string>(deck.Select(Normalize));
        var relicSet = new HashSet<string>(relics.Select(Normalize));
        
        int mustHaveTotal = archetype.KeyCards.MustHave.Count;
        int mustHaveOwned = archetype.KeyCards.MustHave.Count(c => deckSet.Contains(Normalize(c)));
        
        int niceToHaveTotal = archetype.KeyCards.NiceToHave.Count;
        int niceToHaveOwned = archetype.KeyCards.NiceToHave.Count(c => deckSet.Contains(Normalize(c)));
        
        // 计算匹配分数
        float score = 0;
        if (mustHaveTotal > 0)
            score += (float)mustHaveOwned / mustHaveTotal * 60; // 核心卡占60分
        if (niceToHaveTotal > 0)
            score += (float)niceToHaveOwned / niceToHaveTotal * 30; // 协同卡占30分
        
        // 遗物加成
        int dreamRelics = archetype.Relics.Dream.Count(r => relicSet.Contains(Normalize(r)));
        int goodRelics = archetype.Relics.Good.Count(r => relicSet.Contains(Normalize(r)));
        score += dreamRelics * 5 + goodRelics * 2;
        
        var missingKey = archetype.KeyCards.MustHave
            .Where(c => !deckSet.Contains(Normalize(c)))
            .ToList();
        
        return new ArchetypeMatchResult
        {
            ArchetypeId = archetype.Id,
            Name = archetype.Name,
            Source = "reference",
            MatchScore = Math.Min(score, 100),
            MustHaveOwned = mustHaveOwned,
            MustHaveTotal = mustHaveTotal,
            NiceToHaveOwned = niceToHaveOwned,
            MissingKeyCards = missingKey,
            StrategyHint = archetype.Strategy.GetValueOrDefault("early", "")
        };
    }
    
    private ArchetypeMatchResult AnalyzeDiscoveredBuildMatch(
        List<string> deck,
        List<string> relics,
        DiscoveredBuild build)
    {
        var deckSet = new HashSet<string>(deck.Select(Normalize));
        var buildSet = new HashSet<string>(build.DeckSnapshot.Select(Normalize));
        
        // 计算交集
        int commonCards = deckSet.Count(c => buildSet.Contains(c));
        int totalUnique = deckSet.Union(buildSet).Count();
        
        float similarity = totalUnique > 0 ? (float)commonCards / buildSet.Count * 100 : 0;
        
        return new ArchetypeMatchResult
        {
            ArchetypeId = build.Id,
            Name = $"{build.Name} (你的经验)",
            Source = "discovered",
            MatchScore = similarity,
            MustHaveOwned = commonCards,
            MustHaveTotal = build.DeckSnapshot.Count,
            StrategyHint = build.WhyItWorked ?? ""
        };
    }
    
    private List<DiscoveredBuild> FindSimilarBuilds(List<string> deck, AiExperience experience)
    {
        var results = new List<(DiscoveredBuild build, float similarity)>();
        var deckSet = new HashSet<string>(deck.Select(Normalize));
        
        foreach (var build in experience.DiscoveredBuilds.Where(b => b.Status != "deprecated"))
        {
            var buildSet = new HashSet<string>(build.DeckSnapshot.Select(Normalize));
            int common = deckSet.Count(c => buildSet.Contains(c));
            float similarity = (float)common / Math.Max(deckSet.Count, buildSet.Count);
            
            if (similarity > 0.3f) // 至少30%相似
                results.Add((build, similarity));
        }
        
        return results.OrderByDescending(r => r.similarity).Select(r => r.build).ToList();
    }
    
    private bool IsKeyCard(string cardId, ArchetypeReference reference)
    {
        var normalized = Normalize(cardId);
        foreach (var archetype in reference.Archetypes)
        {
            if (archetype.KeyCards.MustHave.Any(c => Normalize(c) == normalized))
                return true;
        }
        return false;
    }
    
    private static string Normalize(string id) => 
        id.ToLowerInvariant().Replace(" ", "").Replace("'", "").Replace("-", "");
}
