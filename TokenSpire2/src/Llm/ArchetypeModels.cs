using System;
using System.Collections.Generic;
using System.Linq;

namespace TokenSpire2.Llm;

// ==================== 参考流派库模型 ====================

public class ArchetypeReference
{
    public string Description { get; set; } = "";
    public List<ReferenceArchetype> Archetypes { get; set; } = new();
    public Dictionary<string, string> CardNotes { get; set; } = new();
}

public class ReferenceArchetype
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "community"; // community, dev, wiki
    public string Tier { get; set; } = "B"; // S/A/B/C
    public KeyCards KeyCards { get; set; } = new();
    public RelicInfo Relics { get; set; } = new();
    public Dictionary<string, string> Strategy { get; set; } = new();
    public string Notes { get; set; } = "";
}

public class KeyCards
{
    public List<string> MustHave { get; set; } = new();
    public List<string> NiceToHave { get; set; } = new();
    public List<string> Support { get; set; } = new();
}

public class RelicInfo
{
    public List<string> Dream { get; set; } = new();
    public List<string> Good { get; set; } = new();
}

// ==================== AI经验库模型 ====================

public class AiExperience
{
    public string Description { get; set; } = "AI个人经验 - 基于实际对局的学习";
    public DateTime LastUpdated { get; set; } = DateTime.Now;
    public List<DiscoveredBuild> DiscoveredBuilds { get; set; } = new();
    public List<CardExperience> CardExperiences { get; set; } = new();
    public List<SurprisingSynergy> SurprisingSynergies { get; set; } = new();
    public Dictionary<string, ArchetypeCorrection> CorrectionsToReference { get; set; } = new();
    public List<DecisionLog> RecentDecisions { get; set; } = new();
}

public class DiscoveredBuild
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<string> DeckSnapshot { get; set; } = new();
    public List<string> KeyRelics { get; set; } = new();
    public BuildResult Result { get; set; } = new();
    public string? WhyItWorked { get; set; }
    public string? WhyItFailed { get; set; }
    public int SuccessCount { get; set; } = 1;
    public int AttemptCount { get; set; } = 1;
    public string Status { get; set; } = "experimental"; // experimental, promising, verified, deprecated
}

public class BuildResult
{
    public bool Victory { get; set; }
    public int Floor { get; set; }
    public string Character { get; set; } = "";
}

public class CardExperience
{
    public string CardId { get; set; } = "";
    public int PickedCount { get; set; } = 0;
    public int WinWhenPicked { get; set; } = 0;
    public float AvgFloorWhenPicked { get; set; } = 0;
    public List<string> Notes { get; set; } = new();
    public List<string> ContextsWhereGood { get; set; } = new();
    public List<string> ContextsWhereBad { get; set; } = new();
    
    public float WinRate => PickedCount > 0 ? (float)WinWhenPicked / PickedCount : 0;
}

public class SurprisingSynergy
{
    public List<string> Cards { get; set; } = new();
    public string Description { get; set; } = "";
    public DateTime DiscoveredInRun { get; set; }
    public int VerifiedCount { get; set; } = 1;
    public string Confidence { get; set; } = "low"; // low, medium, high
}

public class ArchetypeCorrection
{
    public List<string> AgreeWith { get; set; } = new();
    public List<string> DisagreeWith { get; set; } = new();
    public List<string> MyImprovements { get; set; } = new();
}

public class DecisionLog
{
    public int Floor { get; set; }
    public string Choice { get; set; } = "";
    public string Context { get; set; } = "";
    public string Result { get; set; } = "neutral"; // worked, regret, neutral
    public string? Reflection { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

// ==================== 运行时分析结果 ====================

public class ArchetypeMatchResult
{
    public string ArchetypeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = ""; // reference, discovered
    public float MatchScore { get; set; } = 0; // 0-100
    public int MustHaveOwned { get; set; } = 0;
    public int MustHaveTotal { get; set; } = 0;
    public int NiceToHaveOwned { get; set; } = 0;
    public List<string> MissingKeyCards { get; set; } = new();
    public List<string> AntiSynergyCards { get; set; } = new();
    public string? StrategyHint { get; set; }
}

public class CardEvaluation
{
    public string CardId { get; set; } = "";
    public float BaseScore { get; set; } = 5; // 基础质量分 0-10
    public float ReferenceScore { get; set; } = 0; // 参考流派加分
    public float ExperienceScore { get; set; } = 0; // 个人经验加分
    public float SynergyScore { get; set; } = 0; // 协同加分
    public float TotalScore => BaseScore + ReferenceScore + ExperienceScore + SynergyScore;
    
    public List<string> MatchingArchetypes { get; set; } = new();
    public List<SurprisingSynergy> RelevantSynergies { get; set; } = new();
    public CardExperience? PersonalExperience { get; set; }
    public string Recommendation { get; set; } = "一般"; // 强烈推荐/推荐/可选/一般/不推荐
    public string? Reasoning { get; set; }
}

public class DeckAnalysis
{
    public int TotalCards { get; set; }
    public int AttackCount { get; set; }
    public int SkillCount { get; set; }
    public int PowerCount { get; set; }
    public List<string> KeyCards { get; set; } = new();
    public List<ArchetypeMatchResult> ArchetypeMatches { get; set; } = new();
    public List<DiscoveredBuild> SimilarDiscoveredBuilds { get; set; } = new();
    public ArchetypeMatchResult? PrimaryArchetype => ArchetypeMatches.FirstOrDefault();
}
