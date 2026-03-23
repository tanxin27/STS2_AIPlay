using System;
using System.Collections.Generic;
using System.Linq;

namespace TokenSpire2.Llm;

/// <summary>
/// 回合决策分析器 - 分析每回合的最优策略
/// </summary>
public class CombatTurnAnalyzer
{
    /// <summary>
    /// 分析当前回合的最优策略
    /// </summary>
    public TurnAnalysis AnalyzeOptimalStrategy(CombatTurnRecord turn)
    {
        var analysis = new TurnAnalysis
        {
            TurnId = turn.Id,
            EnemyId = turn.EnemyId,
            TurnNumber = turn.TurnNumber,
            Situation = AnalyzeSituation(turn),
            RecommendedStrategy = RecommendStrategy(turn),
            KeyCards = IdentifyKeyCards(turn),
            CommonMistakes = CheckCommonMistakes(turn),
            ExpectedOutcome = PredictOutcome(turn)
        };
        
        return analysis;
    }
    
    /// <summary>
    /// 分析当前局势
    /// </summary>
    private SituationAnalysis AnalyzeSituation(CombatTurnRecord turn)
    {
        var situation = new SituationAnalysis();
        var start = turn.StartState;
        
        // HP状况
        var hpPercent = (float)start.PlayerHp / start.PlayerMaxHp;
        if (hpPercent < 0.3f) situation.Urgency = "critical";
        else if (hpPercent < 0.5f) situation.Urgency = "high";
        else if (hpPercent < 0.7f) situation.Urgency = "medium";
        else situation.Urgency = "low";
        
        // 威胁等级
        if (start.Enemy.IntentDamage > start.PlayerHp * 0.5f)
            situation.ThreatLevel = "deadly";
        else if (start.Enemy.IntentDamage > start.PlayerHp * 0.3f)
            situation.ThreatLevel = "high";
        else if (start.Enemy.IntentDamage > 0)
            situation.ThreatLevel = "moderate";
        else
            situation.ThreatLevel = "low";
        
        // 是否可以斩杀
        var potentialDamage = CalculatePotentialDamage(start.Hand, start.PlayerEnergy);
        if (potentialDamage >= start.Enemy.Hp + start.Enemy.Block)
        {
            situation.CanLethal = true;
            situation.Priority = "lethal";
        }
        // 敌人血量低，应该rush
        else if (start.Enemy.Hp < start.Enemy.MaxHp * 0.3f)
        {
            situation.Priority = "aggressive";
        }
        // 威胁高，需要防御
        else if (situation.ThreatLevel == "high" || situation.ThreatLevel == "deadly")
        {
            situation.Priority = "defensive";
        }
        else
        {
            situation.Priority = "balanced";
        }
        
        // 卡组类型判断
        var hasPoison = start.DeckSnapshot.Any(c => 
            c.Contains("Poison") || c.Contains("Noxious") || c.Contains("Catalyst"));
        var hasShiv = start.DeckSnapshot.Any(c => 
            c.Contains("Blade Dance") || c.Contains("Shiv") || c.Contains("Accuracy"));
        
        if (hasPoison) situation.DeckArchetype = "poison";
        else if (hasShiv) situation.DeckArchetype = "shiv";
        else situation.DeckArchetype = "general";
        
        return situation;
    }
    
    /// <summary>
    /// 推荐具体策略
    /// </summary>
    private StrategyRecommendation RecommendStrategy(CombatTurnRecord turn)
    {
        var start = turn.StartState;
        var rec = new StrategyRecommendation();
        var situation = AnalyzeSituation(turn);
        
        switch (situation.Priority)
        {
            case "lethal":
                rec.PrimaryGoal = "Kill enemy this turn";
                rec.KeyActions = new List<string> 
                { 
                    "Use all damage cards",
                    "Ignore block if enemy will die",
                    "Use damage potions if needed"
                };
                rec.EnergyAllocation = "All-in damage";
                break;
                
            case "defensive":
                rec.PrimaryGoal = "Survive this turn";
                rec.KeyActions = new List<string>
                {
                    $"Block at least {start.Enemy.IntentDamage - start.PlayerBlock} damage",
                    "Use defensive potions if HP < 30%",
                    "Play block cards first"
                };
                rec.EnergyAllocation = "70% defense, 30% damage if possible";
                break;
                
            case "aggressive":
                rec.PrimaryGoal = "Finish enemy quickly";
                rec.KeyActions = new List<string>
                {
                    "Maximize damage output",
                    "Set up for lethal next turn",
                    "Apply debuffs (weak, vulnerable)"
                };
                rec.EnergyAllocation = "70% damage, 30% setup";
                break;
                
            default: // balanced
                rec.PrimaryGoal = "Efficient turn";
                rec.KeyActions = new List<string>
                {
                    "Block incoming damage",
                    "Deal damage with remaining energy",
                    "Draw cards if possible"
                };
                rec.EnergyAllocation = "50% defense, 50% damage";
                break;
        }
        
        // 特定敌人特殊处理
        rec.SpecialConsiderations = GetEnemySpecificAdvice(turn.EnemyId, situation);
        
        return rec;
    }
    
    /// <summary>
    /// 识别关键卡牌
    /// </summary>
    private List<KeyCardInfo> IdentifyKeyCards(CombatTurnRecord turn)
    {
        var start = turn.StartState;
        var keyCards = new List<KeyCardInfo>();
        
        foreach (var card in start.Hand)
        {
            var info = new KeyCardInfo { CardName = card };
            
            // 判断卡牌类型
            if (IsAttackCard(card))
            {
                info.Type = "attack";
                info.Importance = start.Enemy.Hp < 20 ? "high" : "medium";
                
                // 如果能完成斩杀
                var damage = EstimateCardDamage(card);
                if (damage >= start.Enemy.Hp + start.Enemy.Block)
                    info.Importance = "critical";
            }
            else if (IsBlockCard(card))
            {
                info.Type = "block";
                info.Importance = start.Enemy.IntentDamage > start.PlayerBlock ? "high" : "low";
            }
            else if (IsDrawCard(card))
            {
                info.Type = "draw";
                info.Importance = "medium";
                info.Note = "Play early to see more options";
            }
            else if (IsPowerCard(card))
            {
                info.Type = "power";
                info.Importance = turn.TurnNumber <= 2 ? "high" : "low";
                info.Note = turn.TurnNumber <= 2 ? "Setup for future turns" : "Skip if fighting for survival";
            }
            
            keyCards.Add(info);
        }
        
        return keyCards.OrderByDescending(c => c.Importance switch
        {
            "critical" => 4,
            "high" => 3,
            "medium" => 2,
            _ => 1
        }).ToList();
    }
    
    /// <summary>
    /// 检查常见错误
    /// </summary>
    private List<MistakeWarning> CheckCommonMistakes(CombatTurnRecord turn)
    {
        var warnings = new List<MistakeWarning>();
        var start = turn.StartState;
        
        // 检查1：低血量不用药水
        if (start.PlayerHp < 20 && !start.Potions.Any(p => p.Contains("Block") || p.Contains("Heal")))
        {
            warnings.Add(new MistakeWarning
            {
                Pattern = "Low HP without using defensive potions",
                Risk = "Might die to next attack",
                BetterPlay = "Use block/heal potion proactively"
            });
        }
        
        // 检查2：可以斩杀但没输出
        var potentialDamage = CalculatePotentialDamage(start.Hand, start.PlayerEnergy);
        if (potentialDamage >= start.Enemy.Hp + start.Enemy.Block)
        {
            var blockCards = start.Hand.Where(c => IsBlockCard(c)).Count();
            if (blockCards > 0)
            {
                warnings.Add(new MistakeWarning
                {
                    Pattern = "Can lethal but might waste energy on block",
                    Risk = "Missed opportunity to end fight",
                    BetterPlay = "All-in damage, ignore block when enemy dies"
                });
            }
        }
        
        // 检查3：面对高伤敌人不防御
        if (start.Enemy.IntentDamage > start.PlayerHp * 0.4f)
        {
            var blockCards = start.Hand.Where(c => IsBlockCard(c)).Count();
            if (blockCards == 0 && !start.Potions.Any(p => p.Contains("Block")))
            {
                warnings.Add(new MistakeWarning
                {
                    Pattern = "No block against heavy attack",
                    Risk = $"Taking {start.Enemy.IntentDamage} damage could be lethal",
                    BetterPlay = "Need to find block cards or use potions"
                });
            }
        }
        
        // 检查4：抽牌时机不对
        var drawCards = start.Hand.Where(c => IsDrawCard(c));
        if (drawCards.Any() && turn.Decision.CardsPlayed.Any())
        {
            var firstCard = turn.Decision.CardsPlayed.FirstOrDefault();
            if (firstCard != null && !IsDrawCard(firstCard))
            {
                warnings.Add(new MistakeWarning
                {
                    Pattern = "Played non-draw cards before draw cards",
                    Risk = "Missing better options that could be drawn",
                    BetterPlay = "Play draw cards first to see full options"
                });
            }
        }
        
        return warnings;
    }
    
    /// <summary>
    /// 预测结果
    /// </summary>
    private OutcomePrediction PredictOutcome(CombatTurnRecord turn)
    {
        var start = turn.StartState;
        var decision = turn.Decision;
        var prediction = new OutcomePrediction();
        
        // 预测伤害
        prediction.DamageDealt = decision.DamageDealt;
        prediction.EnemyHpRemaining = Math.Max(0, start.Enemy.Hp - start.Enemy.Block - decision.DamageDealt);
        prediction.WillKillEnemy = prediction.EnemyHpRemaining == 0;
        
        // 预测受到的伤害
        var blockAfter = start.PlayerBlock + decision.BlockGained;
        var damageTaken = Math.Max(0, start.Enemy.IntentDamage - blockAfter);
        prediction.DamageTaken = damageTaken;
        prediction.PlayerHpAfter = start.PlayerHp - damageTaken;
        prediction.WillDie = prediction.PlayerHpAfter <= 0;
        
        // 评估
        if (prediction.WillKillEnemy && !prediction.WillDie)
            prediction.Evaluation = "optimal";
        else if (damageTaken == 0)
            prediction.Evaluation = "good";
        else if (damageTaken < start.Enemy.IntentDamage * 0.5f)
            prediction.Evaluation = "acceptable";
        else
            prediction.Evaluation = "risky";
        
        return prediction;
    }
    
    /// <summary>
    /// 事后分析：对比实际决策和推荐决策
    /// </summary>
    public PostTurnAnalysis PostHocAnalysis(CombatTurnRecord turn, TurnAnalysis preAnalysis)
    {
        var post = new PostTurnAnalysis
        {
            TurnId = turn.Id,
            WasOptimal = turn.Outcome?.DamageTaken == 0 && turn.Outcome?.EnemyDefeated == true,
            ActualStrategy = InferActualStrategy(turn),
            RecommendedStrategy = preAnalysis.RecommendedStrategy.PrimaryGoal,
            Deviation = AnalyzeDeviation(turn, preAnalysis),
            Lesson = GenerateLesson(turn, preAnalysis)
        };
        
        return post;
    }
    
    // ==================== 辅助方法 ====================
    
    private int CalculatePotentialDamage(List<string> hand, int energy)
    {
        // 简化计算：假设每张攻击牌平均7伤害
        var attackCards = hand.Count(c => IsAttackCard(c));
        var energyAvailable = Math.Min(energy, attackCards * 1); // 假设1费
        return energyAvailable * 7;
    }
    
    private int EstimateCardDamage(string card)
    {
        // 简化估算
        if (card.Contains("Strike")) return 6;
        if (card.Contains("Heavy")) return 14;
        if (card.Contains("Bash")) return 8;
        return 7; // 默认
    }
    
    private bool IsAttackCard(string card)
    {
        return card.Contains("Strike") || card.Contains("Attack") || 
               card.Contains("Damage") || card.Contains("Bash") ||
               card.Contains("Heavy") || card.Contains("Cleave");
    }
    
    private bool IsBlockCard(string card)
    {
        return card.Contains("Defend") || card.Contains("Block") || 
               card.Contains("Blur") || card.Contains("Dodge") ||
               card.Contains("Backflip");
    }
    
    private bool IsDrawCard(string card)
    {
        return card.Contains("Draw") || card.Contains("Pommel") || 
               card.Contains("Shrug") || card.Contains("Acrobatics");
    }
    
    private bool IsPowerCard(string card)
    {
        return card.Contains("Demon Form") || card.Contains("Barricade") ||
               card.Contains("Footwork") || card.Contains("Noxious Fumes");
    }
    
    private List<string> GetEnemySpecificAdvice(string enemyId, SituationAnalysis situation)
    {
        var advice = new List<string>();
        
        // Gremlin Nob
        if (enemyId.Contains("Nob"))
        {
            advice.Add("Gremlin Nob enrages when you play skills - avoid skills!");
        }
        // Lagavulin
        else if (enemyId.Contains("Lagavulin"))
        {
            advice.Add("Lagavulin debuffs strength/dex - frontload damage before debuff");
        }
        // Sentries
        else if (enemyId.Contains("Sentry"))
        {
            advice.Add("Kill left sentry first to reduce daze");
        }
        // Slime Boss
        else if (enemyId.Contains("Slime"))
        {
            if (situation.CanLethal)
                advice.Add("Boss splits at 50% HP - try to one-shot if possible");
        }
        
        return advice;
    }
    
    private string InferActualStrategy(CombatTurnRecord turn)
    {
        var attackCount = turn.Decision.CardsPlayed.Count(c => IsAttackCard(c));
        var blockCount = turn.Decision.CardsPlayed.Count(c => IsBlockCard(c));
        
        if (attackCount >= 2 && blockCount == 0) return "aggressive";
        if (blockCount >= 2 && attackCount <= 1) return "defensive";
        return "balanced";
    }
    
    private string AnalyzeDeviation(CombatTurnRecord turn, TurnAnalysis analysis)
    {
        var actual = InferActualStrategy(turn);
        var recommended = analysis.Situation.Priority switch
        {
            "lethal" or "aggressive" => "aggressive",
            "defensive" => "defensive",
            _ => "balanced"
        };
        
        if (actual == recommended) return "none";
        if (recommended == "defensive" && actual == "aggressive") return "too_aggressive";
        if (recommended == "aggressive" && actual == "defensive") return "too_defensive";
        return "minor";
    }
    
    private string GenerateLesson(CombatTurnRecord turn, TurnAnalysis analysis)
    {
        if (turn.Outcome?.DamageTaken > turn.StartState.PlayerHp * 0.3f)
        {
            return $"Lost {turn.Outcome.DamageTaken} HP - should have been more defensive";
        }
        if (turn.Outcome?.EnemyDefeated == false && analysis.Situation.CanLethal)
        {
            return "Missed lethal opportunity - check damage math more carefully";
        }
        return "";
    }
}

// ==================== 分析结果类 ====================

public class TurnAnalysis
{
    public string TurnId { get; set; } = "";
    public string EnemyId { get; set; } = "";
    public int TurnNumber { get; set; }
    public SituationAnalysis Situation { get; set; } = new();
    public StrategyRecommendation RecommendedStrategy { get; set; } = new();
    public List<KeyCardInfo> KeyCards { get; set; } = new();
    public List<MistakeWarning> CommonMistakes { get; set; } = new();
    public OutcomePrediction ExpectedOutcome { get; set; } = new();
}

public class SituationAnalysis
{
    public string Urgency { get; set; } = "medium"; // critical, high, medium, low
    public string ThreatLevel { get; set; } = "medium"; // deadly, high, moderate, low
    public string Priority { get; set; } = "balanced"; // lethal, defensive, aggressive, balanced
    public string DeckArchetype { get; set; } = "general";
    public bool CanLethal { get; set; }
}

public class StrategyRecommendation
{
    public string PrimaryGoal { get; set; } = "";
    public List<string> KeyActions { get; set; } = new();
    public string EnergyAllocation { get; set; } = "";
    public List<string> SpecialConsiderations { get; set; } = new();
}

public class KeyCardInfo
{
    public string CardName { get; set; } = "";
    public string Type { get; set; } = "";
    public string Importance { get; set; } = "medium"; // critical, high, medium, low
    public string? Note { get; set; }
}

public class MistakeWarning
{
    public string Pattern { get; set; } = "";
    public string Risk { get; set; } = "";
    public string BetterPlay { get; set; } = "";
}

public class OutcomePrediction
{
    public int DamageDealt { get; set; }
    public int EnemyHpRemaining { get; set; }
    public bool WillKillEnemy { get; set; }
    public int DamageTaken { get; set; }
    public int PlayerHpAfter { get; set; }
    public bool WillDie { get; set; }
    public string Evaluation { get; set; } = "neutral"; // optimal, good, acceptable, risky
}

public class PostTurnAnalysis
{
    public string TurnId { get; set; } = "";
    public bool WasOptimal { get; set; }
    public string ActualStrategy { get; set; } = "";
    public string RecommendedStrategy { get; set; } = "";
    public string Deviation { get; set; } = "none"; // none, too_aggressive, too_defensive, minor
    public string? Lesson { get; set; }
}
