using System;
using System.Collections.Generic;
using System.Linq;

namespace TokenSpire2.Llm;

// ==================== Phase 3: 药水使用学习 ====================

/// <summary>
/// 药水使用记录
/// </summary>
public class PotionUsageRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PotionId { get; set; } = "";
    public string PotionName { get; set; } = "";
    public DateTime UsedAt { get; set; } = DateTime.Now;
    
    // 使用时的情境
    public PotionUsageContext Context { get; set; } = new();
    
    // 使用后的结果
    public PotionUsageOutcome Outcome { get; set; } = new();
    
    // 评估
    public PotionUsageRating Rating { get; set; } = PotionUsageRating.Neutral;
    public string? Analysis { get; set; }
}

public class PotionUsageContext
{
    public string EnemyId { get; set; } = "";
    public int PlayerHp { get; set; }
    public int PlayerMaxHp { get; set; }
    public float HpPercent => PlayerMaxHp > 0 ? (float)PlayerHp / PlayerMaxHp : 0;
    public int TurnNumber { get; set; }
    public string EnemyIntent { get; set; } = "";
    public int IncomingDamage { get; set; }
    public bool HasBlockOptions { get; set; } // 手牌中是否有格挡选项
    public bool NextRoomIsEliteOrBoss { get; set; }
}

public class PotionUsageOutcome
{
    public bool SurvivedTurn { get; set; }
    public int HpAfter { get; set; }
    public bool WonCombat { get; set; }
    public int TurnsToWin { get; set; }
    public bool WouldHaveDiedWithoutPotion { get; set; } // 关键：不用药水会不会死
}

public enum PotionUsageRating
{
    Excellent, // 完美时机，救了一命或加速击杀
    Good,      // 使用合理
    Neutral,   // 用了但没太大影响
    Poor,      // 使用不当（如满血用治疗药水）
    Wasted     // 完全浪费（如死了也没用掉）
}

/// <summary>
/// 药水使用策略库
/// </summary>
public class PotionStrategyLibrary
{
    public Dictionary<string, Phase3PotionStrategy> Strategies { get; set; } = new();
    public List<PotionTimingRule> TimingRules { get; set; } = new();
    public List<PotionUsageRecord> UsageHistory { get; set; } = new();
}

public class Phase3PotionStrategy
{
    public string PotionId { get; set; } = "";
    public string PotionName { get; set; } = "";
    
    // 最佳使用情境
    public List<string> BestUseCases { get; set; } = new();
    public List<string> WorstUseCases { get; set; } = new();
    
    // 统计
    public int TimesUsed { get; set; }
    public int TimesHelpedWin { get; set; }
    public int TimesPreventedDeath { get; set; }
    public int TimesWasted { get; set; }
    
    // 规则
    public string? HpThresholdRule { get; set; } // "Use when HP < 30%"
    public string? IntentRule { get; set; } // "Use when enemy intents heavy attack"
    
    public float Effectiveness => TimesUsed > 0 ? (float)(TimesHelpedWin + TimesPreventedDeath) / TimesUsed : 0;
}

public class PotionTimingRule
{
    public string Rule { get; set; } = "";
    public string PotionType { get; set; } = "";
    public string Condition { get; set; } = "";
    public float Priority { get; set; } = 1.0f;
    public int SuccessCount { get; set; }
    public int AttemptCount { get; set; }
    public float SuccessRate => AttemptCount > 0 ? (float)SuccessCount / AttemptCount : 0;
}

// ==================== Phase 3: 连招/卡牌组合学习 ====================

/// <summary>
/// 卡牌连招记录
/// </summary>
public class CardComboRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public List<string> CardSequence { get; set; } = new(); // 卡牌使用顺序
    public string EnemyId { get; set; } = "";
    public int TurnNumber { get; set; }
    
    // 执行时的资源
    public int EnergyCost { get; set; }
    public int CardsDrawn { get; set; }
    
    // 效果
    public int TotalDamage { get; set; }
    public int TotalBlock { get; set; }
    public List<string> EffectsApplied { get; set; } = new(); // 虚弱、易伤等
    
    // 结果
    public bool KilledEnemy { get; set; }
    public int HpLost { get; set; }
    public ComboRating Rating { get; set; } = ComboRating.Average;
}

public enum ComboRating
{
    Poor,     // 效果不好
    Average,  // 一般
    Good,     // 效果不错
    Excellent,// 完美连招
    Lethal    // 完成斩杀
}

/// <summary>
/// 发现的连招模式
/// </summary>
public class Phase3DiscoveredCombo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = ""; // 自动生成或LLM命名
    public List<string> CardSequence { get; set; } = new();
    public string? EnemyId { get; set; } // 特定敌人还是通用
    public string Archetype { get; set; } = "general"; // 适用于什么流派
    
    // 效果
    public string ExpectedOutcome { get; set; } = "";
    public int AvgDamage { get; set; }
    public int AvgBlock { get; set; }
    public int EnergyRequired { get; set; }
    
    // 统计
    public int TimesExecuted { get; set; }
    public int TimesSucceeded { get; set; } // 达到预期效果
    public float SuccessRate => TimesExecuted > 0 ? (float)TimesSucceeded / TimesExecuted : 0;
    
    // 评价
    public string Difficulty { get; set; } = "easy"; // easy, medium, hard
    public string Situation { get; set; } = "general"; // 什么情况下使用
    public bool Verified { get; set; }
    
    public DateTime DiscoveredAt { get; set; } = DateTime.Now;
    public DateTime LastUsedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 连招库
/// </summary>
public class ComboLibrary
{
    public List<Phase3DiscoveredCombo> Combos { get; set; } = new();
    public Dictionary<string, List<CardComboRecord>> ComboHistory { get; set; } = new();
    
    // 卡牌协同度矩阵（哪两张卡一起用好）
    public Dictionary<string, CardSynergy> CardSynergies { get; set; } = new();
}

public class CardSynergy
{
    public string CardA { get; set; } = "";
    public string CardB { get; set; } = "";
    public int TimesPlayedTogether { get; set; }
    public int TimesWonTogether { get; set; }
    public float SynergyScore { get; set; } // -1 到 1，负值表示反协同
    public string? Mechanism { get; set; } // "Draw + Damage", "Setup + Execute"等
}

// ==================== Phase 3: 能量管理学习 ====================

/// <summary>
/// 能量使用效率记录
/// </summary>
public class EnergyUsageRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TurnNumber { get; set; }
    public string EnemyId { get; set; } = "";
    
    // 能量情况
    public int EnergyAvailable { get; set; }
    public int EnergyUsed { get; set; }
    public int EnergyWasted { get; set; } // 回合结束剩余能量（如果手牌有可打出的牌）
    
    // 决策
    public bool SavedEnergyForNextTurn { get; set; } // 是否有意留能量
    public List<string> CardsPlayed { get; set; } = new();
    public List<string> CardsSkipped { get; set; } = new(); // 有能量但没打
    
    // 结果
    public bool GoodDecision { get; set; } // 留能量是否正确
    public string? Reasoning { get; set; }
}

/// <summary>
/// 能量管理策略
/// </summary>
public class EnergyManagementProfile
{
    public Dictionary<int, TurnEnergyRule> TurnRules { get; set; } = new(); // 第几回合的能量规则
    public List<EnergyUsageRecord> UsageHistory { get; set; } = new();
    
    // 学习到的规则
    public List<string> LearnedRules { get; set; } = new();
}

public class TurnEnergyRule
{
    public int TurnNumber { get; set; }
    public string Strategy { get; set; } = "spend"; // spend, save, flexible
    public string Reasoning { get; set; } = "";
    public int SuccessCount { get; set; }
    public int AttemptCount { get; set; }
    public float SuccessRate => AttemptCount > 0 ? (float)SuccessCount / AttemptCount : 0;
}

// ==================== Phase 3: 危险回合识别 ====================

/// <summary>
/// 危险模式识别
/// </summary>
public class DangerPattern
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EnemyId { get; set; } = "";
    public string PatternName { get; set; } = "";
    public string Description { get; set; } = "";
    
    // 触发条件
    public List<DangerTrigger> Triggers { get; set; } = new();
    
    // 后果
    public string Consequence { get; set; } = ""; // "high_damage", "debuff", "heal", etc.
    public int Severity { get; set; } = 1; // 1-10
    
    // 应对
    public List<string> Countermeasures { get; set; } = new();
    public int SuccessfulCounters { get; set; }
    public int FailedCounters { get; set; }
}

public class DangerTrigger
{
    public string Type { get; set; } = ""; // hp_percent, turn_number, buff_count
    public string Condition { get; set; } = ""; // "< 50%", "== 3"
    public int OccurrenceCount { get; set; }
}

// ==================== 综合战术档案 ====================

/// <summary>
/// 单个敌人的完整战术档案（Phase 1 + 2 + 3）
/// </summary>
public class CompleteEnemyProfile
{
    public string EnemyId { get; set; } = "";
    public string EnemyName { get; set; } = "";
    
    // Phase 1: 基础统计
    public EnemyStats Stats { get; set; } = new();
    public Dictionary<string, IntentStrategy> IntentStrategies { get; set; } = new();
    
    // Phase 2: 回合分析
    public List<TurnAnalysis> TurnAnalyses { get; set; } = new();
    
    // Phase 3: 高级战术
    public List<DangerPattern> DangerPatterns { get; set; } = new();
    public List<Phase3DiscoveredCombo> EffectiveCombos { get; set; } = new(); // 对这个敌人有效的连招
    public List<string> RecommendedPotions { get; set; } = new(); // 打这个敌人推荐带什么药水
    
    // 综合洞察
    public List<string> KeyInsights { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.Now;
}

/// <summary>
/// Phase 3 综合提示生成
/// </summary>
public class AdvancedTacticsHint
{
    public string? UrgentWarning { get; set; } // 最高优先级警告
    public string? ComboSuggestion { get; set; } // 推荐的连招
    public string? PotionAdvice { get; set; } // 药水使用建议
    public string? EnergyTip { get; set; } // 能量管理提示
    public List<string> DangerAlerts { get; set; } = new(); // 危险预警
    public List<string> OptimizationTips { get; set; } = new(); // 优化建议
}
