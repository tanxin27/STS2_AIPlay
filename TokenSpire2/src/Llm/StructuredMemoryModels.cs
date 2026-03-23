using System;
using System.Collections.Generic;
using System.Linq;

namespace TokenSpire2.Llm;

// ==================== 结构化全局记忆 ====================

public class StructuredGlobalMemory
{
    public string Version { get; set; } = "1.0";
    public DateTime LastUpdated { get; set; } = DateTime.Now;
    public string Description { get; set; } = "结构化全局记忆 - 跨角色通用知识";
    
    public Dictionary<string, EnemyKnowledge> EnemyKnowledge { get; set; } = new();
    public Dictionary<string, EventKnowledge> EventKnowledge { get; set; } = new();
    public MapStrategyKnowledge MapStrategies { get; set; } = new();
    public List<KeyLesson> UniversalLessons { get; set; } = new();
}

public class EnemyKnowledge
{
    public string EnemyId { get; set; } = "";
    public string EnemyName { get; set; } = "";
    public string DangerLevel { get; set; } = "medium"; // low, medium, high, extreme
    public List<string> KeyPatterns { get; set; } = new();
    public List<string> RecommendedStrategies { get; set; } = new();
    public EnemyStats Stats { get; set; } = new();
    public List<string> KeyInsights { get; set; } = new();
    public DateTime LastEncountered { get; set; }
}

public class EnemyStats
{
    public int Encounters { get; set; } = 0;
    public int Wins { get; set; } = 0;
    public int Losses => Encounters - Wins;
    public float WinRate => Encounters > 0 ? (float)Wins / Encounters : 0;
    public float AvgHpLost { get; set; } = 0;
    public int BestPerformanceFloor { get; set; } = 0; // 打得最好的一次到达层数
    public Dictionary<string, int> DeathCountByFloor { get; set; } = new(); // 在哪层死过
}

public class EventKnowledge
{
    public string EventId { get; set; } = "";
    public string EventName { get; set; } = "";
    public Dictionary<string, EventChoiceData> Choices { get; set; } = new();
    public string MyExperience { get; set; } = "";
    public int TimesEncountered { get; set; } = 0;
}

public class EventChoiceData
{
    public string ChoiceId { get; set; } = "";
    public string Description { get; set; } = "";
    public int TimesChosen { get; set; } = 0;
    public int TimesSucceeded { get; set; } = 0;
    public float SuccessRate => TimesChosen > 0 ? (float)TimesSucceeded / TimesChosen : 0;
    public List<string> TypicalOutcomes { get; set; } = new();
}

public class MapStrategyKnowledge
{
    public Dictionary<string, ActStrategy> ActStrategies { get; set; } = new();
    public PathRiskThresholds RiskThresholds { get; set; } = new();
}

public class ActStrategy
{
    public int Act { get; set; }
    public string EliteRiskThreshold { get; set; } = "hp > 60%"; // 自然语言规则
    public float ShopPriority { get; set; } = 0.5f; // 0-1
    public List<string> PathPreferences { get; set; } = new();
    public string KeyInsight { get; set; } = "";
}

public class PathRiskThresholds
{
    public float Act1EliteHpPercent { get; set; } = 0.6f;
    public float Act2EliteHpPercent { get; set; } = 0.7f;
    public float Act3EliteHpPercent { get; set; } = 0.75f;
    public int MinHpForEliteAct1 { get; set; } = 35;
    public int MinHpForEliteAct2 { get; set; } = 50;
    public int MinHpForEliteAct3 { get; set; } = 60;
}

public class KeyLesson
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Lesson { get; set; } = "";
    public float Confidence { get; set; } = 0.5f; // 0-1
    public List<int> SourceRuns { get; set; } = new(); // 来自哪些对局
    public bool Verified { get; set; } = false;
    public DateTime DiscoveredAt { get; set; } = DateTime.Now;
    public string Category { get; set; } = "general"; // combat, path, deck_building
}

// ==================== 结构化角色记忆 ====================

public class StructuredCharacterMemory
{
    public string Version { get; set; } = "1.0";
    public string Character { get; set; } = "";
    public DateTime LastUpdated { get; set; } = DateTime.Now;
    public string Description { get; set; } = "结构化角色记忆 - 角色专属知识";
    
    public Dictionary<string, StructuredCardEvaluation> CardEvaluations { get; set; } = new();
    public Dictionary<string, ArchetypePerformance> ArchetypePerformance { get; set; } = new();
    public List<KeyLesson> CharacterLessons { get; set; } = new();
    public List<DiscoveredCombo> DiscoveredCombos { get; set; } = new();
    public RelicPriorityKnowledge RelicPriorities { get; set; } = new();
}

public class StructuredCardEvaluation
{
    public string CardId { get; set; } = "";
    public string CardName { get; set; } = "";
    public float BaseRating { get; set; } = 5f; // 0-10
    
    // 在不同情境下的评分
    public Dictionary<string, float> ContextRatings { get; set; } = new();
    
    // 统计数据
    public CardPickHistory PickHistory { get; set; } = new();
    
    // 核心洞察
    public string KeyInsight { get; set; } = "";
    public List<string> Synergies { get; set; } = new();
    public List<string> AntiSynergies { get; set; } = new();
    
    // 升级优先级
    public string UpgradePriority { get; set; } = "low"; // low, medium, high, essential
}

public class CardPickHistory
{
    public int TimesPicked { get; set; } = 0;
    public int TimesSkipped { get; set; } = 0;
    public int WinsWhenPicked { get; set; } = 0;
    public float AvgFloorWhenPicked { get; set; } = 0;
    public float AvgFloorWhenSkipped { get; set; } = 0;
    
    public float PickRate => TimesPicked + TimesSkipped > 0 
        ? (float)TimesPicked / (TimesPicked + TimesSkipped) 
        : 0;
    public float WinRateWhenPicked => TimesPicked > 0 
        ? (float)WinsWhenPicked / TimesPicked 
        : 0;
}

public class ArchetypePerformance
{
    public string ArchetypeId { get; set; } = "";
    public string ArchetypeName { get; set; } = "";
    public int Attempts { get; set; } = 0;
    public int Wins { get; set; } = 0;
    public float WinRate => Attempts > 0 ? (float)Wins / Attempts : 0;
    public float AvgFloor { get; set; } = 0;
    public int BestFloor { get; set; } = 0;
    public List<string> KeyCards { get; set; } = new();
    public string Evolution { get; set; } = ""; // AI对这个流派的认知演进
    public float CurrentConfidence { get; set; } = 0.5f; // 目前对这个流派的信心
}

public class DiscoveredCombo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public List<string> Cards { get; set; } = new();
    public List<string> Relics { get; set; } = new();
    public string Description { get; set; } = "";
    public int TimesExecuted { get; set; } = 0;
    public int TimesSucceeded { get; set; } = 0;
    public float SuccessRate => TimesExecuted > 0 ? (float)TimesSucceeded / TimesExecuted : 0;
    public DateTime DiscoveredAt { get; set; } = DateTime.Now;
    public bool Verified { get; set; } = false;
}

public class RelicPriorityKnowledge
{
    public Dictionary<string, RelicEvaluation> RelicEvaluations { get; set; } = new();
    public List<string> DreamRelics { get; set; } = new(); // 看到这个必买
    public List<string> SkipRelics { get; set; } = new(); // 通常不买
}

public class RelicEvaluation
{
    public string RelicId { get; set; } = "";
    public float Rating { get; set; } = 5f;
    public int TimesObtained { get; set; } = 0;
    public int WinsWithRelic { get; set; } = 0;
    public string KeyInsight { get; set; } = "";
    public List<string> Synergies { get; set; } = new();
}

// ==================== 决策追踪模型 ====================

public class DecisionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DecisionType Type { get; set; }
    public int Floor { get; set; }
    public int Act { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Character { get; set; } = "";
    
    // 情境
    public GameSnapshot Context { get; set; } = new();
    public List<string> Options { get; set; } = new();
    public string Chosen { get; set; } = "";
    public string LlmReasoning { get; set; } = "";
    
    // 结果（后续填充）
    public DecisionOutcome? Outcome { get; set; }
}

public enum DecisionType
{
    CardPick,
    PathChoice,
    CombatAction,
    ShopPurchase,
    EventChoice,
    RestSiteChoice,
    BossRelicChoice,
    CardRemoval,
    CardUpgrade
}

public class GameSnapshot
{
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Gold { get; set; }
    public List<string> Deck { get; set; } = new();
    public List<string> Relics { get; set; } = new();
    public List<string> Potions { get; set; } = new();
    public string? CurrentEnemy { get; set; }
    public string? CurrentArchetype { get; set; }
}

public class DecisionOutcome
{
    public int FloorReached { get; set; }
    public bool EventuallyWon { get; set; }
    public string OutcomeType { get; set; } = "neutral"; // led_to_victory, neutral, led_to_death
    public string ShortTermResult { get; set; } = "";
    public int HpChange5Floors { get; set; } = 0;
    public string? Analysis { get; set; }
    public bool? WasGoodDecision { get; set; }
    public DateTime? AnalyzedAt { get; set; }
}

// ==================== 死因分析模型 ====================

public class DeathAnalysis
{
    public string RunId { get; set; } = "";
    public int DeathFloor { get; set; }
    public string ImmediateCause { get; set; } = ""; // 立即死因
    public string KilledBy { get; set; } = "";
    
    public List<DeathFactor> Factors { get; set; } = new();
    public List<string> KeyMistakes { get; set; } = new();
    public List<DecisionRecord> CriticalDecisions { get; set; } = new();
    public List<ActionableAdvice> ActionableAdvice { get; set; } = new();
    
    public string Summary { get; set; } = "";
    public DateTime AnalyzedAt { get; set; } = DateTime.Now;
}

public class DeathFactor
{
    public string Category { get; set; } = ""; // deck, path, combat, rng
    public string Description { get; set; } = "";
    public float Severity { get; set; } = 0.5f; // 0-1
    public bool Preventable { get; set; } = true;
}

public class ActionableAdvice
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Advice { get; set; } = "";
    public string TriggerCondition { get; set; } = ""; // 什么情况下应用这个建议
    public float Confidence { get; set; } = 0.5f;
    public string Category { get; set; } = "";
}

// ==================== 对局摘要模型 ====================

public class RunSummary
{
    public string RunId { get; set; } = Guid.NewGuid().ToString();
    public string Character { get; set; } = "";
    public bool Victory { get; set; }
    public int Floor { get; set; }
    public string? KilledBy { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public int DurationMinutes { get; set; }
    
    // 最终状态
    public List<string> FinalDeck { get; set; } = new();
    public List<string> FinalRelics { get; set; } = new();
    public int FinalHp { get; set; }
    public int FinalMaxHp { get; set; }
    public int FinalGold { get; set; }
    
    // 关键统计
    public int EliteFights { get; set; } = 0;
    public int RestSitesUsed { get; set; } = 0;
    public int CardsRemoved { get; set; } = 0;
    public int CardsUpgraded { get; set; } = 0;
    public int ShopVisits { get; set; } = 0;
    
    // 主要流派
    public string? PrimaryArchetype { get; set; }
    public float ArchetypeConfidence { get; set; } = 0;
    
    // 相关决策
    public List<string> DecisionIds { get; set; } = new();
}
