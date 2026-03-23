using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TokenSpire2.Llm;

/// <summary>
/// 运行时动态保守策略系统
/// 根据当前HP、地图状态、卡组强度等实时生成策略建议
/// </summary>
public static class ConservativeStrategy
{
    // ==================== HP 阈值配置 ====================
    
    /// <summary>HP > 80%: 可以安全打精英</summary>
    public const int HpSafeThreshold = 80;
    
    /// <summary>HP 50-80%: 谨慎，避免精英</summary>
    public const int HpCautionThreshold = 50;
    
    /// <summary>HP 30-50%: 危险，必须找恢复</summary>
    public const int HpDangerThreshold = 30;
    
    /// <summary>HP < 30%: 极度危险，生存优先</summary>
    public const int HpCriticalThreshold = 25;

    // ==================== HP 风险评估 ====================

    public enum HpRiskLevel
    {
        Safe,       // > 80%
        Caution,    // 50-80%
        Danger,     // 30-50%
        Critical    // < 30%
    }

    public static HpRiskLevel GetHpRiskLevel(int hpPercent)
    {
        return hpPercent switch
        {
            >= HpSafeThreshold => HpRiskLevel.Safe,
            >= HpCautionThreshold => HpRiskLevel.Caution,
            >= HpDangerThreshold => HpRiskLevel.Danger,
            _ => HpRiskLevel.Critical
        };
    }

    /// <summary>
    /// 获取基于HP的紧急警告（用于战斗提示）- 精简版
    /// </summary>
    public static string? GetCombatHpWarning(int hpPercent)
    {
        return GetHpRiskLevel(hpPercent) switch
        {
            HpRiskLevel.Critical => @"🚨 CRITICAL: HP<25%! SURVIVAL MODE!
- Block ALL damage (priority #1)
- Use potions NOW
- DO NOT attack if you can't survive",

            HpRiskLevel.Danger => @"⚠️ LOW HP: DEFENSIVE MODE
- Prioritize defense
- Use potions proactively
- Attack only with excess block",

            _ => null  // Safe/Caution - no warning needed
        };
    }

    /// <summary>
    /// 获取基于HP的地图路线策略（用于地图选择）
    /// </summary>
    /// <summary>
    /// 基于当前游戏状态生成动态策略建议（不预设固定规则）
    /// </summary>
    public static string GetMapStrategy(
        int hpPercent, 
        int distanceToRest, 
        int actNumber, 
        int consecutiveElites = 0, 
        bool lastWasElite = false,
        DeckStatus? deckStatus = null,
        int gold = 0)
    {
        var sb = new StringBuilder();
        var risk = GetHpRiskLevel(hpPercent);
        
        // 头部警告
        sb.AppendLine($"=== CURRENT STATE ===");
        sb.AppendLine($"HP: {hpPercent}% | Risk: {risk} | Gold: {gold}g");
        
        // 连续精英警告（这是生存必需的，不是策略选择）
        if (consecutiveElites >= 2)
        {
            sb.AppendLine("🚨 SURVIVAL: 2+ Elites fought! You likely need REST to survive.");
        }
        else if (lastWasElite && hpPercent < 50)
        {
            sb.AppendLine("⚠️ CAUTION: Elite just cost you HP. Consider recovery before next fight.");
        }
        
        sb.AppendLine();
        sb.AppendLine("=== DECISION FACTORS ===");
        
        // 基于当前状态的决策因子（让AI自己权衡）
        
        // 1. 血量因子
        sb.AppendLine($"Health: {(hpPercent < 30 ? "CRITICAL - Survival priority" : hpPercent < 50 ? "Low - Rest beneficial" : "Acceptable")}");
        
        // 2. 金币因子
        if (gold >= 100)
        {
            sb.AppendLine($"Gold: {gold}g - Shop viable for removal (~75g) or cards/relics");
        }
        else if (gold >= 50)
        {
            sb.AppendLine($"Gold: {gold}g - Limited shop options, maybe 1 removal or potion");
        }
        else
        {
            sb.AppendLine($"Gold: {gold}g - Need more gold before shopping");
        }
        
        // 3. 卡组状态因子
        if (deckStatus != null)
        {
            sb.AppendLine($"Deck: {deckStatus.CardCount} cards, {deckStatus.StrikeCount} Strikes, {deckStatus.DefendCount} Defends");
            if (deckStatus.StrikeCount > 4)
            {
                sb.AppendLine($"  Note: Many Strikes. Shop removal valuable if you have 75g");
            }
            if (deckStatus.CardCount > 25)
            {
                sb.AppendLine($"  Note: Large deck. Consider thinning at shop");
            }
        }
        
        // 4. 距离因子
        if (distanceToRest > 0)
        {
            sb.AppendLine($"Next Rest: {distanceToRest} rooms away");
            if (hpPercent < 40 && distanceToRest > 2)
            {
                sb.AppendLine($"  → Rest is far and HP low. Play safe.");
            }
        }
        
        sb.AppendLine();
        sb.AppendLine("=== ROOM TYPE EFFECTS ===");
        sb.AppendLine("Monster: +Cards/Gold, -HP (variable)");
        sb.AppendLine("Elite:   +Relic/Gold, -HP (high risk)");
        sb.AppendLine("Shop:    -Gold, +Cards/Removal/Potions (strategic investment)");
        sb.AppendLine("Rest:    +HP or +CardUpgrade (recovery)");
        sb.AppendLine("Event:   Variable outcomes (gamble/safe option)");
        
        sb.AppendLine();
        sb.AppendLine("Choose based on your CURRENT needs: HP recovery? Gold? Card removal? Relic?");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 卡组状态信息
    /// </summary>
    public class DeckStatus
    {
        public int CardCount { get; set; }
        public int StrikeCount { get; set; }
        public int DefendCount { get; set; }
    }

    /// <summary>
    /// 获取房间类型推荐（带风险标记）- 增强版，考虑连续战斗
    /// </summary>
    public static string GetRoomRiskLabel(string roomType, int hpPercent, int consecutiveFights = 0, int consecutiveElites = 0, int totalFights = 0, bool hasShopNearby = false)
    {
        var risk = GetHpRiskLevel(hpPercent);
        
        // 连续战斗强制警告
        if (consecutiveFights >= 2 && roomType.ToLower() == "monster")
        {
            return " [🚫 AVOID - You just fought 2+ battles! Consider Shop/Rest/Event]";
        }
        
        if (consecutiveElites >= 1 && roomType.ToLower() == "elite")
        {
            return " [🚫 FORBIDDEN - Just fought an Elite! MUST rest first!]";
        }
        
        if (totalFights >= 4 && roomType.ToLower() == "monster" && !hasShopNearby)
        {
            return " [⚠️ CAUTION - Many fights without shopping. Deck may be bloated]";
        }
        
        return roomType.ToLower() switch
        {
            "elite" => risk switch
            {
                >= HpRiskLevel.Caution => " [❌ AVOID - HP too low!]",
                HpRiskLevel.Safe => " [⚠️ Risky - Ensure you can survive]",
                _ => " [Unknown]"
            },
            
            "monster" => risk switch
            {
                HpRiskLevel.Critical => " [⚠️ Dangerous at low HP]",
                _ => " [Standard combat]"
            },
            
            "rest" => risk switch
            {
                HpRiskLevel.Danger => " [✅ HIGH PRIORITY - You need this!]",
                HpRiskLevel.Critical => " [✅ HIGH PRIORITY - You need this!]",
                HpRiskLevel.Caution => " [💛 Consider if no better options]",
                _ => " [Optional]"
            },
            
            "shop" => consecutiveFights >= 2 
                ? " [✅ RECOMMENDED - After 2+ fights, time to upgrade!]" 
                : risk switch
                {
                    <= HpRiskLevel.Danger => " [✅ Good - Can buy potions]",
                    _ => " [Standard]"
                },
            
            "event" => risk switch
            {
                HpRiskLevel.Critical => " [🎲 Gamble - Might heal or hurt]",
                _ => " [Varied outcomes]"
            },
            
            "boss" => " [⚠️ FINAL CHALLENGE - Prepare well!]",
            
            _ => ""
        };
    }

    /// <summary>
    /// 判断是否可以安全进入某种房间
    /// </summary>
    public static bool CanSafelyEnterRoom(string roomType, int hpPercent, int potionsAvailable, int deckStrength)
    {
        var risk = GetHpRiskLevel(hpPercent);
        
        return roomType.ToLower() switch
        {
            "elite" => risk switch
            {
                HpRiskLevel.Safe => true,                    // >80% 可以直接打
                HpRiskLevel.Caution => deckStrength >= 7,    // 需要强力卡组
                _ => false                                   // 低血量绝对不行
            },
            
            "monster" => risk switch
            {
                HpRiskLevel.Critical => false,               // <30% 危险
                _ => true
            },
            
            _ => true  // 其他房间通常都可以
        };
    }

    /// <summary>
    /// 获取连续战斗风险评估
    /// </summary>
    public static string GetConsecutiveFightWarning(int hpPercent, bool previousWasElite, bool nextIsElite)
    {
        if (previousWasElite && hpPercent < 60)
        {
            return "⚠️ WARNING: Just fought an elite and HP is low. REST before next fight!";
        }
        
        if (nextIsElite && hpPercent < 80)
        {
            return "💡 TIP: Elite coming up. Consider healing to 80%+ first.";
        }
        
        return "";
    }

    // ==================== 私有辅助方法 ====================

    private static string GetActSpecificAdvice(int act, int hpPercent)
    {
        var risk = GetHpRiskLevel(hpPercent);
        
        return act switch
        {
            1 => risk switch
            {
                HpRiskLevel.Safe => "Act 1: Can take 1-2 elites for early relics",
                HpRiskLevel.Caution => "Act 1: Be selective, take only 1 elite max",
                _ => "Act 1: SKIP ALL ELITES, focus on survival"
            },
            
            2 => risk switch
            {
                HpRiskLevel.Safe => "Act 2: Can take elites but plan rest before Boss",
                HpRiskLevel.Caution => "Act 2: Avoid elites, ensure rest before Boss",
                _ => "Act 2: SURVIVAL MODE - Rest is critical"
            },
            
            3 => risk switch
            {
                _ => "Act 3: Conserve HP for Final Boss - avoid unnecessary risks"
            },
            
            _ => ""
        };
    }

    // ==================== 配置加载支持 ====================
    
    private static StrategyConfig? _cachedConfig;
    
    /// <summary>
    /// 从配置文件加载策略配置（可选）
    /// </summary>
    public static StrategyConfig LoadConfig(string configPath)
    {
        try
        {
            if (System.IO.File.Exists(configPath))
            {
                var json = System.IO.File.ReadAllText(configPath);
                _cachedConfig = System.Text.Json.JsonSerializer.Deserialize<StrategyConfig>(json);
            }
        }
        catch (System.Exception)
        {
            // 使用默认配置
        }
        
        return _cachedConfig ?? new StrategyConfig();
    }
    
    /// <summary>
    /// 策略配置数据模型
    /// </summary>
    public class StrategyConfig
    {
        public HpThresholdsConfig HpThresholds { get; set; } = new();
        public RiskToleranceConfig RiskTolerance { get; set; } = new();
    }
    
    public class HpThresholdsConfig
    {
        public int Safe { get; set; } = 80;
        public int Caution { get; set; } = 50;
        public int Danger { get; set; } = 30;
        public int Critical { get; set; } = 25;
    }
    
    public class RiskToleranceConfig
    {
        public string Mode { get; set; } = "conservative";
    }
}
