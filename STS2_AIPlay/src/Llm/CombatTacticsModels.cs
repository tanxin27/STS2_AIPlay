using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_AIPlay.Llm;

// ==================== 战斗回合记录 ====================

/// <summary>
/// 单回合完整记录
/// </summary>
public class CombatTurnRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RunId { get; set; } = "";
    public string Character { get; set; } = "";
    public string EnemyId { get; set; } = "";
    public string EnemyName { get; set; } = "";
    public int TurnNumber { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    
    // 回合开始状态
    public TurnStartState StartState { get; set; } = new();
    
    // AI的决策
    public PlayerDecision Decision { get; set; } = new();
    
    // 回合结果
    public TurnOutcome Outcome { get; set; } = new();
    
    // 决策质量评估（后续分析填充）
    public DecisionQuality? Quality { get; set; }
}

public class TurnStartState
{
    public int PlayerHp { get; set; }
    public int PlayerMaxHp { get; set; }
    public int PlayerBlock { get; set; }
    public int PlayerEnergy { get; set; }
    public List<string> Hand { get; set; } = new();
    public List<string> DeckSnapshot { get; set; } = new(); // 简化卡组快照
    public List<string> Relics { get; set; } = new();
    public List<string> Potions { get; set; } = new();
    
    // 敌人状态
    public EnemyState Enemy { get; set; } = new();
}

public class EnemyState
{
    public string EnemyId { get; set; } = "";
    public string EnemyName { get; set; } = "";
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public string Intent { get; set; } = ""; // "Attack 12", "Defend", "Buff", etc.
    public int IntentDamage { get; set; } // 预告的伤害值
    public List<string> Powers { get; set; } = new();
    public bool IsElite { get; set; }
    public bool IsBoss { get; set; }
}

public class PlayerDecision
{
    public List<string> CardsPlayed { get; set; } = new(); // 打出了哪些牌
    public List<string> PotionsUsed { get; set; } = new();
    public bool EndedTurn { get; set; }
    public int DamageDealt { get; set; } // 预期伤害
    public int BlockGained { get; set; } // 预期格挡
    public string LlmReasoning { get; set; } = ""; // LLM当时的推理
}

public class TurnOutcome
{
    public int PlayerHpAfter { get; set; }
    public int PlayerBlockAfter { get; set; }
    public int EnemyHpAfter { get; set; }
    public int ActualDamageDealt { get; set; } // 实际造成的伤害
    public int DamageTaken { get; set; } // 本回合受到的伤害
    public bool EnemyDefeated { get; set; }
    public bool PlayerDefeated { get; set; }
    public List<string> NewHand { get; set; } = new(); // 下回合手牌（用于验证抽牌）
}

public class DecisionQuality
{
    public bool WasOptimal { get; set; }
    public float Score { get; set; } // 0-100
    public List<string> Mistakes { get; set; } = new();
    public List<string> GoodPlays { get; set; } = new();
    public string Analysis { get; set; } = "";
    
    // 具体指标
    public bool ShouldHaveBlocked { get; set; } // 应该防御但没防
    public bool WastedBlock { get; set; } // 格挡溢出
    public bool MissedLethal { get; set; } //  missed 斩杀机会
    public bool BadPotionTiming { get; set; } // 药水使用时机差
}

// ==================== 敌人战术档案 ====================

/// <summary>
/// 特定敌人的累积战术知识
/// </summary>
public class EnemyTacticsProfile
{
    public string EnemyId { get; set; } = "";
    public string EnemyName { get; set; } = "";
    public DateTime LastUpdated { get; set; } = DateTime.Now;
    
    // 统计
    public EnemyStats Stats { get; set; } = new();
    
    // 意图模式学习
    public Dictionary<string, IntentStrategy> IntentStrategies { get; set; } = new();
    
    // 关键发现
    public List<EnemyInsight> Insights { get; set; } = new();
    
    // 回合特定策略
    public List<TurnSpecificStrategy> TurnStrategies { get; set; } = new();
    
    // 反制策略
    public List<CounterStrategy> Counters { get; set; } = new();
}

public class IntentStrategy
{
    public string IntentType { get; set; } = ""; // "Attack", "Defend", "Buff", etc.
    public int SampleSize { get; set; }
    
    // 应对策略效果统计
    public StrategyStats AggressiveApproach { get; set; } = new(); // 全力输出
    public StrategyStats DefensiveApproach { get; set; } = new();  // 优先防御
    public StrategyStats BalancedApproach { get; set; } = new();   // 平衡
    
    public string RecommendedApproach { get; set; } = "balanced";
}

public class StrategyStats
{
    public int Attempts { get; set; }
    public int SuccessCount { get; set; } // 成功击杀且损血少
    public float AvgHpLost { get; set; }
    public float AvgTurnsToKill { get; set; }
    public float SuccessRate => Attempts > 0 ? (float)SuccessCount / Attempts : 0;
}

public class EnemyInsight
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Insight { get; set; } = "";
    public float Confidence { get; set; } = 0.5f;
    public int SupportingEvidence { get; set; } = 0; // 支持证据数量
    public int ContradictingEvidence { get; set; } = 0;
    public DateTime DiscoveredAt { get; set; } = DateTime.Now;
    public bool Verified => Confidence > 0.7f && SupportingEvidence >= 3;
}

public class TurnSpecificStrategy
{
    public int TurnNumber { get; set; } // 第几回合（1=第一回合）
    public string RecommendedStrategy { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public int SampleSize { get; set; }
    public float SuccessRate { get; set; }
}

public class CounterStrategy
{
    public string CardId { get; set; } = "";
    public string Effectiveness { get; set; } = "medium"; // low, medium, high, essential
    public string Context { get; set; } = ""; // 什么情况下特别有效
    public int TimesUsed { get; set; }
    public int TimesHelped { get; set; }
}

// ==================== 通用战术知识 ====================

public class CombatTacticsLibrary
{
    public Dictionary<string, EnemyTacticsProfile> EnemyProfiles { get; set; } = new();
    public List<UniversalCombatLesson> UniversalLessons { get; set; } = new();
    
    // 药水使用策略
    public Dictionary<string, PotionStrategy> PotionStrategies { get; set; } = new();
    
    // 常见错误模式
    public List<CommonMistake> CommonMistakes { get; set; } = new();
}

public class UniversalCombatLesson
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Lesson { get; set; } = "";
    public string Category { get; set; } = ""; // "block", "damage", "potion", "energy"
    public float Confidence { get; set; } = 0.5f;
    public int SupportingRuns { get; set; } = 0;
    public bool Verified => Confidence > 0.75f;
}

public class PotionStrategy
{
    public string PotionId { get; set; } = "";
    public string BestUseCase { get; set; } = "";
    public string WorstUseCase { get; set; } = "";
    public int TimesUsed { get; set; }
    public int TimesHelped { get; set; }
}

public class CommonMistake
{
    public string Pattern { get; set; } = ""; // 如"HP<20还用攻击牌不用药水"
    public string Consequence { get; set; } = "";
    public string BetterAlternative { get; set; } = "";
    public int OccurrenceCount { get; set; }
}
