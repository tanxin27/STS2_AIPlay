using System;
using System.Collections.Generic;
using System.Linq;

namespace TokenSpire2.Llm;

/// <summary>
/// 根据对局结果更新AI经验的工具
/// </summary>
public class ExperienceUpdater
{
    private readonly ArchetypeManager _manager;
    
    public ExperienceUpdater(ArchetypeManager manager)
    {
        _manager = manager;
    }
    
    /// <summary>
    /// 游戏结束后更新经验
    /// </summary>
    public void UpdateFromRun(
        string character,
        bool victory,
        int floor,
        List<string> finalDeck,
        List<string> finalRelics,
        List<CardPickInfo> cardPicks)
    {
        var experience = _manager.GetExperience(character);
        
        try
        {
            // 1. 更新单卡经验
            foreach (var pick in cardPicks)
            {
                UpdateCardExperience(experience, pick, victory, floor);
            }
            
            // 2. 如果胜利且层数高，记录/更新发现的流派
            if (victory && floor > 35)
            {
                RecordSuccessfulBuild(experience, character, finalDeck, finalRelics, victory, floor);
            }
            
            // 3. 发现潜在协同
            DiscoverSynergies(experience, finalDeck, victory);
            
            // 4. 验证实验性流派
            ValidateExperimentalBuilds(experience);
            
            // 5. 清理旧决策日志（只保留最近20条）
            CleanupDecisionLogs(experience);
            
            // 6. 保存
            _manager.SaveExperience(character, experience);
            
            MainFile.Logger.Info($"[ExperienceUpdater] 已更新 {character} 的经验数据");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[ExperienceUpdater] 更新经验失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 记录一次选牌决策（用于后续分析决策质量）
    /// </summary>
    public void LogDecision(
        string character,
        int floor,
        string choice,
        string context,
        List<string> options)
    {
        var experience = _manager.GetExperience(character);
        
        experience.RecentDecisions.Add(new DecisionLog
        {
            Floor = floor,
            Choice = choice,
            Context = context,
            Result = "pending", // 后续更新
            Timestamp = DateTime.Now
        });
        
        // 立即保存但不清理
        _manager.SaveExperience(character, experience);
    }
    
    /// <summary>
    /// 更新决策结果（游戏结束后调用）
    /// </summary>
    public void UpdateDecisionResults(
        string character,
        bool victory,
        int finalFloor)
    {
        var experience = _manager.GetExperience(character);
        
        foreach (var decision in experience.RecentDecisions.Where(d => d.Result == "pending"))
        {
            // 简化逻辑：胜利就是好决策，失败就是需要反思
            decision.Result = victory ? "worked" : "neutral";
            
            // 可以添加更复杂的分析
            if (!victory && decision.Floor > finalFloor - 5)
            {
                // 如果决策后不久就死亡，可能是坏决策
                decision.Result = "regret";
                decision.Reflection = "这个决策后不久游戏结束，可能是错误选择";
            }
        }
        
        _manager.SaveExperience(character, experience);
    }
    
    private void UpdateCardExperience(
        AiExperience experience,
        CardPickInfo pick,
        bool victory,
        int floor)
    {
        var cardExp = experience.CardExperiences.FirstOrDefault(e => 
            Normalize(e.CardId) == Normalize(pick.CardId));
        
        if (cardExp == null)
        {
            cardExp = new CardExperience { CardId = pick.CardId };
            experience.CardExperiences.Add(cardExp);
        }
        
        cardExp.PickedCount++;
        if (victory)
            cardExp.WinWhenPicked++;
        
        // 更新平均层数
        var totalFloor = cardExp.AvgFloorWhenPicked * (cardExp.PickedCount - 1) + floor;
        cardExp.AvgFloorWhenPicked = totalFloor / cardExp.PickedCount;
        
        // 添加上下文笔记（简化版）
        if (pick.Context.Contains("Act 1") && !cardExp.ContextsWhereGood.Any(c => c.Contains("Act 1")))
        {
            cardExp.ContextsWhereGood.Add("Act 1 适合拿");
        }
    }
    
    private void RecordSuccessfulBuild(
        AiExperience experience,
        string character,
        List<string> deck,
        List<string> relics,
        bool victory,
        int floor)
    {
        // 查找是否已有相似构建
        var similar = FindSimilarBuild(experience, deck);
        
        if (similar != null)
        {
            // 更新现有构建
            similar.AttemptCount++;
            if (victory)
                similar.SuccessCount++;
            
            // 如果验证成功，提升状态
            if (similar.AttemptCount >= 3 && (float)similar.SuccessCount / similar.AttemptCount >= 0.5f)
            {
                similar.Status = "verified";
                MainFile.Logger.Info($"[ExperienceUpdater] 流派 '{similar.Name}' 已验证！");
            }
        }
        else if (victory && floor > 45)
        {
            // 创建新的实验性构建
            var newBuild = new DiscoveredBuild
            {
                Id = $"my_build_{DateTime.Now:yyyyMMdd_HHmmss}",
                Name = GenerateBuildName(deck, character),
                CreatedAt = DateTime.Now,
                DeckSnapshot = deck.ToList(),
                KeyRelics = relics.ToList(),
                Result = new BuildResult
                {
                    Victory = victory,
                    Floor = floor,
                    Character = character
                },
                WhyItWorked = "新发现的构建，需要更多验证",
                Status = "experimental"
            };
            
            experience.DiscoveredBuilds.Add(newBuild);
            MainFile.Logger.Info($"[ExperienceUpdater] 发现新构建: {newBuild.Name}");
        }
    }
    
    private void DiscoverSynergies(AiExperience experience, List<string> deck, bool victory)
    {
        if (!victory || deck.Count < 5) return;
        
        var deckSet = new HashSet<string>(deck.Select(Normalize));
        
        // 检查所有两两组合
        var cards = deck.ToList();
        for (int i = 0; i < cards.Count; i++)
        {
            for (int j = i + 1; j < cards.Count; j++)
            {
                var c1 = cards[i];
                var c2 = cards[j];
                
                // 检查是否已记录
                var exists = experience.SurprisingSynergies.Any(s =>
                    (Normalize(s.Cards[0]) == Normalize(c1) && Normalize(s.Cards[1]) == Normalize(c2)) ||
                    (Normalize(s.Cards[0]) == Normalize(c2) && Normalize(s.Cards[1]) == Normalize(c1)));
                
                if (!exists)
                {
                    experience.SurprisingSynergies.Add(new SurprisingSynergy
                    {
                        Cards = new List<string> { c1, c2 },
                        Description = "同时出现在胜利卡组中",
                        DiscoveredInRun = DateTime.Now,
                        VerifiedCount = 1,
                        Confidence = "low"
                    });
                }
                else
                {
                    // 验证已有协同
                    var synergy = experience.SurprisingSynergies.First(s =>
                        (Normalize(s.Cards[0]) == Normalize(c1) && Normalize(s.Cards[1]) == Normalize(c2)) ||
                        (Normalize(s.Cards[0]) == Normalize(c2) && Normalize(s.Cards[1]) == Normalize(c1)));
                    
                    synergy.VerifiedCount++;
                    if (synergy.VerifiedCount >= 3)
                        synergy.Confidence = "high";
                    else if (synergy.VerifiedCount >= 2)
                        synergy.Confidence = "medium";
                }
            }
        }
    }
    
    private void ValidateExperimentalBuilds(AiExperience experience)
    {
        foreach (var build in experience.DiscoveredBuilds.Where(b => b.Status == "experimental"))
        {
            if (build.AttemptCount >= 5)
            {
                var winRate = (float)build.SuccessCount / build.AttemptCount;
                
                if (winRate >= 0.5f && build.Result.Floor > 35)
                {
                    build.Status = "verified";
                    MainFile.Logger.Info($"[ExperienceUpdater] 构建 '{build.Name}' 已转正！胜率: {winRate:P0}");
                }
                else
                {
                    build.Status = "deprecated";
                    MainFile.Logger.Info($"[ExperienceUpdater] 构建 '{build.Name}' 淘汰，胜率: {winRate:P0}");
                }
            }
        }
    }
    
    private void CleanupDecisionLogs(AiExperience experience)
    {
        if (experience.RecentDecisions.Count > 20)
        {
            experience.RecentDecisions = experience.RecentDecisions
                .OrderByDescending(d => d.Timestamp)
                .Take(20)
                .ToList();
        }
    }
    
    private DiscoveredBuild? FindSimilarBuild(AiExperience experience, List<string> deck)
    {
        var deckSet = new HashSet<string>(deck.Select(Normalize));
        
        foreach (var build in experience.DiscoveredBuilds.Where(b => b.Status != "deprecated"))
        {
            var buildSet = new HashSet<string>(build.DeckSnapshot.Select(Normalize));
            int common = deckSet.Count(c => buildSet.Contains(c));
            float similarity = (float)common / Math.Max(deckSet.Count, buildSet.Count);
            
            if (similarity > 0.6f) // 60%相似认为是同一构建
                return build;
        }
        
        return null;
    }
    
    private string GenerateBuildName(List<string> deck, string character)
    {
        // 简单启发式命名
        var keyCards = deck.Where(c => 
            c.Contains("Catalyst") || 
            c.Contains("Blade Dance") || 
            c.Contains("Noxious Fumes") ||
            c.Contains("Tactician")).ToList();
        
        if (keyCards.Any())
        {
            return $"基于{string.Join("+", keyCards.Take(2))}的构建";
        }
        
        return $"{character}自定义构建 {DateTime.Now:MM/dd}";
    }
    
    private static string Normalize(string id) => 
        id.ToLowerInvariant().Replace(" ", "").Replace("'", "").Replace("-", "");
}

/// <summary>
/// 卡牌选择信息
/// </summary>
public class CardPickInfo
{
    public string CardId { get; set; } = "";
    public int Floor { get; set; }
    public string Context { get; set; } = ""; // 例如 "Act 1, after combat"
    public List<string> OtherOptions { get; set; } = new(); // 其他可选卡牌
}
