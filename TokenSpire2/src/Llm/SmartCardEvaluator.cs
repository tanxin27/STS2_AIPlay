using System;
using System.Collections.Generic;
using System.Linq;

namespace TokenSpire2.Llm;

/// <summary>
/// 智能卡牌评估器 - 结合参考流派和AI个人经验评估卡牌
/// </summary>
public class SmartCardEvaluator
{
    private readonly ArchetypeManager _manager;
    private readonly DeckAnalyzer _analyzer;
    
    public SmartCardEvaluator(ArchetypeManager manager)
    {
        _manager = manager;
        _analyzer = new DeckAnalyzer(manager);
    }
    
    /// <summary>
    /// 评估单张卡牌在当前卡组中的价值
    /// </summary>
    public CardEvaluation EvaluateCard(
        string cardId,
        List<string> currentDeck,
        List<string> currentRelics,
        string character,
        int currentAct = 1,
        int currentFloor = 1)
    {
        var reference = _manager.GetReference(character);
        var experience = _manager.GetExperience(character);
        var normalizedCard = Normalize(cardId);
        
        var evaluation = new CardEvaluation { CardId = cardId };
        
        // 1. 基础质量分（根据Act调整期望）
        evaluation.BaseScore = CalculateBaseScore(cardId, currentAct, reference);
        
        // 2. 参考流派协同分
        var refScore = CalculateReferenceScore(cardId, currentDeck, currentRelics, reference);
        evaluation.ReferenceScore = refScore.score;
        evaluation.MatchingArchetypes = refScore.matchingArchetypes;
        
        // 3. 个人经验分
        var expScore = CalculateExperienceScore(cardId, currentDeck, experience);
        evaluation.ExperienceScore = expScore.score;
        evaluation.PersonalExperience = expScore.experience;
        
        // 4. 发现的协同
        var synergies = FindRelevantSynergies(cardId, currentDeck, experience);
        evaluation.SynergyScore = synergies.Sum(s => 
            s.Confidence == "high" ? 2f : s.Confidence == "medium" ? 1f : 0.5f);
        evaluation.RelevantSynergies = synergies;
        
        // 5. 生成推荐
        evaluation.Recommendation = GenerateRecommendation(evaluation);
        evaluation.Reasoning = GenerateReasoning(evaluation, reference, experience);
        
        return evaluation;
    }
    
    /// <summary>
    /// 评估所有可选卡牌并排序
    /// </summary>
    public List<(string cardId, CardEvaluation evaluation)> EvaluateChoices(
        List<string> choices,
        List<string> currentDeck,
        List<string> currentRelics,
        string character,
        int currentAct = 1)
    {
        var results = new List<(string, CardEvaluation)>();
        
        foreach (var card in choices)
        {
            var eval = EvaluateCard(card, currentDeck, currentRelics, character, currentAct);
            results.Add((card, eval));
        }
        
        return results.OrderByDescending(r => r.Item2.TotalScore).ToList();
    }
    
    private float CalculateBaseScore(string cardId, int act, ArchetypeReference reference)
    {
        // 基础分5分（中等质量）
        float score = 5f;
        
        // 根据社区评价调整
        if (reference.CardNotes.TryGetValue(cardId, out var note))
        {
            // 关键词加分
            if (note.Contains("神卡") || note.Contains("必拿") || note.Contains("万能"))
                score += 2f;
            else if (note.Contains("强力") || note.Contains("核心") || note.Contains("高质量"))
                score += 1.5f;
            else if (note.Contains("一般") || note.Contains("可用"))
                score += 0.5f;
            else if (note.Contains("弱") || note.Contains("坑") || note.Contains("删除"))
                score -= 1f;
        }
        
        // Act调整
        // Act 1: 需要直伤和AOE
        // Act 2: 需要防御和过牌
        // Act 3: 需要终结技
        // TODO: 可以根据卡牌类型进一步调整
        
        return Math.Clamp(score, 1f, 10f);
    }
    
    private (float score, List<string> matchingArchetypes) CalculateReferenceScore(
        string cardId,
        List<string> deck,
        List<string> relics,
        ArchetypeReference reference)
    {
        float score = 0;
        var matching = new List<string>();
        var normalizedCard = Normalize(cardId);
        
        foreach (var archetype in reference.Archetypes)
        {
            bool isMatch = false;
            
            // 检查是否是核心卡
            if (archetype.KeyCards.MustHave.Any(c => Normalize(c) == normalizedCard))
            {
                // 检查卡组是否已经有这个流派的组件
                var deckAlignment = CalculateDeckAlignment(deck, archetype);
                
                if (deckAlignment > 0.5f)
                {
                    // 已经在走这个流派，核心卡很重要
                    score += 3f;
                    isMatch = true;
                }
                else if (deckAlignment > 0.2f)
                {
                    // 有倾向但不是主要方向
                    score += 1.5f;
                    isMatch = true;
                }
                else
                {
                    // 纯新流派，谨慎给分
                    score += 0.5f;
                    isMatch = true;
                }
            }
            // 检查是否是协同卡
            else if (archetype.KeyCards.NiceToHave.Any(c => Normalize(c) == normalizedCard))
            {
                var deckAlignment = CalculateDeckAlignment(deck, archetype);
                if (deckAlignment > 0.4f)
                {
                    score += 1.5f;
                    isMatch = true;
                }
            }
            // 检查是否是辅助卡
            else if (archetype.KeyCards.Support.Any(c => Normalize(c) == normalizedCard))
            {
                var deckAlignment = CalculateDeckAlignment(deck, archetype);
                if (deckAlignment > 0.3f)
                {
                    score += 0.8f;
                    isMatch = true;
                }
            }
            
            if (isMatch)
                matching.Add(archetype.Name);
        }
        
        return (score, matching);
    }
    
    private (float score, CardExperience? experience) CalculateExperienceScore(
        string cardId,
        List<string> deck,
        AiExperience experience)
    {
        var exp = experience.CardExperiences.FirstOrDefault(e => 
            Normalize(e.CardId) == Normalize(cardId));
        
        if (exp == null || exp.PickedCount < 2)
            return (0, null); // 没有足够经验
        
        float score = 0;
        
        // 根据胜率给分
        if (exp.WinRate > 0.6f)
            score += 1.5f;
        else if (exp.WinRate > 0.5f)
            score += 0.8f;
        else if (exp.WinRate < 0.3f)
            score -= 0.5f;
        
        // 根据平均层数给分
        if (exp.AvgFloorWhenPicked > 45)
            score += 0.5f;
        
        // 检查当前环境是否适合
        foreach (var goodContext in exp.ContextsWhereGood)
        {
            // 简化检查：看卡组中是否有提到的牌
            // TODO: 更智能的上下文匹配
            if (goodContext.Contains("有") && deck.Any(d => goodContext.Contains(d)))
            {
                score += 0.5f;
                break;
            }
        }
        
        return (score, exp);
    }
    
    private List<SurprisingSynergy> FindRelevantSynergies(
        string cardId,
        List<string> deck,
        AiExperience experience)
    {
        var results = new List<SurprisingSynergy>();
        var deckSet = new HashSet<string>(deck.Select(Normalize));
        
        foreach (var synergy in experience.SurprisingSynergies)
        {
            if (!synergy.Cards.Contains(cardId))
                continue;
            
            // 检查卡组中是否有其他协同牌
            var otherCards = synergy.Cards.Where(c => c != cardId);
            bool hasSynergy = otherCards.Any(c => deckSet.Contains(Normalize(c)));
            
            if (hasSynergy)
                results.Add(synergy);
        }
        
        return results;
    }
    
    private string GenerateRecommendation(CardEvaluation eval)
    {
        var total = eval.TotalScore;
        
        if (total >= 9f)
            return "强烈推荐";
        if (total >= 7.5f)
            return "推荐";
        if (total >= 6f)
            return "可选";
        if (total >= 4f)
            return "一般";
        return "不推荐";
    }
    
    private string GenerateReasoning(
        CardEvaluation eval, 
        ArchetypeReference reference,
        AiExperience experience)
    {
        var parts = new List<string>();
        
        // 基础质量
        if (eval.BaseScore >= 7)
            parts.Add("基础质量高");
        
        // 流派匹配
        if (eval.MatchingArchetypes.Any())
        {
            var primaryArchetype = eval.MatchingArchetypes.First();
            parts.Add($"符合{primaryArchetype}");
        }
        
        // 个人经验
        if (eval.PersonalExperience != null)
        {
            if (eval.PersonalExperience.WinRate > 0.6f)
                parts.Add($"你的胜率高({eval.PersonalExperience.WinRate:P0})");
            else if (eval.PersonalExperience.WinRate < 0.4f)
                parts.Add($"注意：你的胜率较低({eval.PersonalExperience.WinRate:P0})");
        }
        
        // 协同
        if (eval.RelevantSynergies.Any())
        {
            var synergyCards = eval.RelevantSynergies.SelectMany(s => s.Cards).Distinct();
            parts.Add($"与卡组中的{string.Join(", ", synergyCards.Take(2))}有协同");
        }
        
        return string.Join("，", parts);
    }
    
    private float CalculateDeckAlignment(List<string> deck, ReferenceArchetype archetype)
    {
        var deckSet = new HashSet<string>(deck.Select(Normalize));
        
        int mustHaveTotal = archetype.KeyCards.MustHave.Count;
        int mustHaveOwned = archetype.KeyCards.MustHave.Count(c => deckSet.Contains(Normalize(c)));
        
        if (mustHaveTotal == 0) return 0;
        
        return (float)mustHaveOwned / mustHaveTotal;
    }
    
    private static string Normalize(string id) => 
        id.ToLowerInvariant().Replace(" ", "").Replace("'", "").Replace("-", "");
}
